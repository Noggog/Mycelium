using System.Collections.Concurrent;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// One release out of an artist's Deezer catalog, in the terms the single audit reasons about: what
/// kind of release it is, when it came out, and the id its track listing is reachable by. Everything
/// else about the row — ownership, pressing, credited act — is somebody else's question.
/// </summary>
public record SingleAuditRelease(long DeezerAlbumId, string Title, string? RecordType, DateOnly? ReleaseDate);

/// <summary>
/// Decides which of an artist's singles are records in their own right rather than trailers for one,
/// and so may be offered in the Discover feed.
///
/// <para><b>Why singles are withheld by default.</b> The feed pushes LPs and EPs only
/// (<see cref="AlbumRecordType.IsFeedEligible"/>) because the usual single is a pre-release cut of a
/// song that turns up on an album weeks later. Offer them unaudited and the feed fills with rows that
/// are about to become redundant, each of which costs a download slot and a card the user has to
/// dismiss. But some singles never join an album — a one-off, a soundtrack cut, a charity release —
/// and those are the only way to own the song at all, so a blanket ban loses real records.</para>
///
/// <para><b>The two tests.</b> A single is standalone when:</para>
/// <list type="number">
/// <item>No album or EP in the artist's catalog holds any of its songs. This is asked of every
/// release the catalog lists — owned and missing alike, and every pressing, since the deluxe edition
/// is exactly where a stray single tends to reappear.</item>
/// <item>An album or EP has come out since (or the single has aged past
/// <see cref="FollowUpGrace"/>). Test 1 alone would clear a single released this morning, because the
/// album that will swallow it does not exist yet. Waiting for the next record and then finding the
/// song absent from it is what turns "not on an album" into "not going to be on an album".</item>
/// </list>
///
/// <para><b>Why the grace period.</b> Test 2 on its own never clears the single by an artist who has
/// gone quiet, or who simply hasn't made another record — which is a large share of exactly the
/// standalone releases this class exists to find. After <see cref="FollowUpGrace"/> with no album at
/// all, the silence is the evidence.</para>
///
/// <para><b>Why the verdict is persisted rather than asked at feed time.</b> Same reason as
/// <see cref="UpgradeAvailability"/>: the questions cost a Deezer call per release, and the feed is
/// served in front of a user. The sweep pays for them once into a memo
/// (<see cref="IDeezerAlbumTrackRepo"/>) and writes the answer onto the missing-album row, so
/// <see cref="MissingAlbum.IsFeedEligible"/> is a field read.</para>
///
/// <para><b>Why track listings come from Deezer rather than the library.</b> The library's own listing
/// is free and already synced, but the comparison would then be Plex titles against Deezer titles —
/// the cross-source drift that <see cref="AlbumTitleMatcher"/> exists to paper over, applied to song
/// titles, where there is no owned/missing verdict to sanity-check the result against. Both sides
/// coming from Deezer means one naming convention, and the memo makes it a one-time cost.</para>
/// </summary>
public class StandaloneSingleAuditor
{
    /// <summary>
    /// How long a single waits for an album that never comes before the absence is taken as the
    /// answer. Two years: long enough to outlast the ordinary album cycle (a single trailing a record
    /// by more than a year is rare), short enough that a genuine one-off isn't buried for a decade.
    /// </summary>
    public static readonly TimeSpan FollowUpGrace = TimeSpan.FromDays(365 * 2);

    /// <summary>
    /// How many track listings to have in flight at once. Matched to the missing-album sweep's album
    /// lookups for the same reason: <c>DeezerApi</c> paces every call through its own rolling window,
    /// so this can't outrun the ceiling — it only stops the walk paying a round trip's latency per
    /// release on top of the pacing.
    /// </summary>
    private const int FetchConcurrency = 8;

    private readonly IDeezerApi _deezer;
    private readonly IDeezerAlbumTrackRepo _tracks;
    private readonly ILogger<StandaloneSingleAuditor> _logger;

    public StandaloneSingleAuditor(
        IDeezerApi deezer,
        IDeezerAlbumTrackRepo tracks,
        ILogger<StandaloneSingleAuditor> logger)
    {
        _deezer = deezer;
        _tracks = tracks;
        _logger = logger;
    }

    /// <summary>
    /// The ids of the singles among <paramref name="releases"/> that pass the audit. Pass the artist's
    /// <em>whole</em> catalog, owned rows and alternate pressings included: an album only counts as
    /// holding a song if it is actually looked at, and the ones the library already has are the most
    /// likely to hold it.
    ///
    /// <para><paramref name="learn"/> bounds what this is willing to fetch, the same line
    /// <c>MissingAlbumRefresher</c>'s artist resolution draws. True — the nightly sweep — fetches the
    /// listings it doesn't have and fills the memo for everyone after. False — a drill-down, in front
    /// of a click — reads the memo and nothing else, so a release the sweep hasn't reached is simply
    /// unproven, and an unproven single is withheld. That is the pre-existing behaviour, and the next
    /// sweep promotes it.</para>
    /// </summary>
    public async Task<HashSet<long>> Audit(IReadOnlyList<SingleAuditRelease> releases, bool learn)
    {
        var records = releases.Where(r => AlbumRecordType.IsFeedEligible(r.RecordType)).ToList();
        var singles = releases.Where(r => AlbumRecordType.IsSingle(r.RecordType)).ToList();
        if (singles.Count == 0)
        {
            return new HashSet<long>();
        }

        // Test 2 and the title-track check first, because they need no track listings at all. An artist
        // whose singles are all too recent to judge costs zero Deezer calls — which is most artists most
        // nights, and the difference between this audit being affordable on a nightly sweep and not.
        var recordTitles = records
            .Select(r => AlbumTitleMatcher.NormalizeRecord(r.Title))
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var cutoff = DateOnly.FromDateTime((DateTimeOffset.UtcNow - FollowUpGrace).UtcDateTime);

        var candidates = singles
            .Where(s => FollowedUp(s, records, cutoff) && !IsTitleTrack(s, recordTitles))
            .ToList();
        if (candidates.Count == 0)
        {
            return new HashSet<long>();
        }

        // Every album/EP's songs, plus the candidates' own. Both sides are needed together: a candidate
        // with no listing can't be cleared, and a record with no listing silently stops covering
        // anything it holds, so an unanswered call has to read as "unproven" rather than "uncovered".
        var listings = await Listings(
            records.Select(r => r.DeezerAlbumId).Concat(candidates.Select(c => c.DeezerAlbumId)).ToList(),
            learn);

        // Test 1 is a claim about every record the artist has, so it can only be made when every record
        // has been read. One listing missing — a rate-limit blip on the sweep, a release the sweep
        // hasn't reached yet on a drill-down — and the album that holds the song may be exactly the one
        // we couldn't see. Judging against the rest would turn a quota blip into a feed full of teasers,
        // so the artist's singles wait for a pass that reads the whole catalog.
        var unproven = records.Where(r => !listings.ContainsKey(r.DeezerAlbumId)).ToList();
        if (unproven.Count > 0)
        {
            _logger.LogDebug(
                "Single audit: {Unproven} of {Total} album/EP track listing(s) unavailable; "
                + "withholding {Candidates} candidate single(s) until a pass reads them all",
                unproven.Count, records.Count, candidates.Count);
            return new HashSet<long>();
        }

        var covered = records
            .SelectMany(r => listings[r.DeezerAlbumId])
            .Select(AlbumTitleMatcher.NormalizeTrack)
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var standalone = new HashSet<long>();
        foreach (var single in candidates)
        {
            // No listing is no evidence. A single we can't see inside of is withheld, not cleared —
            // the whole audit turns on knowing what songs it is selling.
            if (!listings.TryGetValue(single.DeezerAlbumId, out var songs) || songs.Count == 0)
            {
                continue;
            }

            // Every song, not just the lead. A "single" carrying one new track and two cuts off the
            // album is a companion release to that album, and offering it would buy the album's songs
            // a second time.
            if (songs.Select(AlbumTitleMatcher.NormalizeTrack).Any(t => t.Length > 0 && covered.Contains(t)))
            {
                continue;
            }

            standalone.Add(single.DeezerAlbumId);
        }

        return standalone;
    }

    /// <summary>
    /// Test 2: an album or EP has come out since this single, or the single has outlived
    /// <see cref="FollowUpGrace"/> waiting for one. A single Deezer gave no full date for fails both —
    /// there is no way to place it against the records around it, and guessing from the year would put
    /// a January single behind a November album on nothing but sort order.
    ///
    /// <para>"Since" includes the same day. By the time this is asked the single's songs are already
    /// known to be absent from that record, and a single released alongside an album it is not on is a
    /// companion release, not a trailer for one.</para>
    /// </summary>
    private static bool FollowedUp(
        SingleAuditRelease single, IReadOnlyList<SingleAuditRelease> records, DateOnly cutoff)
    {
        if (single.ReleaseDate is not { } released)
        {
            return false;
        }

        return released <= cutoff
               || records.Any(r => r.ReleaseDate is { } date && date >= released);
    }

    /// <summary>
    /// Whether the single is named after a record the artist has — the classic pre-release title track,
    /// which test 1 would also catch, but only once the album's track listing has been fetched. Catching
    /// it on the title alone keeps the commonest teaser of all from costing a Deezer call.
    /// </summary>
    private static bool IsTitleTrack(SingleAuditRelease single, HashSet<string> recordTitles)
    {
        var title = AlbumTitleMatcher.NormalizeRecord(single.Title);
        return title.Length > 0 && recordTitles.Contains(title);
    }

    /// <summary>
    /// Track listings for these releases: everything the memo already holds, plus — when
    /// <paramref name="learn"/> — a fetch for the rest, written back for everyone after. An id that
    /// stays unanswered is absent from the result rather than present and empty, so callers can tell
    /// "holds no such song" from "we don't know what it holds".
    /// </summary>
    private async Task<Dictionary<long, IReadOnlyList<string>>> Listings(
        IReadOnlyList<long> albumIds, bool learn)
    {
        var ids = albumIds.Distinct().ToList();
        var known = await _tracks.Get(ids);
        if (!learn)
        {
            return known;
        }

        var unknown = ids.Where(id => !known.ContainsKey(id)).ToList();
        if (unknown.Count == 0)
        {
            return known;
        }

        var learned = new ConcurrentDictionary<long, IReadOnlyList<string>>();
        await Parallel.ForEachAsync(
            unknown,
            new ParallelOptions { MaxDegreeOfParallelism = FetchConcurrency },
            async (id, _) =>
            {
                // Only an answer is recorded. GetAlbumTracks returns empty for a failed call as well as
                // a real miss, and a release out of a discography listing has tracks by construction —
                // so an empty result is a blip, and memoising it would teach the audit for good that
                // this album holds nothing.
                var titles = (await _deezer.GetAlbumTracks(id))
                    .Select(t => t.title)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t!)
                    .ToList();
                if (titles.Count > 0)
                {
                    learned[id] = titles;
                }
            });

        if (!learned.IsEmpty)
        {
            await _tracks.Put(learned.ToDictionary(e => e.Key, e => e.Value));
            foreach (var (id, titles) in learned)
            {
                known[id] = titles;
            }
        }

        return known;
    }
}
