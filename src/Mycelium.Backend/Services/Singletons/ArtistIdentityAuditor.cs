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
/// <param name="Checked">Library artists with a stored resolution.</param>
/// <param name="Pinned">Pinned by hand.</param>
/// <param name="DisagreesWithCurrent">Resolved to a different artist than the one linked today.</param>
public record ArtistIdentityReport(
    int LibraryArtists,
    int Checked,
    int Pinned,
    int High,
    int Medium,
    int Low,
    int Ambiguous,
    int Missing,
    int Unlinked,
    int DisagreesWithCurrent,
    int NeedsAttention,
    ArtistIdentityPassStatus Pass);

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
    /// How many leads get their discography checked. Each is at least one request; past this many
    /// same-named acts the answer is going to be "a person should look" anyway.
    /// </summary>
    internal const int MaxDiscographies = 5;

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
            var present = (await _catalog.GetAllPresent()).Select(a => a.ArtistKey.ArtistName).ToList();
            await _resolutions.DeleteAllExcept(present);

            var checkedAt = await _resolutions.GetCheckedAt();
            var now = _time.GetUtcNow();
            var due = all
                ? present
                : present.Where(a => !checkedAt.TryGetValue(a, out var at) || now - at >= RecheckAfter).ToList();
            var owned = OwnedTitles(await _catalog.GetOwnedAlbums());

            lock (_gate)
            {
                _total = due.Count;
            }

            foreach (var artist in due)
            {
                lock (_gate)
                {
                    _currentArtist = artist;
                }

                try
                {
                    var resolution = await Check(artist, owned.GetValueOrDefault(artist) ?? [], fresh: false);
                    if (resolution is null)
                    {
                        Interlocked.Increment(ref _unreachable);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Identity check failed for {Artist}", artist);
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
    /// Checks one artist now, at interactive priority — the reconciliation page's Re-check.
    /// <paramref name="fresh"/> skips the cache, for when someone has just edited MusicBrainz.
    /// </summary>
    public async Task<ArtistResolution?> Check(string artist, bool fresh)
    {
        var owned = OwnedTitles(await _catalog.GetOwnedAlbums());
        return await Check(artist, owned.GetValueOrDefault(artist) ?? [], fresh);
    }

    /// <summary>
    /// Gathers the evidence for one artist, judges it, and stores the verdict. Null — and nothing
    /// stored — when MusicBrainz didn't answer part of it: a verdict on half the evidence is exactly
    /// the kind of wrong answer this exists to avoid.
    /// </summary>
    internal async Task<ArtistResolution?> Check(string artist, IReadOnlyCollection<string> ownedTitles, bool fresh)
    {
        var key = new ArtistKey(artist);
        var stored = await _catalog.GetMusicBrainz(key);
        var unlinked = await _catalog.IsMusicBrainzUnlinked(key);
        var ownedRecords = ownedTitles
            .Select(AlbumTitleMatcher.NormalizeRecord)
            .Where(t => t.Length > 0)
            .ToHashSet();

        var candidates = new List<Lead>();
        if (!unlinked && stored is not { IsOverride: true })
        {
            if (!await GatherLeads(key, stored?.Identity, candidates, fresh))
            {
                return null;
            }

            if (ownedRecords.Count > 0)
            {
                foreach (var lead in candidates.Take(MaxDiscographies))
                {
                    var groups = await ReleaseGroups(lead.Mbid, fresh);
                    if (groups is null)
                    {
                        return null;
                    }

                    var theirs = groups.Select(g => AlbumTitleMatcher.NormalizeRecord(g.Title)).ToHashSet();
                    lead.AlbumOverlap = ownedRecords.Count(theirs.Contains);
                }
            }
        }

        var resolution = ArtistIdentityJudge.Judge(
            artist,
            ownedRecords.Count,
            stored?.Identity,
            stored?.IsOverride ?? false,
            unlinked,
            candidates.Select(c => c.ToCandidate()).ToList(),
            _time.GetUtcNow());
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

        var found = await _musicBrainz.SearchArtists(key.ArtistName, MusicBrainzArtistMatch.SearchCandidates);
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
            resolutions.Count(r => r.Status == ArtistResolutionStatus.Missing),
            resolutions.Count(r => r.Status == ArtistResolutionStatus.Unlinked),
            resolutions.Count(r => r.DisagreesWithCurrent),
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
                ArtistResolutionStatus.Ambiguous => 0,
                ArtistResolutionStatus.Resolved => 1,
                ArtistResolutionStatus.Missing => 2,
                _ => 3,
            })
            .ThenByDescending(r => r.OwnedAlbums)
            .ThenBy(r => r.Artist, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>The stored resolutions of artists still in the library.</summary>
    private async Task<(HashSet<string> Present, List<ArtistResolution> Resolutions)> Current()
    {
        var present = (await _catalog.GetAllPresent())
            .Select(a => a.ArtistKey.ArtistName)
            .ToHashSet();
        var resolutions = (await _resolutions.GetAll()).Where(r => present.Contains(r.Artist)).ToList();
        return (present, resolutions);
    }

    private static Dictionary<string, IReadOnlyCollection<string>> OwnedTitles(
        Dictionary<string, Dictionary<string, AudioQuality?>> owned) =>
        owned
            .GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyCollection<string>)g.SelectMany(e => e.Value.Keys).ToList(),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>A candidate being assembled: evidence accumulates as each source mentions it.</summary>
    private sealed class Lead(string mbid, string? name, string? disambiguation)
    {
        public string Mbid { get; } = mbid;
        public List<string> Evidence { get; } = new();
        public int? AlbumOverlap { get; set; }

        public ResolutionCandidate ToCandidate() =>
            new(Mbid, name, disambiguation, AlbumOverlap, Evidence.Distinct().ToArray());
    }
}
