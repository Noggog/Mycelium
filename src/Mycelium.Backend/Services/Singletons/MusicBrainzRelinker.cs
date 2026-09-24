using Microsoft.Extensions.Logging;
using Mycelium.Interfaces;
using Mycelium.ListenBrainz.Services;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>A point-in-time view of a MusicBrainz relink pass, for the dev panel.</summary>
/// <param name="Kept">Stored name already matches — accepted without a search.</param>
/// <param name="Unchanged">Re-searched, and the stricter match landed on the same MBID.</param>
/// <param name="Relinked">Re-searched and now on a different MBID (or, for an artist with edges but no
/// stored link to compare with, re-derived either way).</param>
/// <param name="Cleared">Re-searched and nothing goes by the name: the link and its edges are gone.</param>
/// <param name="Skipped">Pinned or detached by hand, so never touched.</param>
/// <param name="Errors">MusicBrainz didn't answer (or the artist threw); left exactly as it was.</param>
/// <param name="QueuesRebuilt">Whether the pass changed anything and so rebuilt every user's queue.</param>
public record MusicBrainzRelinkStatus(
    bool Running,
    int Processed,
    int Total,
    int Kept,
    int Unchanged,
    int Relinked,
    int Cleared,
    int Skipped,
    int Errors,
    bool QueuesRebuilt,
    string? CurrentArtist,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>
/// Dev tool: re-checks every automatic MusicBrainz link against the name match
/// <see cref="MusicBrainzArtistResolver"/> now enforces, and repairs what it finds.
///
/// <para><b>Why it's needed.</b> Links used to be MusicBrainz's top search hit whatever its name, so
/// an act MusicBrainz doesn't know was quietly given some other act's MBID — and with it that act's
/// ListenBrainz neighbours, stored as edges and grown into recommendations. Waiting it out doesn't
/// work: the catalog link never expires, and the old ingestion kept existing edges whenever a
/// resolution came back empty.</para>
///
/// <para><b>What it walks.</b> Two sets. Library artists with an automatic link, where the stored
/// MusicBrainz name is checked offline first (<see cref="MusicBrainzArtistMatch.NamesMatch"/>) — the
/// overwhelming majority were right, and they cost nothing. And artists holding ListenBrainz edges
/// with no stored link to check, chiefly liked artists the library doesn't own yet; those have to be
/// searched. Pinned and detached artists are counted and left alone: a user's decision is the one
/// thing a heuristic must never overrule.</para>
///
/// <para><b>What a repair is.</b> A fresh search (cache bypassed — the cache holds the old guesses),
/// which moves the catalog link and drops album release groups resolved under the old MBID; then a
/// forced ListenBrainz re-ingest, which fetches the right edges or records none. If anything changed,
/// every user's recommendation queue is rebuilt at the end so the wrong edges' recommendations go
/// too. An unanswered search changes nothing and counts as an error — safe to simply run again.</para>
/// </summary>
public class MusicBrainzRelinker
{
    private readonly IArtistCatalogRepo _catalog;
    private readonly IRelatedArtistRepo _related;
    private readonly MusicBrainzArtistResolver _resolver;
    private readonly ListenBrainzIngestionService _listenBrainz;
    private readonly IDiscoveryQueueRebuilder _queues;
    private readonly ILogger<MusicBrainzRelinker> _logger;

    private readonly object _gate = new();
    private Task? _run;

    private volatile bool _running;
    private int _processed, _total, _kept, _unchanged, _relinked, _cleared, _skipped, _errors;
    private bool _queuesRebuilt;
    private volatile string? _currentArtist;
    private DateTimeOffset? _startedAt, _finishedAt;

    public MusicBrainzRelinker(
        IArtistCatalogRepo catalog,
        IRelatedArtistRepo related,
        MusicBrainzArtistResolver resolver,
        ListenBrainzIngestionService listenBrainz,
        IDiscoveryQueueRebuilder queues,
        ILogger<MusicBrainzRelinker> logger)
    {
        _catalog = catalog;
        _related = related;
        _resolver = resolver;
        _listenBrainz = listenBrainz;
        _queues = queues;
        _logger = logger;
    }

    /// <summary>Starts a pass unless one is running, then returns the live status.</summary>
    public MusicBrainzRelinkStatus Start()
    {
        lock (_gate)
        {
            if (!_running)
            {
                _running = true;
                _processed = _total = _kept = _unchanged = _relinked = _cleared = _skipped = _errors = 0;
                _queuesRebuilt = false;
                _currentArtist = null;
                _startedAt = DateTimeOffset.UtcNow;
                _finishedAt = null;
                _run = Task.Run(RunAsync);
            }
            return Snapshot();
        }
    }

    public MusicBrainzRelinkStatus GetStatus()
    {
        lock (_gate)
        {
            return Snapshot();
        }
    }

    /// <summary>The pass itself, awaited directly — what <see cref="Start"/> runs in the background.</summary>
    internal async Task RunAsync()
    {
        // A sweep over every linked artist: anything a user is waiting on goes to MusicBrainz first.
        using var background = MusicBrainzGate.Background();
        try
        {
            var linked = (await _catalog.GetAllPresent()).Where(a => a.MusicBrainz != null).ToList();
            var withStoredLink = linked
                .Select(a => a.ArtistKey.ArtistName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var automatic = linked.Where(a => !a.MusicBrainzOverride).ToList();
            _skipped = linked.Count - automatic.Count;

            var unchecked_ = (await _related.GetArtistNamesWithEdges("listenbrainz"))
                .Where(name => !withStoredLink.Contains(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _total = automatic.Count + unchecked_.Count;
            _logger.LogInformation(
                "MusicBrainz relink started: {Linked} automatic link(s), {Unchecked} unlinked artist(s) with edges",
                automatic.Count, unchecked_.Count);

            var changed = false;

            foreach (var artist in automatic)
            {
                var name = artist.ArtistKey.ArtistName;
                if (MusicBrainzArtistMatch.NamesMatch(artist.MusicBrainz!.Name, name))
                {
                    Interlocked.Increment(ref _kept);
                    Interlocked.Increment(ref _processed);
                    continue;
                }

                changed |= await Recheck(name, artist.MusicBrainz.Mbid);
            }

            foreach (var name in unchecked_)
            {
                // Not in the present catalog with a link — but it may still carry a user decision (a pin
                // on a not-yet-owned artist, or a detach), which a pass like this must not second-guess.
                var stored = await _catalog.GetMusicBrainz(new ArtistKey(name));
                if (stored is { IsOverride: true } || await _catalog.IsMusicBrainzUnlinked(new ArtistKey(name)))
                {
                    Interlocked.Increment(ref _skipped);
                    Interlocked.Increment(ref _processed);
                    continue;
                }

                changed |= await Recheck(name, previousMbid: null);
            }

            if (changed)
            {
                _currentArtist = "(rebuilding recommendation queues)";
                await _queues.RebuildAll();
                _queuesRebuilt = true;
            }

            _logger.LogInformation(
                "MusicBrainz relink finished: {Kept} kept, {Unchanged} unchanged, {Relinked} relinked, " +
                "{Cleared} cleared, {Skipped} skipped, {Errors} error(s)",
                _kept, _unchanged, _relinked, _cleared, _skipped, _errors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MusicBrainz relink aborted");
        }
        finally
        {
            lock (_gate)
            {
                _currentArtist = null;
                _finishedAt = DateTimeOffset.UtcNow;
                _running = false;
            }
        }
    }

    /// <summary>Re-searches one artist and re-derives its edges if the link moved. True when it did.</summary>
    private async Task<bool> Recheck(string name, string? previousMbid)
    {
        _currentArtist = name;
        try
        {
            var resolution = await _resolver.Resolve(name, fresh: true);
            if (resolution.Unreachable)
            {
                Interlocked.Increment(ref _errors);
                return false;
            }

            if (previousMbid != null && resolution.Identity?.Mbid == previousMbid)
            {
                Interlocked.Increment(ref _unchanged);
                return false;
            }

            // The resolver just cached the new answer, so this re-ingest resolves without another search.
            await _listenBrainz.EnsureRelated(new ArtistKey(name), forceRefresh: true);
            Interlocked.Increment(ref resolution.Identity is null ? ref _cleared : ref _relinked);
            _logger.LogInformation(
                "MusicBrainz relink: {Artist} {From} -> {To}",
                name, previousMbid ?? "(unknown)", resolution.Identity?.Mbid ?? "(none)");
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            _logger.LogWarning(ex, "MusicBrainz relink failed for {Artist}", name);
            return false;
        }
        finally
        {
            Interlocked.Increment(ref _processed);
        }
    }

    private MusicBrainzRelinkStatus Snapshot() => new(
        _running, _processed, _total, _kept, _unchanged, _relinked, _cleared, _skipped, _errors,
        _queuesRebuilt, _currentArtist, _startedAt, _finishedAt);
}
