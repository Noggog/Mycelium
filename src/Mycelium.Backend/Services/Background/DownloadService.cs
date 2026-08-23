using System.Threading.Channels;
using Mycelium.Backend.Services.Download;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Background;

/// <summary>
/// The slow, server-controlled download engine. A single consumer loop pulls album ids off a queue
/// and downloads them one at a time (single-flight — Deezer cracks down on parallel tooling), with a
/// configurable delay between items. Ids reach the queue two ways:
///   • <b>Automatic</b> (the drainer switch): a background loop enqueues
///     pending albums every <c>DOWNLOAD_BATCH_INTERVAL_MINUTES</c>. The switch is stored in Mongo and
///     re-read on every tick, so flipping it on the Download page takes effect without a restart.
///   • <b>Manual</b>: <see cref="RequestDownload"/> (the "Download now" button) enqueues one id and
///     returns immediately, so the HTTP request never blocks on the multi-minute fetch.
/// "Fast mode" (see <see cref="DownloadSettings.FastUntil"/>) is a time-boxed variant of the automatic
/// pass: for an hour it lifts the batch cap so every pending album is queued at once, and re-runs the
/// pass every few seconds so an album marked mid-burst is queued straight away rather than at the next
/// batch tick. The consumer is unchanged — same single flight, same wait between albums — so a burst
/// empties the backlog into the queue, it doesn't make the fetching itself any less polite.
/// Each item goes Pending → Queued → Downloading → Sent/Failed; a downloaded album then closes the loop
/// (file lands in Plex → reconcile → in-library, drops off the list) — via the settle pass here if it lands
/// soon after the download, else at the next daily catalog sync. Registered as a shared
/// singleton hosted service so the endpoint and the loop are the same instance.
/// </summary>
public class DownloadService : BackgroundService
{
    private readonly IPurchaseRepo _repo;
    private readonly IDownloader _downloader;
    private readonly DownloaderConfig _config;
    private readonly DownloadSettings _settings;
    private readonly PurchaseService _purchases;
    private readonly CatalogRefresher _catalog;
    private readonly ArtistTagBackfill _tagBackfill;
    private readonly JitterPolicy _jitter;
    private readonly DownloadSchedule _schedule;
    private readonly ILibraryScanner _scanner;
    private readonly ILogger<DownloadService> _logger;

    // Unbounded but effectively tiny; ProcessOne dedups by re-checking status, so duplicate ids are cheap.
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();

    public DownloadService(
        IPurchaseRepo repo,
        IDownloader downloader,
        DownloaderConfig config,
        DownloadSettings settings,
        PurchaseService purchases,
        CatalogRefresher catalog,
        ArtistTagBackfill tagBackfill,
        JitterPolicy jitter,
        DownloadSchedule schedule,
        ILibraryScanner scanner,
        ILogger<DownloadService> logger)
    {
        _repo = repo;
        _downloader = downloader;
        _config = config;
        _settings = settings;
        _purchases = purchases;
        _catalog = catalog;
        _tagBackfill = tagBackfill;
        _jitter = jitter;
        _schedule = schedule;
        _scanner = scanner;
        _logger = logger;
    }

    /// <summary>
    /// Manually queues one item for download now (the "Download now"/"Retry" button). Moves it to
    /// Queued (so it tallies as in-flight immediately, and a failed item retries) and enqueues it;
    /// returns false if it's unknown or not a downloadable Deezer album. Non-blocking — the consumer
    /// loop does the actual fetch.
    /// </summary>
    public async Task<bool> RequestDownload(string id)
    {
        var item = (await _repo.GetAll()).FirstOrDefault(p => p.Id == id);
        if (item is null || item.Kind != FeedKind.MissingAlbum || item.DeezerAlbumId is null or 0)
        {
            return false;
        }
        if (item.Status is PurchaseStatus.Queued or PurchaseStatus.Downloading)
        {
            return true; // already queued / in flight
        }

        await _repo.SetStatus(id, PurchaseStatus.Queued);
        _queue.Writer.TryWrite(id);
        _logger.LogInformation("Manual download requested for {Id}", id);
        return true;
    }

    // Startup recovery runs before Mongo is necessarily reachable — right after a deploy the store may
    // still be coming up — so it gets a few bounded attempts before we give up and carry on.
    private const int StartupAttempts = 5;
    private static readonly TimeSpan StartupRetryDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Recover from a crash mid-download: anything left Queued/Downloading never finished (and the
        // in-memory queue is gone), so return it to Pending to be re-requested. This is the first thing
        // in the app to touch Mongo, and an unhandled throw in a BackgroundService takes the whole host
        // down (BackgroundServiceExceptionBehavior.StopHost) — so it retries, then continues regardless.
        // Safe to retry here and nowhere else: the loops below haven't started, so nothing is in flight
        // for the reset to clobber.
        for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await ResetStuckDownloads();
                _logger.LogInformation(
                    "Download engine ready via {Backend}; automatic={Automatic} (batch {Batch}, {ItemDelay}s/item, every {Interval})",
                    _downloader.Name, await _settings.Automatic(), _config.BatchSize,
                    _config.ItemDelay.TotalSeconds, _config.BatchInterval);
                break;
            }
            catch (Exception ex) when (attempt < StartupAttempts && !stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex, "Download engine startup pass failed (attempt {Attempt}/{Total}); retrying in {Delay}s",
                    attempt, StartupAttempts, StartupRetryDelay.TotalSeconds);
                await Delay(StartupRetryDelay, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex, "Download engine could not run its startup pass; continuing — the queue recovers at the next restart");
                break;
            }
        }

        // Consumer, automatic producer and the post-download settle watcher run together; any of them
        // ending cancels the rest.
        await Task.WhenAll(Consume(stoppingToken), AutoEnqueue(stoppingToken), Settle(stoppingToken));
    }

    /// <summary>Single-flight consumer: downloads queued ids one at a time, throttled.</summary>
    private async Task Consume(CancellationToken ct)
    {
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    var downloaded = await ProcessOne(id);
                    if (downloaded)
                    {
                        // Drop anything that became owned/unwanted, and space out fetches.
                        await _purchases.Reconcile();
                        // Ask Plex to pick up the new album. Debounced, so a draining batch triggers a
                        // single rescan once it quiets — and a no-op unless PLEX_RESCAN_AFTER_DOWNLOAD is on.
                        await _scanner.RequestScan();

                        // Publish the wait before taking it, so the monitor can show when the next
                        // album starts rather than just "Idle".
                        var wait = _jitter.Apply(_config.ItemDelay);
                        _schedule.ItemWait(wait);
                        await Delay(wait, ct);
                        _schedule.ClearItemWait();
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // One bad item (a Mongo blip mid-status-write, a Plex rescan refused) must not end
                    // the consumer — that would silently stop every later download until a restart.
                    _logger.LogWarning(ex, "Download pass for {Id} failed; continuing with the queue", id);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>
    /// Downloads one queued id if it's still a queued downloadable album (re-checked here, so
    /// duplicate/auto+manual enqueues of the same id are harmless). Returns whether a fetch ran.
    /// </summary>
    public async Task<bool> ProcessOne(string id)
    {
        var item = (await _repo.GetAll()).FirstOrDefault(p => p.Id == id);
        if (item is null
            || item.Status != PurchaseStatus.Queued
            || item.Kind != FeedKind.MissingAlbum
            || item.DeezerAlbumId is null or 0)
        {
            return false;
        }

        // Mark it in-flight before the (slow) fetch so the monitor shows what's downloading now.
        await _repo.SetStatus(item.Id, PurchaseStatus.Downloading);

        DownloadOutcome outcome;
        try
        {
            outcome = await _downloader.Request(item);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Download errored for {Id}", item.Id);
            outcome = DownloadOutcome.Failed();
        }

        await _repo.SetStatus(
            item.Id,
            outcome.Accepted ? PurchaseStatus.Sent : PurchaseStatus.Failed,
            outcome.Failure);
        return true;
    }

    /// <summary>
    /// How often the enqueue pass re-checks for pending albums during a fast-mode burst. Short enough
    /// that an album marked while the burst is running joins the queue as soon as you can look back at
    /// the panel — the whole point of fast mode is that nothing waits for the next batch tick — and
    /// cheap because the pass only reads Mongo; what actually reaches Deezer is still the single-flight
    /// consumer, unchanged.
    /// </summary>
    private static readonly TimeSpan FastPollInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Periodically enqueues pending downloadable albums (batch-capped) while the drainer switch is on.
    /// The loop runs regardless of the switch and each pass re-checks it, so switching to automatic
    /// starts draining at the next tick rather than needing a restart. Its own loop rather than
    /// <see cref="JitterPolicy.RunPeriodic"/> because the cadence isn't fixed: it re-reads the fast-mode
    /// deadline after every pass and drops to <see cref="FastPollInterval"/> while a burst is running,
    /// then returns to the batch interval on its own when the deadline lapses. Every gap is jittered
    /// (as all the app's recurring waits are) and published to <see cref="DownloadSchedule"/> so the
    /// monitor counts down the cadence actually in force.
    /// </summary>
    private async Task AutoEnqueue(CancellationToken ct)
    {
        try
        {
            // Pass first, wait after: after a deploy the queue should start moving at once, and a single
            // pass at startup isn't what a rate-limiter keys on — the repeating cadence is, and that's
            // jittered.
            while (!ct.IsCancellationRequested)
            {
                await EnqueuePendingBatch();

                var wait = await NextEnqueueWait();
                _schedule.BatchWait(wait);
                await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>
    /// How long to wait before the next enqueue pass: the batch interval normally, the much shorter
    /// <see cref="FastPollInterval"/> while a fast-mode burst is running. Re-decided after every pass
    /// rather than fixed at startup, so a burst begun (or ended) mid-loop changes the cadence from the
    /// very next gap.
    /// </summary>
    internal async Task<TimeSpan> NextEnqueueWait()
    {
        var wait = _jitter.Apply(await FastMode() ? FastPollInterval : _config.BatchInterval);
        // A floor, not a throttle: a misconfigured interval of zero would otherwise spin the loop
        // against Mongo as fast as the CPU allows.
        return wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// Whether a fast-mode burst is in force right now. Swallows a failed read rather than letting it
    /// end the loop: not knowing means the ordinary batched pace, which is the safe answer.
    /// </summary>
    private async Task<bool> FastMode()
    {
        try
        {
            return await _settings.FastUntil() is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the fast-mode deadline; using the normal batch pace");
            return false;
        }
    }

    /// <summary>
    /// One automatic pass: a no-op in manual mode, where only the button enqueues. While a fast-mode
    /// burst is running the batch cap comes off and the whole pending list goes onto the queue at once
    /// — the drainer is still single-flight and still spaces albums out by <c>ItemDelay</c>, so what
    /// changes is that it never runs out of work between batch ticks, not how hard it hits Deezer.
    /// </summary>
    internal async Task EnqueuePendingBatch()
    {
        try
        {
            if (!await _settings.Automatic())
            {
                return;
            }

            var fast = await FastMode();
            await _purchases.Reconcile();
            var candidates = (await _repo.GetAll())
                .Where(p => p.Status == PurchaseStatus.Pending
                            && p.Kind == FeedKind.MissingAlbum
                            && p.DeezerAlbumId is > 0)
                .OrderBy(p => p.RequestedAt);
            var pending = (fast ? candidates : candidates.Take(_config.BatchSize)).ToList();
            if (fast && pending.Count > 0)
            {
                _logger.LogInformation("Fast mode: queueing all {Count} pending album(s)", pending.Count);
            }
            foreach (var item in pending)
            {
                // Mark it queued before writing so it tallies as in-flight straight away (the drainer
                // is single-flight, so it'd otherwise sit as Pending until its turn came up).
                await _repo.SetStatus(item.Id, PurchaseStatus.Queued);
                _queue.Writer.TryWrite(item.Id);
            }
        }
        catch (Exception ex)
        {
            // A transient failure must not tear down the timer — retry at the next interval.
            _logger.LogWarning(ex, "Auto-enqueue pass failed; will retry at the next interval");
        }
    }

    /// <summary>
    /// Watches for just-downloaded albums arriving in the library. A "Complete" (Sent) row only clears
    /// once the album shows up in the Plex catalog, and the catalog is otherwise refreshed just once a
    /// day — so without this a finished download sits on the Download page for hours after it's already
    /// visible in Plex. Ticks every <c>DOWNLOAD_SETTLE_INTERVAL_MINUTES</c>.
    /// </summary>
    private Task Settle(CancellationToken ct) =>
        // Unscattered, unlike the download loops above: a settle pass only re-reads the user's own Plex
        // server, so there's no fingerprint to hide — just a plain re-check on the interval.
        _jitter.RunPeriodic(
            _config.SettleInterval, _config.SettleInterval, SettleOnce, ct, scatter: false);

    /// <summary>
    /// One settle pass: re-pull the Plex catalog and reconcile, but only while something downloaded
    /// inside the settle window is still waiting to land. That keeps the (whole-library) Plex read off
    /// the clock when nothing is in flight, and stops an album that never arrives — a title Plex files
    /// differently, say, which the user resolves with "Already in library?" — from polling forever.
    /// </summary>
    internal async Task SettleOnce()
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow - _config.SettleWindow;
            var waiting = (await _repo.GetAll())
                .Count(p => p.Status == PurchaseStatus.Sent && (p.SentAt ?? p.RequestedAt) >= cutoff);
            if (waiting == 0)
            {
                return;
            }

            _logger.LogInformation(
                "Settle pass: {Waiting} downloaded album(s) awaiting the library; refreshing the catalog", waiting);
            var result = await _catalog.Refresh();
            await _purchases.Reconcile();
            // This is the payoff moment for an artist liked before the library had it: the album just
            // landed, so stamp the verdict mood the rating couldn't write back then.
            await _tagBackfill.Backfill(result.NewlyPresent);
        }
        catch (Exception ex)
        {
            // Same contract as the auto-enqueue pass: a transient Plex/Mongo blip must not tear down
            // the timer — the next tick tries again.
            _logger.LogWarning(ex, "Settle pass failed; will retry at the next interval");
        }
    }

    /// <summary>Returns rows stranded mid-pipeline in <see cref="PurchaseStatus.Queued"/> or
    /// <see cref="PurchaseStatus.Downloading"/> (e.g. by a crash — the queue is in-memory and lost on
    /// restart) to <see cref="PurchaseStatus.Pending"/> so they're re-requested.</summary>
    public async Task ResetStuckDownloads()
    {
        foreach (var item in (await _repo.GetAll())
                     .Where(p => p.Status is PurchaseStatus.Queued or PurchaseStatus.Downloading))
        {
            await _repo.SetStatus(item.Id, PurchaseStatus.Pending);
            _logger.LogInformation("Reset stranded download {Id} to pending", item.Id);
        }
    }

    // Task.Delay throws on a negative TimeSpan; treat zero/negative as "no wait" so tests run instantly.
    private static Task Delay(TimeSpan delay, CancellationToken ct) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct);
}
