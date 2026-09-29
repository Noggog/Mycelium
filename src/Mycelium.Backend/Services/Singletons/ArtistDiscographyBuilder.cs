using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Mycelium.Deezer.Models;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;
using Mycelium.ListenBrainz.Models;
using Mycelium.ListenBrainz.Services;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>A point-in-time view of a discography pass.</summary>
/// <param name="Unreachable">Artists skipped because MusicBrainz or Deezer didn't answer. Picked up by the next pass.</param>
public record DiscographyPassStatus(
    bool Running,
    int Processed,
    int Total,
    int Unreachable,
    int Errors,
    string? CurrentArtist,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>
/// How well MusicBrainz release groups match Deezer across the library: the Phase 2 report of
/// <c>MUSICBRAINZ-IDENTITY.md</c>. "Core" release groups are official albums and EPs with no secondary
/// type (<see cref="DiscographyReleaseGroup.IsCore"/>); the rates that matter are theirs.
/// </summary>
/// <param name="Artists">Resolved MusicBrainz artists in the library, each counted once.</param>
/// <param name="Built">Of those, how many have a stored discography.</param>
/// <param name="CoreWithHighEdition">Core groups with at least one edition found by link or barcode.</param>
/// <param name="CoreWithLowEditionOnly">Core groups whose only editions were found by title.</param>
/// <param name="DeezerAlbums">Deezer albums placed or left over, across every built discography.</param>
/// <param name="EditionsByMethod">Placed Deezer albums per <see cref="EditionMatchMethod"/>, by name.</param>
/// <param name="TitleDisagrees">
/// Link or barcode matches demoted to low confidence because the titles share nothing
/// (<see cref="DeezerEdition.TitleDisagrees"/>). Counted in <paramref name="DeezerLow"/> too.
/// </param>
public record DiscographyReport(
    int Artists,
    int Built,
    int ReleaseGroups,
    int CoreReleaseGroups,
    int CoreWithHighEdition,
    int CoreWithLowEditionOnly,
    int CoreWithoutEdition,
    int DeezerAlbums,
    int DeezerHigh,
    int DeezerLow,
    int DeezerUnmatched,
    IReadOnlyDictionary<string, int> EditionsByMethod,
    int TitleDisagrees,
    OwnedAlbumReport Owned,
    DiscographyPassStatus Pass);

/// <summary>How the library's own albums matched release groups, across every built discography.</summary>
/// <param name="Albums">Owned albums by settled artists that have a discography.</param>
/// <param name="Earlier">Nothing fitted, but the older backfill's release group is on the discography and was kept.</param>
/// <param name="Manual">Decided by a person, including albums they said aren't on MusicBrainz.</param>
/// <param name="Tied">Unmatched because the title fits several release groups; a person picks.</param>
/// <param name="DeezerLink">Found through MusicBrainz's link to the Deezer album of the same title.</param>
public record OwnedAlbumReport(
    int Albums, int Title, int TitleFuzzy, int Earlier, int Unmatched, int Manual = 0, int Tied = 0, int DeezerLink = 0)
{
    public static OwnedAlbumReport Of(IEnumerable<OwnedAlbumMatch> owned)
    {
        var all = owned.ToList();
        return new OwnedAlbumReport(
            all.Count,
            all.Count(o => o.Method == OwnedAlbumMatchMethod.Title),
            all.Count(o => o.Method == OwnedAlbumMatchMethod.TitleFuzzy),
            all.Count(o => o.Method == OwnedAlbumMatchMethod.Earlier),
            all.Count(o => o.ReleaseGroup is null),
            all.Count(o => o.Method == OwnedAlbumMatchMethod.Manual),
            all.Count(o => o.Candidates is { Count: > 0 }),
            all.Count(o => o.Method == OwnedAlbumMatchMethod.DeezerLink));
    }
}

/// <summary>One built discography in brief, for finding the artists whose matching went worst.</summary>
public record DiscographySummary(
    string Mbid,
    string? Name,
    DateTimeOffset BuiltAt,
    int ReleaseGroups,
    int CoreReleaseGroups,
    int CoreWithHighEdition,
    int CoreWithLowEditionOnly,
    int CoreWithoutEdition,
    int DeezerHigh,
    int DeezerLow,
    int DeezerUnmatched,
    int TitleDisagrees,
    int OwnedAlbums,
    int OwnedUnmatched)
{
    public static DiscographySummary Of(ArtistDiscography d)
    {
        var core = d.ReleaseGroups.Where(g => g.IsCore).ToList();
        var editions = d.ReleaseGroups.SelectMany(g => g.Editions).ToList();
        return new DiscographySummary(
            d.Mbid,
            d.Name,
            d.BuiltAt,
            d.ReleaseGroups.Count,
            core.Count,
            core.Count(g => g.Editions.Any(e => e.Confidence == EditionConfidence.High)),
            core.Count(g => g.Editions.Count > 0 && g.Editions.All(e => e.Confidence == EditionConfidence.Low)),
            core.Count(g => g.Editions.Count == 0),
            editions.Count(e => e.Confidence == EditionConfidence.High),
            editions.Count(e => e.Confidence == EditionConfidence.Low),
            d.UnmatchedDeezer.Count,
            editions.Count(e => e.TitleDisagrees),
            d.Owned?.Count ?? 0,
            d.Owned?.Count(o => o.ReleaseGroup is null) ?? 0);
    }
}

/// <summary>
/// One MusicBrainz artist to build a discography for, and the Deezer pages whose albums go on it.
/// Several library artists can resolve to the same MusicBrainz artist; they share one discography.
/// </summary>
/// <param name="LibraryArtists">The library artists resolved to it, whose albums are matched against it.</param>
public record DiscographyTarget(
    string Mbid,
    string Name,
    IReadOnlyList<DeezerIdentity> Deezer,
    IReadOnlyList<DiscographyLibraryArtist> LibraryArtists);

/// <summary>A library artist by name, and by Plex artist when the name is shared (see <see cref="LibraryArtist"/>).</summary>
public record DiscographyLibraryArtist(string Name, int? PlexArtistKey);

/// <summary>
/// Builds and stores each resolved artist's discography (<see cref="ArtistDiscography"/>): MusicBrainz's
/// release groups, with the Deezer albums that are editions of them. Each build also records which
/// release group every owned album by the artist is (<see cref="OwnedAlbumMatcher"/>), in the catalog's
/// <c>albumIdentities</c>, which the metadata archive reads. Nothing else reads discographies yet: Browse,
/// Discover and downloads still run on names and Deezer until later phases switch them over.
///
/// <para><b>Sources.</b> Release groups and releases come through <see cref="SourceCache"/>, under the same
/// keys the identity check fills, so the first build of an already-checked artist costs no MusicBrainz
/// requests. The Deezer side is the artist's own listing plus what album search adds (the listing leaves
/// albums out), fetched fresh each build, as the nightly missing-album diff does. Barcode lookups and the
/// old album ids MusicBrainz links to are cached: neither changes much.</para>
///
/// <para><b>Groups credited elsewhere.</b> The release-group browse only finds groups credited to the
/// artist. A release credited to the artist can sit in a group credited to an earlier band name or a
/// project (<i>Campfire Songs</i>), so the groups of the artist's own releases are added too
/// (<see cref="WithReleaseGroups"/>). An owned album that still fits nothing is looked up by the Deezer
/// album of the same title: when MusicBrainz links that album to a release, the album is that release's
/// group, whoever it is credited to (<see cref="FollowDeezerLinks"/>).</para>
///
/// <para><b>Lifetime.</b> An artist who released something in the last two years is rebuilt about weekly,
/// anyone else every 60 to 90 days, with random spread so rebuilds don't all fall due together. A rebuild
/// that is due asks MusicBrainz again rather than reusing what is cached.</para>
///
/// <para><b>No answer, no discography.</b> When a source doesn't answer part of it, nothing is stored for
/// the artist: a discography missing half its editions would read as Deezer not having them.</para>
/// </summary>
public partial class ArtistDiscographyBuilder
{
    private static readonly TimeSpan ActiveLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan ActiveSpread = TimeSpan.FromDays(1);
    private static readonly TimeSpan QuietMin = TimeSpan.FromDays(60);
    private static readonly TimeSpan QuietMax = TimeSpan.FromDays(90);

    /// <summary>How recent the latest release must be for an artist to count as still releasing.</summary>
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromDays(730);

    /// <summary>A barcode Deezer has an album for: stable, and re-asked rarely.</summary>
    private static readonly TimeSpan BarcodeFoundLifetime = TimeSpan.FromDays(90);

    /// <summary>A barcode Deezer has nothing for: its catalogue grows, so asked again sooner.</summary>
    private static readonly TimeSpan BarcodeMissingLifetime = TimeSpan.FromDays(30);

    private static readonly TimeSpan DeezerAlbumLifetime = TimeSpan.FromDays(90);

    /// <summary>A Deezer album MusicBrainz links to a release, and that release: stable.</summary>
    private static readonly TimeSpan LinkFoundLifetime = TimeSpan.FromDays(90);

    /// <summary>A Deezer album MusicBrainz doesn't know yet: someone may import it, so asked again sooner.</summary>
    private static readonly TimeSpan LinkMissingLifetime = TimeSpan.FromDays(7);

    private readonly IMusicBrainzApi _musicBrainz;
    private readonly IDeezerApi _deezer;
    private readonly SourceCache _cache;
    private readonly IArtistCatalogRepo _catalog;
    private readonly IArtistResolutionRepo _resolutions;
    private readonly IArtistDiscographyRepo _discographies;
    private readonly ILogger<ArtistDiscographyBuilder> _logger;
    private readonly TimeProvider _time;

    private readonly object _gate = new();
    private Task? _run;
    private bool _running;
    private int _processed, _total, _unreachable, _errors;
    private string? _currentArtist;
    private DateTimeOffset? _startedAt, _finishedAt;

    public ArtistDiscographyBuilder(
        IMusicBrainzApi musicBrainz,
        IDeezerApi deezer,
        SourceCache cache,
        IArtistCatalogRepo catalog,
        IArtistResolutionRepo resolutions,
        IArtistDiscographyRepo discographies,
        ILogger<ArtistDiscographyBuilder> logger)
        : this(musicBrainz, deezer, cache, catalog, resolutions, discographies, logger, TimeProvider.System)
    {
    }

    internal ArtistDiscographyBuilder(
        IMusicBrainzApi musicBrainz,
        IDeezerApi deezer,
        SourceCache cache,
        IArtistCatalogRepo catalog,
        IArtistResolutionRepo resolutions,
        IArtistDiscographyRepo discographies,
        ILogger<ArtistDiscographyBuilder> logger,
        TimeProvider time)
    {
        _musicBrainz = musicBrainz;
        _deezer = deezer;
        _cache = cache;
        _catalog = catalog;
        _resolutions = resolutions;
        _discographies = discographies;
        _logger = logger;
        _time = time;
    }

    /// <summary>
    /// Starts a pass unless one is running, then returns the live status. <paramref name="all"/> rebuilds
    /// every artist; otherwise only those never built or due. <paramref name="count"/> caps how many.
    /// </summary>
    public DiscographyPassStatus Start(bool all, int? count)
    {
        _ = RunPass(all, count);
        return GetStatus();
    }

    public DiscographyPassStatus GetStatus()
    {
        lock (_gate)
        {
            return new DiscographyPassStatus(
                _running, _processed, _total, _unreachable, _errors, _currentArtist, _startedAt, _finishedAt);
        }
    }

    /// <summary>A pass, or the one already running. What the background service awaits.</summary>
    public Task RunPass(bool all, int? count = null)
    {
        lock (_gate)
        {
            if (!_running)
            {
                _running = true;
                _processed = _total = _unreachable = _errors = 0;
                _currentArtist = null;
                _startedAt = _time.GetUtcNow();
                _finishedAt = null;
                _run = Task.Run(() => RunAsync(all, count));
            }
            return _run!;
        }
    }

    private async Task RunAsync(bool all, int? count)
    {
        using var background = MusicBrainzGate.Background();
        try
        {
            var targets = await Targets();
            await _discographies.DeleteAllExcept(targets.Select(t => t.Mbid).ToList());
            var owned = await _catalog.GetOwnedAlbumArtists();

            var expiresAt = await _discographies.GetExpiresAt();
            var now = _time.GetUtcNow();
            var due = targets
                .Where(t => all || !expiresAt.TryGetValue(t.Mbid, out var at) || at <= now)
                .Take(count ?? int.MaxValue)
                .ToList();

            lock (_gate)
            {
                _total = due.Count;
            }

            foreach (var target in due)
            {
                lock (_gate)
                {
                    _currentArtist = target.Name;
                }

                try
                {
                    // A rebuild that is due is due because its answers are old: ask again.
                    var fresh = expiresAt.TryGetValue(target.Mbid, out var at) && at <= now;
                    if (await Build(target, fresh, owned) is null)
                    {
                        Interlocked.Increment(ref _unreachable);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Discography build failed for {Artist} ({Mbid})", target.Name, target.Mbid);
                    Interlocked.Increment(ref _errors);
                }

                Interlocked.Increment(ref _processed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discography pass could not run");
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                _currentArtist = null;
                _finishedAt = _time.GetUtcNow();
            }
        }
    }

    /// <summary>
    /// Builds one artist's discography now, at interactive priority. Null when the library resolves no
    /// artist to <paramref name="mbid"/>, or a source didn't answer.
    /// </summary>
    /// <param name="fresh">
    /// Ask MusicBrainz again rather than use what is cached: a Re-check, straight after an edit there.
    /// </param>
    public async Task<ArtistDiscography?> Rebuild(string mbid, bool fresh = false)
    {
        var target = (await Targets()).FirstOrDefault(t => string.Equals(t.Mbid, mbid, StringComparison.OrdinalIgnoreCase));
        return target is null ? null : await Build(target, fresh);
    }

    /// <summary>
    /// Every resolved MusicBrainz artist in the library, once each: settled resolutions only (pinned, or
    /// resolved at high or medium confidence). A low-confidence answer is waiting on a person.
    /// </summary>
    internal async Task<IReadOnlyList<DiscographyTarget>> Targets()
    {
        var settled = (await _resolutions.GetAll())
            .Where(r => r.Mbid is { Length: > 0 }
                        && (r.Status == ArtistResolutionStatus.Pinned
                            || r is { Status: ArtistResolutionStatus.Resolved, Confidence: ResolutionConfidence.High or ResolutionConfidence.Medium }))
            .GroupBy(r => r.Mbid!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        var targets = new List<DiscographyTarget>();
        foreach (var artist in settled)
        {
            var deezer = new List<DeezerIdentity>();
            // A shared name's Deezer page is the name's, and may be the other act's: leave it out.
            foreach (var resolution in artist.Where(r => r.PlexArtistKey is null))
            {
                var key = new ArtistKey(resolution.Artist);
                if (await _catalog.GetDeezer(key) is { } linked
                    && !await _catalog.IsDeezerUnlinked(key)
                    && deezer.All(d => d.Id != linked.Identity.Id))
                {
                    deezer.Add(linked.Identity);
                }
            }

            var first = artist.First();
            targets.Add(new DiscographyTarget(
                artist.Key,
                first.Name ?? first.Artist,
                deezer,
                artist.Select(r => new DiscographyLibraryArtist(r.Artist, r.PlexArtistKey)).ToList()));
        }
        return targets;
    }

    /// <summary>
    /// Gathers, matches and stores one artist's discography, then records which release group each of
    /// the library's albums by it is. Null, with nothing stored, when a source didn't answer part of it.
    /// </summary>
    /// <param name="owned">The library's albums (<see cref="IArtistCatalogRepo.GetOwnedAlbumArtists"/>), read once per pass.</param>
    internal async Task<ArtistDiscography?> Build(
        DiscographyTarget target, bool fresh, IReadOnlyDictionary<string, IReadOnlyList<OwnedAlbumArtist>>? owned = null)
    {
        var groups = await _cache.GetOrFetch(
            $"musicbrainz:release-groups:{target.Mbid}",
            () => _musicBrainz.BrowseReleaseGroups(target.Mbid),
            g => Lifetime(g.Select(x => x.FirstReleaseDate)),
            fresh);
        if (groups is null)
        {
            return null;
        }

        var releases = await _cache.GetOrFetch(
            $"musicbrainz:releases:{target.Mbid}",
            () => _musicBrainz.BrowseReleases(target.Mbid),
            r => Lifetime(r.Select(x => x.Date)),
            fresh);
        if (releases is null)
        {
            return null;
        }
        groups = WithReleaseGroups(groups, releases);

        var listing = new List<DeezerAlbum>();
        var searchOnly = new HashSet<long>();
        foreach (var page in target.Deezer)
        {
            if (!await DeezerAlbums(page, page.Name ?? target.Name, listing, searchOnly))
            {
                return null;
            }
        }

        var linked = await Linked(releases, listing);

        var byBarcode = new Dictionary<string, DeezerAlbum>();
        foreach (var barcode in releases.Select(r => r.Barcode).OfType<string>().Where(b => b.Length > 0).Distinct())
        {
            var lookup = await _cache.GetOrFetch(
                $"deezer:upc:{barcode}",
                () => _deezer.GetAlbumByUpc(barcode),
                l => l.Album is null ? BarcodeMissingLifetime : BarcodeFoundLifetime);
            if (lookup is null)
            {
                return null;
            }
            if (lookup.Album is { id: > 0 } album)
            {
                byBarcode[barcode] = album;
            }
        }

        var previous = await _discographies.Get(target.Mbid);
        var rejected = (previous?.ReleaseGroups ?? [])
            .Where(g => g.Rejected.Count > 0)
            .ToDictionary(g => g.Mbid, g => (IReadOnlySet<long>)g.Rejected.ToHashSet());

        var (matched, unmatched) = DeezerEditionMatcher.Match(
            groups, releases, listing, searchOnly, linked, byBarcode, rejected);

        owned ??= await _catalog.GetOwnedAlbumArtists();
        var ownedMatches = new List<(ArtistKey Artist, List<OwnedAlbumMatch> Matches, Dictionary<string, AlbumIdentity> Earlier)>();
        foreach (var libraryArtist in target.LibraryArtists)
        {
            var library = LibraryArtist.For(libraryArtist.Name, owned.GetValueOrDefault(libraryArtist.Name) ?? [])
                .FirstOrDefault(a => a.PlexArtistKey == libraryArtist.PlexArtistKey);
            if (library is null || library.Albums.Count == 0)
            {
                continue;
            }

            var key = new ArtistKey(libraryArtist.Name);
            var earlier = await _catalog.GetAlbumIdentities(key);
            var matches = OwnedAlbumMatcher.Match(library.Id, library.Albums, matched, earlier);
            await FollowDeezerLinks(matches, unmatched, earlier, fresh);
            ownedMatches.Add((key, matches, earlier));
        }

        var now = _time.GetUtcNow();
        var discography = new ArtistDiscography(
            target.Mbid,
            target.Name,
            target.Deezer.Select(d => d.Id).ToList(),
            now,
            now + Lifetime(groups.Select(g => g.FirstReleaseDate)),
            matched,
            unmatched,
            ownedMatches.SelectMany(o => o.Matches).ToList());
        await _discographies.Put(discography);

        // A person's decision is written back as it was; the rest as matched, keeping what they unlinked.
        foreach (var (artist, matches, earlier) in ownedMatches)
        {
            await _catalog.SetAlbumIdentities(artist, matches
                .Select(m => earlier.GetValueOrDefault(m.Title) is { } before
                    ? before.Manual ? before : before with { Mbid = m.ReleaseGroup }
                    : new AlbumIdentity(m.Title, m.ReleaseGroup))
                .ToList());
        }
        return discography;
    }

    /// <summary>
    /// Adds one Deezer page's albums to <paramref name="listing"/>: the listing, then the albums search
    /// credits to the same artist that the listing left out (noted in <paramref name="searchOnly"/>).
    /// False when Deezer didn't answer.
    /// </summary>
    private async Task<bool> DeezerAlbums(
        DeezerIdentity page, string searchName, List<DeezerAlbum> listing, HashSet<long> searchOnly)
    {
        var own = await _deezer.GetAlbums(page.Id);
        if (own is null)
        {
            return false;
        }

        var found = await _deezer.SearchArtistAlbums(searchName);
        if (found is null)
        {
            return false;
        }

        var known = listing.Select(a => a.id).ToHashSet();
        listing.AddRange(own.Where(a => known.Add(a.id)));
        foreach (var album in found.Where(a => a.artist?.id == page.Id && known.Add(a.id)))
        {
            listing.Add(album);
            searchOnly.Add(album.id);
        }
        return true;
    }

    /// <summary>
    /// The Deezer albums each release links to, by release MBID. A link to an album on the listing is
    /// taken from it; any other is looked up, because Deezer renumbers reissued albums and an old id
    /// answers with the new album. A link Deezer doesn't answer for is skipped: it only ever adds.
    /// </summary>
    private async Task<Dictionary<string, IReadOnlyList<DeezerAlbum>>> Linked(
        IReadOnlyList<MusicBrainzRelease> releases, IReadOnlyList<DeezerAlbum> listing)
    {
        var onListing = listing.GroupBy(a => a.id).ToDictionary(g => g.Key, g => g.First());
        var linked = new Dictionary<string, IReadOnlyList<DeezerAlbum>>();
        foreach (var release in releases.Where(r => r.Id is { Length: > 0 }))
        {
            var albums = new List<DeezerAlbum>();
            foreach (var id in DeezerAlbumIds(release))
            {
                var album = onListing.GetValueOrDefault(id)
                            ?? await _cache.GetOrFetch(
                                $"deezer:album:{id}", () => _deezer.GetAlbum(id), _ => DeezerAlbumLifetime);
                if (album is { id: > 0 })
                {
                    albums.Add(album);
                }
            }

            if (albums.Count > 0)
            {
                linked[release.Id!] = albums;
            }
        }
        return linked;
    }

    /// <summary>
    /// The artist's release groups, plus the groups of its own releases the browse left out: a group is
    /// found by its credit, and a release credited to the artist can sit in a group credited to someone
    /// else (an earlier band name, a project, the artist with a backing band).
    /// </summary>
    internal static MusicBrainzReleaseGroup[] WithReleaseGroups(
        IReadOnlyList<MusicBrainzReleaseGroup> groups, IReadOnlyList<MusicBrainzRelease> releases)
    {
        var known = groups.Select(g => g.Id).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return groups
            .Concat(releases
                .Select(r => r.ReleaseGroup)
                .OfType<MusicBrainzReleaseGroup>()
                .Where(g => g.Id is { Length: > 0 } id && known.Add(id)))
            .ToArray();
    }

    /// <summary>
    /// Settles owned albums nothing on the discography fits, a tie included, by the Deezer album of the
    /// same title: when MusicBrainz links it to releases of exactly one group, and that group's title
    /// shares a word with the album's, the album is that group (<see cref="OwnedAlbumMatchMethod.DeezerLink"/>).
    /// The group may be credited to another artist entirely: a duo the artist is half of, or the original
    /// artist of a remix EP. A lookup MusicBrainz doesn't answer is skipped; the next build asks again.
    /// </summary>
    private async Task FollowDeezerLinks(
        List<OwnedAlbumMatch> matches,
        IReadOnlyList<UnmatchedDeezerAlbum> unmatched,
        IReadOnlyDictionary<string, AlbumIdentity> earlier,
        bool fresh)
    {
        var deezer = unmatched
            .GroupBy(u => AlbumTitleMatcher.NormalizeRecord(u.Title))
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.Select(u => u.AlbumId).Distinct().ToList());

        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var key = AlbumTitleMatcher.NormalizeRecord(match.Title);
            if (match.ReleaseGroup is not null
                || match.Method == OwnedAlbumMatchMethod.Manual
                || !deezer.TryGetValue(key, out var albumIds))
            {
                continue;
            }

            var rejected = (earlier.GetValueOrDefault(match.Title)?.Rejected ?? [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var found = new Dictionary<string, MusicBrainzReleaseGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var albumId in albumIds)
            {
                foreach (var group in await LinkedReleaseGroups(albumId, fresh))
                {
                    if (!rejected.Contains(group.Id!))
                    {
                        found.TryAdd(group.Id!, group);
                    }
                }
            }

            if (found.Count == 1
                && found.Values.Single() is var only
                && DeezerEditionMatcher.Words(key)
                    .Overlaps(DeezerEditionMatcher.Words(AlbumTitleMatcher.NormalizeRecord(only.Title))))
            {
                matches[i] = match with
                {
                    ReleaseGroup = only.Id, Method = OwnedAlbumMatchMethod.DeezerLink, Candidates = null,
                };
            }
        }
    }

    /// <summary>The groups of the releases MusicBrainz links a Deezer album to. Empty when it links none.</summary>
    private async Task<List<MusicBrainzReleaseGroup>> LinkedReleaseGroups(long albumId, bool fresh)
    {
        var resource = $"https://www.deezer.com/album/{albumId}";
        var url = await _cache.GetOrFetch(
            $"musicbrainz:url:{resource}",
            () => _musicBrainz.LookupUrl(resource),
            u => u.Relations.Count > 0 ? LinkFoundLifetime : LinkMissingLifetime,
            fresh);

        var groups = new List<MusicBrainzReleaseGroup>();
        var releaseIds = (url?.Relations ?? [])
            .Where(r => r is { TargetType: "release", Ended: false })
            .Select(r => r.Release?.Id)
            .OfType<string>()
            .Distinct();
        foreach (var releaseId in releaseIds)
        {
            var release = await _cache.GetOrFetch(
                $"musicbrainz:release:{releaseId}",
                () => _musicBrainz.GetRelease(releaseId),
                _ => LinkFoundLifetime);
            if (release?.ReleaseGroup is { Id.Length: > 0 } group)
            {
                groups.Add(group);
            }
        }
        return groups;
    }

    /// <summary>The Deezer album ids a release links to, from links that haven't ended.</summary>
    internal static IEnumerable<long> DeezerAlbumIds(MusicBrainzRelease release) =>
        (release.Relations ?? [])
            .Where(r => r is { TargetType: "url", Ended: false })
            .Select(r => DeezerAlbumUrl().Match(r.Url?.Resource ?? ""))
            .Where(m => m.Success && long.TryParse(m.Groups[1].ValueSpan, out _))
            .Select(m => long.Parse(m.Groups[1].ValueSpan))
            .Distinct();

    /// <summary>
    /// How long an answer stays fresh, from the dates in it: about a week for an artist who released
    /// something in the last two years, otherwise 60 to 90 days. Spread at random either way, so a
    /// library built in one pass doesn't come due in one pass.
    /// </summary>
    internal TimeSpan Lifetime(IEnumerable<string?> dates)
    {
        var latest = dates.Select(ParseDate).Max();
        var active = latest is { } date && date >= DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime - ActiveWindow);
        return active
            ? ActiveLifetime + ActiveSpread * (Random.Shared.NextDouble() * 2 - 1)
            : QuietMin + (QuietMax - QuietMin) * Random.Shared.NextDouble();
    }

    /// <summary>A MusicBrainz date ("2024-04-12", "2024-04", "2024"), or null when there is none.</summary>
    private static DateOnly? ParseDate(string? date)
    {
        if (date is not { Length: >= 4 } || !int.TryParse(date.AsSpan(0, 4), out var year) || year <= 0)
        {
            return null;
        }

        var month = date.Length >= 7 && int.TryParse(date.AsSpan(5, 2), out var m) && m is >= 1 and <= 12 ? m : 1;
        var day = date.Length >= 10 && int.TryParse(date.AsSpan(8, 2), out var d) && d >= 1
                  && d <= DateTime.DaysInMonth(year, month) ? d : 1;
        return new DateOnly(year, month, day);
    }

    /// <summary>The whole library's match rates.</summary>
    public async Task<DiscographyReport> Report()
    {
        var targets = await Targets();
        var ids = targets.Select(t => t.Mbid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var built = (await _discographies.GetAll()).Where(d => ids.Contains(d.Mbid)).ToList();

        var groups = built.SelectMany(d => d.ReleaseGroups).ToList();
        var core = groups.Where(g => g.IsCore).ToList();
        var editions = groups.SelectMany(g => g.Editions).ToList();
        var unmatched = built.Sum(d => d.UnmatchedDeezer.Count);

        return new DiscographyReport(
            targets.Count,
            built.Count,
            groups.Count,
            core.Count,
            core.Count(g => g.Editions.Any(e => e.Confidence == EditionConfidence.High)),
            core.Count(g => g.Editions.Count > 0 && g.Editions.All(e => e.Confidence == EditionConfidence.Low)),
            core.Count(g => g.Editions.Count == 0),
            editions.Count + unmatched,
            editions.Count(e => e.Confidence == EditionConfidence.High),
            editions.Count(e => e.Confidence == EditionConfidence.Low),
            unmatched,
            Enum.GetValues<EditionMatchMethod>().ToDictionary(m => m.ToString(), m => editions.Count(e => e.Method == m)),
            editions.Count(e => e.TitleDisagrees),
            OwnedAlbumReport.Of(built.SelectMany(d => d.Owned ?? [])),
            GetStatus());
    }

    /// <summary>
    /// Every built discography in brief, the most unmatched Deezer albums first, then the most core
    /// release groups without an edition: where the matching needs looking at.
    /// </summary>
    public async Task<IReadOnlyList<DiscographySummary>> Summaries() =>
        (await _discographies.GetAll())
            .Select(DiscographySummary.Of)
            .OrderByDescending(s => s.DeezerUnmatched)
            .ThenByDescending(s => s.CoreWithoutEdition)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    [GeneratedRegex(@"^https?://(?:www\.)?deezer\.com/(?:[a-z]{2}(?:-[a-z]{2})?/)?album/(\d+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex DeezerAlbumUrl();
}
