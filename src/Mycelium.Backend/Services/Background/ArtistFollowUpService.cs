using System.Threading.Channels;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Background;

/// <summary>
/// The seam for handing the worker an album's Plex tag write. Extracted for the same reason as
/// <see cref="IVerdictFollowUp"/>: it lets <c>CollectionService</c> be tested for <em>what</em> it
/// defers without standing up a background service to observe it.
/// </summary>
public interface IAlbumTagFollowUp
{
    /// <inheritdoc cref="ArtistFollowUpService.QueueAlbumTagWrite"/>
    void QueueAlbumTagWrite(string artist, string album, string? addTag, IReadOnlyCollection<string> removeTags);
}

/// <summary>
/// The after-the-click worker. Rating, seeding or re-pointing an artist has two halves: recording the
/// user's decision (a couple of Mongo writes) and the graph work that decision implies — re-ingesting
/// similarity edges from the rate-limited source APIs, re-expanding the recommendation frontier, and
/// stamping the verdict into Plex. Only the first half is what the UI is waiting to see, but doing
/// both inline made "Add" on a not-yet-owned artist a multi-second stall: nothing about that artist is
/// cached yet, so the request paid a MusicBrainz resolve (1 req/sec), a ListenBrainz fetch, a Deezer
/// related pull, <em>and</em> — because an artist outside the library has no stored Plex rating key —
/// a whole-library Plex scan that was always going to match nothing.
///
/// <para>So the endpoints record the decision, hand the rest here, and return. Work runs on a single
/// consumer in submission order, which is what keeps a quick like → clear from landing backwards; one
/// item failing is logged and skipped rather than taking the loop (or, since an unhandled throw in a
/// <see cref="BackgroundService"/> stops the host, the app) down with it.</para>
///
/// <para>Queued work is in memory only: a restart drops whatever is still pending. That is survivable
/// by design — the daily replenisher re-expands every liked artist and <see cref="ArtistTagBackfill"/>
/// re-stamps missing Plex tags, so a dropped item is repaired on the next pass rather than lost.</para>
/// </summary>
public class ArtistFollowUpService : BackgroundService, IAlbumTagFollowUp
{
    /// <summary>One deferred unit of work, with a description for the log line if it fails.</summary>
    private record WorkItem(string Description, Func<Task> Run);

    private readonly IVerdictFollowUp _engine;
    private readonly IRelatedArtistReader _related;
    private readonly IArtistTagger _tagger;
    private readonly IAlbumTagger _albumTagger;
    private readonly ILogger<ArtistFollowUpService> _logger;

    // Unbounded, but only ever holds a user's in-flight clicks — one item per rate/seed/correction.
    private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>();

    public ArtistFollowUpService(
        IVerdictFollowUp engine,
        IRelatedArtistReader related,
        IArtistTagger tagger,
        IAlbumTagger albumTagger,
        ILogger<ArtistFollowUpService> logger)
    {
        _engine = engine;
        _related = related;
        _tagger = tagger;
        _albumTagger = albumTagger;
        _logger = logger;
    }

    /// <summary>
    /// Queues the graph half of a verdict: growing the frontier from a new like (or pruning what a
    /// dislike/clear seeded), then mirroring the verdict into Plex as a mood tag. <paramref name="status"/>
    /// is null when the verdict was cleared. Pass the depth <see cref="DiscoveryEngine.RecordArtistVerdict"/>
    /// returned so the expansion lands at the right distance from the seeds.
    /// </summary>
    public void QueueVerdictFollowUp(
        string userId, string artist, DiscoveryStatus? status, int depth,
        string? addTag, IReadOnlyCollection<string> removeTags)
    {
        Enqueue($"verdict follow-up for {artist}", async () =>
        {
            await _engine.ApplyVerdictFollowUp(userId, artist, status, depth);
            if (addTag != null || removeTags.Count > 0)
            {
                // Best-effort by contract (PlexArtistTagger never throws) — a Plex hiccup must not
                // cost the expansion that already ran.
                await _tagger.SetTags(artist, addTag, removeTags);
            }
        });
    }

    /// <summary>
    /// Queues the Plex mood write a verdict on an <em>album</em> implies. The album twin of
    /// <see cref="QueueVerdictFollowUp"/>, minus the graph half: a compilation seeds no frontier, so
    /// there is nothing to expand — only the tag, which is deferred for the same reason (a catalog read
    /// plus up to two Plex round trips the click has no reason to wait for).
    ///
    /// <para>Runs on the same single consumer in submission order, so a quick like → clear on the same
    /// album can't land backwards.</para>
    /// </summary>
    public void QueueAlbumTagWrite(
        string artist, string album, string? addTag, IReadOnlyCollection<string> removeTags)
    {
        if (addTag == null && removeTags.Count == 0)
        {
            return;
        }

        Enqueue($"album tag write for \"{album}\" ({artist})", () =>
            // Best-effort by contract (PlexAlbumTagger never throws).
            _albumTagger.SetTags(artist, album, addTag, removeTags));
    }

    /// <summary>
    /// Queues the re-derivation an identity correction implies: re-fetch the artist's similarity edges
    /// under its new (or no longer pinned) id, then rebuild the user's queue so the edges the old,
    /// wrong identity seeded drop out.
    /// </summary>
    public void QueueIdentityRefresh(string userId, string artist)
    {
        Enqueue($"identity refresh for {artist}", async () =>
        {
            await _related.GetRelated(new ArtistKey(artist), forceRefresh: true);
            await _engine.Rebuild(userId);
        });
    }

    private void Enqueue(string description, Func<Task> run)
    {
        // Unbounded channel: TryWrite only fails once the writer is completed (shutdown).
        if (!_queue.Writer.TryWrite(new WorkItem(description, run)))
        {
            _logger.LogWarning("Dropped {Work} — the follow-up queue is closed", description);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await item.Run();
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Follow-up work failed: {Work}; continuing with the queue", item.Description);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
