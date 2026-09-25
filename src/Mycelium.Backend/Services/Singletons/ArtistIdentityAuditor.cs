using Microsoft.Extensions.Logging;
using Mycelium.Interfaces;
using Mycelium.ListenBrainz.Models;
using Mycelium.ListenBrainz.Services;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>A point-in-time view of an identity pass, for the reconciliation page.</summary>
/// <param name="Unreachable">Artists skipped because MusicBrainz didn't answer. Picked up by the next pass.</param>
public record ArtistIdentityPassStatus(
    bool Running,
    int Processed,
    int Total,
    int Unreachable,
    int Errors,
    string? CurrentArtist,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>
/// How the library stands against MusicBrainz: the Phase 1 report of <c>MUSICBRAINZ-IDENTITY.md</c>.
/// </summary>
/// <param name="LibraryArtists">
/// Library artists: one per name, except a name covering several Plex artists counts each of them.
/// </param>
/// <param name="Checked">Library artists with a stored resolution.</param>
/// <param name="Pinned">Pinned by hand.</param>
/// <param name="DisagreesWithCurrent">Resolved to a different artist than the one linked today.</param>
/// <param name="SharedNames">Names that cover more than one Plex artist, each checked separately.</param>
public record ArtistIdentityReport(
    int LibraryArtists,
    int Checked,
    int Pinned,
    int High,
    int Medium,
    int Low,
    int Ambiguous,
    int Mixed,
    int Missing,
    int Unlinked,
    int DisagreesWithCurrent,
    int SharedNames,
    int NeedsAttention,
    ArtistIdentityPassStatus Pass);

/// <summary>
/// One act in the library, as the identity check sees it: usually a name, but one Plex artist of a name
/// that several share — each with its own albums, so each can be matched to its own MusicBrainz artist.
/// </summary>
public record LibraryArtist(string Name, int? PlexArtistKey, IReadOnlyList<string> Albums)
{
    public string Id => ArtistResolution.LibraryArtistId(Name, PlexArtistKey);

    /// <summary>
    /// The library artists a name stands for. Only a name whose albums sit under two or more Plex
    /// artists is split; albums with no Plex artist (collaborations, entries synced before it was
    /// recorded) can't be placed then, and are left out rather than guessed at.
    /// </summary>
    public static IReadOnlyList<LibraryArtist> For(string name, IReadOnlyList<OwnedAlbumArtist> albums)
    {
        var byPlexArtist = albums
            .Where(a => a.PlexArtistRatingKey is not null)
            .GroupBy(a => a.PlexArtistRatingKey!.Value)
            .ToList();
        if (byPlexArtist.Count < 2)
        {
            return [new LibraryArtist(name, null, Titles(albums))];
        }

        return byPlexArtist
            .OrderBy(g => g.Key)
            .Select(g => new LibraryArtist(name, g.Key, Titles(g)))
            .ToList();
    }

    private static IReadOnlyList<string> Titles(IEnumerable<OwnedAlbumArtist> albums) =>
        albums.Select(a => a.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// Checks library artists against MusicBrainz and stores what each came to
/// (<see cref="IArtistResolutionRepo"/>). Read-only as far as the rest of the app goes: nothing here
/// changes an artist's current link — it records what the link <em>should</em> be, and how sure that
/// is, so the gaps can be fixed (on MusicBrainz, or by a pin) before anything is keyed by MBID.
///
/// <para><b>Evidence gathered per artist</b>, each a lead to a MusicBrainz artist:</para>
/// <list type="bullet">
/// <item>MusicBrainz's link to the artist's Deezer page (<see cref="IMusicBrainzApi.LookupUrl"/>).</item>
/// <item>The MBID linked today.</item>
/// <item>A name search, keeping every act that goes by the name.</item>
/// </list>
/// <para>Then each lead's discography is checked for the albums the library owns — the evidence that
/// settles it. How the evidence is weighed is <see cref="ArtistIdentityJudge"/>'s job.</para>
///
/// <para>Discographies and URL lookups go through <see cref="SourceCache"/>: the same release groups
/// are what the discography work reads next, and a re-run shouldn't cost the hours the first run did.
/// The name search is not cached — it is one request, and it is the part that changes when someone
/// adds an artist to MusicBrainz.</para>
/// </summary>
public class ArtistIdentityAuditor
{
    /// <summary>How long a resolution stands before the background pass checks the artist again.</summary>
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromDays(30);

    /// <summary>How long a MusicBrainz discography or URL lookup is cached for.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// How many same-named acts the name search asks for — and so how many get their discography
    /// checked. Generous on purpose: MusicBrainz scores every exact-name act alike, so the one whose
    /// discography holds the library's albums is as likely to be tenth as first ("Iconoclast" has ten,
    /// and the right one was sixth). An unchecked candidate can only ever read as "a person should
    /// look", so checking too few just hands the work to a person. Each check is one request, cached
    /// for a month, and only names that many acts share pay it.
    /// </summary>
    internal const int MaxCandidates = 25;

    private readonly IMusicBrainzApi _musicBrainz;
    private readonly SourceCache _cache;
    private readonly IArtistCatalogRepo _catalog;
    private readonly IArtistResolutionRepo _resolutions;
    private readonly ILogger<ArtistIdentityAuditor> _logger;
    private readonly TimeProvider _time;

    private readonly object _gate = new();
    private Task? _run;
    private bool _running;
    private int _processed, _total, _unreachable, _errors;
    private string? _currentArtist;
    private DateTimeOffset? _startedAt, _finishedAt;

    public ArtistIdentityAuditor(
        IMusicBrainzApi musicBrainz,
        SourceCache cache,
        IArtistCatalogRepo catalog,
        IArtistResolutionRepo resolutions,
        ILogger<ArtistIdentityAuditor> logger)
        : this(musicBrainz, cache, catalog, resolutions, logger, TimeProvider.System)
    {
    }

    internal ArtistIdentityAuditor(
        IMusicBrainzApi musicBrainz,
        SourceCache cache,
        IArtistCatalogRepo catalog,
        IArtistResolutionRepo resolutions,
        ILogger<ArtistIdentityAuditor> logger,
        TimeProvider time)
    {
        _musicBrainz = musicBrainz;
        _cache = cache;
        _catalog = catalog;
        _resolutions = resolutions;
        _logger = logger;
        _time = time;
    }

    /// <summary>
    /// Starts a pass unless one is running, then returns the live status. <paramref name="all"/>
    /// re-checks every artist; otherwise only those never checked or due again.
    /// </summary>
    public ArtistIdentityPassStatus Start(bool all)
    {
        _ = RunPass(all);
        return GetStatus();
    }

    public ArtistIdentityPassStatus GetStatus()
    {
        lock (_gate)
        {
            return new ArtistIdentityPassStatus(
                _running, _processed, _total, _unreachable, _errors, _currentArtist, _startedAt, _finishedAt);
        }
    }

    /// <summary>A pass, or the one already running. What the background service awaits.</summary>
    public Task RunPass(bool all)
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
                _run = Task.Run(() => RunAsync(all));
            }
            return _run!;
        }
    }

    private async Task RunAsync(bool all)
    {
        // Nobody is waiting on a pass: anything a user asks MusicBrainz goes first.
        using var background = MusicBrainzGate.Background();
        try
        {
            var present = await LibraryArtists();
            await _resolutions.DeleteAllExcept(present.Select(a => a.Id).ToList());

            var checkedAt = await _resolutions.GetCheckedAt();
            var now = _time.GetUtcNow();
            var due = all
                ? present
                : present.Where(a => !checkedAt.TryGetValue(a.Id, out var at) || now - at >= RecheckAfter).ToList();

            lock (_gate)
            {
                _total = due.Count;
            }

            foreach (var artist in due)
            {
                lock (_gate)
                {
                    _currentArtist = artist.Name;
                }

                try
                {
                    var resolution = await Check(artist, fresh: false);
                    if (resolution is null)
                    {
                        Interlocked.Increment(ref _unreachable);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Identity check failed for {Artist}", artist.Id);
                    Interlocked.Increment(ref _errors);
                }

                Interlocked.Increment(ref _processed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Identity pass could not run");
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
    /// Checks one library artist now, at interactive priority — the reconciliation page's Re-check.
    /// <paramref name="plexArtistKey"/> picks one Plex artist of a shared name. <paramref name="fresh"/>
    /// skips the cache, for when someone has just edited MusicBrainz. Null when MusicBrainz didn't
    /// answer, or there is no such library artist.
    /// </summary>
    public async Task<ArtistResolution?> Check(string artist, int? plexArtistKey, bool fresh)
    {
        var target = await Find(artist, plexArtistKey);
        return target is null ? null : await Check(target, fresh);
    }

    /// <summary>
    /// Pins one Plex artist of a shared name to a MusicBrainz artist, then re-checks it. The name-level
    /// pin (the Sources tab's) can't do this — it would pin every act that shares the name. Null when
    /// MusicBrainz has no such artist or there is no such library artist.
    /// </summary>
    public async Task<ArtistResolution?> PinPlexArtist(string artist, int plexArtistKey, string mbid)
    {
        var target = await Find(artist, plexArtistKey);
        if (target is null || await _musicBrainz.GetArtist(mbid) is not { Id: { Length: > 0 } id } found)
        {
            return null;
        }

        await _resolutions.SetPin(target.Id, new MusicBrainzIdentity(id, found.Name, found.Disambiguation));
        return await Check(target, fresh: false);
    }

    private async Task<LibraryArtist?> Find(string artist, int? plexArtistKey)
    {
        var owned = await _catalog.GetOwnedAlbumArtists();
        return LibraryArtist.For(artist, owned.GetValueOrDefault(artist) ?? [])
            .FirstOrDefault(a => a.PlexArtistKey == plexArtistKey);
    }

    /// <summary>
    /// Gathers the evidence for one library artist, judges it, and stores the verdict. Null — and
    /// nothing stored — when MusicBrainz didn't answer part of it: a verdict on half the evidence is
    /// exactly the kind of wrong answer this exists to avoid.
    /// </summary>
    internal async Task<ArtistResolution?> Check(LibraryArtist artist, bool fresh)
    {
        var key = new ArtistKey(artist.Name);
        var unlinked = await _catalog.IsMusicBrainzUnlinked(key);

        // Today's link is the name's. A pin is the name's too — unless the name is shared, when only a
        // pin on this very Plex artist says anything about it.
        var stored = await _catalog.GetMusicBrainz(key);
        var current = stored?.Identity;
        var pinned = artist.PlexArtistKey is null
            ? stored is { IsOverride: true } ? current : null
            : await _resolutions.GetPin(artist.Id);

        // Record-level key → the library's own spelling, for showing which albums matched.
        var owned = new Dictionary<string, string>();
        foreach (var title in artist.Albums)
        {
            var record = AlbumTitleMatcher.NormalizeRecord(title);
            if (record.Length > 0)
            {
                owned.TryAdd(record, title);
            }
        }

        var candidates = new List<Lead>();
        if (!unlinked && pinned is null)
        {
            if (!await GatherLeads(key, current, candidates, fresh))
            {
                return null;
            }

            if (owned.Count > 0)
            {
                foreach (var lead in candidates.Take(MaxCandidates))
                {
                    var groups = await ReleaseGroups(lead.Mbid, fresh);
                    if (groups is null)
                    {
                        return null;
                    }

                    var theirs = groups.Select(g => AlbumTitleMatcher.NormalizeRecord(g.Title)).ToHashSet();
                    lead.ReleaseGroups = groups.Length;

                    // A release group is named after one edition; the library may hold another whose
                    // title differs ("Firewatch Original Soundtrack" is a release in the group
                    // "Firewatch Original Score"). So anything the groups didn't account for is looked
                    // for among the releases too — titles only, the same record-level comparison.
                    if (owned.Keys.Any(o => !theirs.Contains(o)))
                    {
                        var releases = await Releases(lead.Mbid, fresh);
                        if (releases is null)
                        {
                            return null;
                        }

                        theirs.UnionWith(releases.Select(r => AlbumTitleMatcher.NormalizeRecord(r.Title)));
                    }

                    lead.MatchedAlbums = owned.Where(o => theirs.Contains(o.Key)).Select(o => o.Value).ToList();
                }
            }
        }

        var resolution = ArtistIdentityJudge.Judge(
                artist.Name,
                owned.Count,
                pinned ?? current,
                currentIsPinned: pinned is not null,
                unlinked,
                candidates.Select(c => c.ToCandidate()).ToList(),
                _time.GetUtcNow())
            with
            {
                // Today's link is the name's, whatever this Plex artist was pinned to.
                CurrentMbid = current?.Mbid,
                PlexArtistKey = artist.PlexArtistKey,
                Albums = artist.Albums,
            };
        await _resolutions.Put(resolution);
        return resolution;
    }

    /// <summary>
    /// Collects the MusicBrainz artists worth weighing, strongest lead first. False when MusicBrainz
    /// didn't answer.
    /// </summary>
    private async Task<bool> GatherLeads(ArtistKey key, MusicBrainzIdentity? current, List<Lead> leads, bool fresh)
    {
        Lead Add(string mbid, string? name, string? disambiguation)
        {
            var lead = leads.FirstOrDefault(l => string.Equals(l.Mbid, mbid, StringComparison.OrdinalIgnoreCase));
            if (lead is null)
            {
                lead = new Lead(mbid, name, disambiguation);
                leads.Add(lead);
            }
            return lead;
        }

        var deezer = await _catalog.GetDeezer(key);
        if (deezer is not null && !await _catalog.IsDeezerUnlinked(key))
        {
            var url = await LookupUrl($"https://www.deezer.com/artist/{deezer.Value.Identity.Id}", fresh);
            if (url is null)
            {
                return false;
            }

            foreach (var relation in url.Relations.Where(r => r is { TargetType: "artist", Ended: false }))
            {
                if (relation.Artist?.Id is { Length: > 0 } mbid)
                {
                    Add(mbid, relation.Artist.Name, relation.Artist.Disambiguation)
                        .Evidence.Add(ResolutionEvidence.Deezer);
                }
            }
        }

        if (current is not null)
        {
            Add(current.Mbid, current.Name, current.Disambiguation).Evidence.Add(ResolutionEvidence.Current);
        }

        var found = await _musicBrainz.SearchArtists(key.ArtistName, MaxCandidates);
        if (found is null)
        {
            return false;
        }

        foreach (var (artist, byAlias) in MusicBrainzArtistMatch.Matching(found, key.ArtistName))
        {
            Add(artist.Id!, artist.Name, artist.Disambiguation)
                .Evidence.Add(byAlias ? ResolutionEvidence.Alias : ResolutionEvidence.Name);
        }

        return true;
    }

    private Task<MusicBrainzReleaseGroup[]?> ReleaseGroups(string mbid, bool fresh) =>
        _cache.GetOrFetch(
            $"musicbrainz:release-groups:{mbid}",
            () => _musicBrainz.BrowseReleaseGroups(mbid),
            _ => CacheLifetime,
            fresh);

    private Task<MusicBrainzRelease[]?> Releases(string mbid, bool fresh) =>
        _cache.GetOrFetch(
            $"musicbrainz:releases:{mbid}",
            () => _musicBrainz.BrowseReleases(mbid),
            _ => CacheLifetime,
            fresh);

    private Task<MusicBrainzUrl?> LookupUrl(string resource, bool fresh) =>
        _cache.GetOrFetch(
            $"musicbrainz:url:{resource}",
            () => _musicBrainz.LookupUrl(resource),
            _ => CacheLifetime,
            fresh);

    /// <summary>The whole library's standing, for the report and the page's summary.</summary>
    public async Task<ArtistIdentityReport> Report()
    {
        var (present, resolutions) = await Current();
        var resolved = resolutions.Where(r => r.Status == ArtistResolutionStatus.Resolved).ToList();
        return new ArtistIdentityReport(
            present.Count,
            resolutions.Count,
            resolutions.Count(r => r.Status == ArtistResolutionStatus.Pinned),
            resolved.Count(r => r.Confidence == ResolutionConfidence.High),
            resolved.Count(r => r.Confidence == ResolutionConfidence.Medium),
            resolved.Count(r => r.Confidence == ResolutionConfidence.Low),
            resolutions.Count(r => r.Status == ArtistResolutionStatus.Ambiguous),
            resolutions.Count(r => r.Status == ArtistResolutionStatus.Mixed),
            resolutions.Count(r => r.Status == ArtistResolutionStatus.Missing),
            resolutions.Count(r => r.Status == ArtistResolutionStatus.Unlinked),
            resolutions.Count(r => r.DisagreesWithCurrent),
            present.Where(a => a.PlexArtistKey is not null).Select(a => a.Name).Distinct().Count(),
            resolutions.Count(r => r.NeedsAttention),
            GetStatus());
    }

    /// <summary>
    /// The artists a person should look at, the ones with the most albums first — a wrong answer
    /// costs more the more of the library hangs off it.
    /// </summary>
    public async Task<ArtistResolution[]> NeedingAttention()
    {
        var (_, resolutions) = await Current();
        return resolutions
            .Where(r => r.NeedsAttention)
            .OrderBy(r => r.Status switch
            {
                ArtistResolutionStatus.Mixed => 0,
                ArtistResolutionStatus.Ambiguous => 1,
                ArtistResolutionStatus.Resolved => 2,
                ArtistResolutionStatus.Missing => 3,
                _ => 4,
            })
            .ThenByDescending(r => r.OwnedAlbums)
            .ThenBy(r => r.Artist, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>The stored resolutions of library artists still in the library.</summary>
    private async Task<(IReadOnlyList<LibraryArtist> Present, List<ArtistResolution> Resolutions)> Current()
    {
        var present = await LibraryArtists();
        var ids = present.Select(a => a.Id).ToHashSet();
        var resolutions = (await _resolutions.GetAll()).Where(r => ids.Contains(r.Id)).ToList();
        return (present, resolutions);
    }

    /// <summary>Every library artist: each present name, split per Plex artist where it is shared.</summary>
    private async Task<IReadOnlyList<LibraryArtist>> LibraryArtists()
    {
        var owned = await _catalog.GetOwnedAlbumArtists();
        return (await _catalog.GetAllPresent())
            .Select(a => a.ArtistKey.ArtistName)
            .SelectMany(name => LibraryArtist.For(name, owned.GetValueOrDefault(name) ?? []))
            .ToList();
    }

    /// <summary>A candidate being assembled: evidence accumulates as each source mentions it.</summary>
    private sealed class Lead(string mbid, string? name, string? disambiguation)
    {
        public string Mbid { get; } = mbid;
        public List<string> Evidence { get; } = new();
        public IReadOnlyList<string>? MatchedAlbums { get; set; }
        public int? ReleaseGroups { get; set; }

        public ResolutionCandidate ToCandidate() =>
            new(Mbid, name, disambiguation, MatchedAlbums?.Count, Evidence.Distinct().ToArray(),
                MatchedAlbums, ReleaseGroups);
    }
}
