using Mycelium.Backend.Services.Singletons;

namespace Mycelium.Backend.Services.Background;

/// <summary>
/// Keeps the Library Catalog fresh: syncs once on startup, then every day at
/// <see cref="DailySyncSchedule.CatalogSync"/> (default 6am, lightly jittered — see
/// <see cref="JitterPolicy"/>). A wall-clock hour rather than "every 24h" because Plex only files
/// newly-arrived music into the library on its own nightly pass: a read that drifted to just ahead of
/// that pass would keep missing each night's arrivals by minutes and report them a day late.
/// A failed sync is logged and retried at the next tick — it never takes the app down, since
/// reads serve from whatever is already in the catalog. (Registered as a hosted service in
/// Program.cs rather than via assembly scanning, so it lives outside the scanned namespace.)
/// </summary>
public class CatalogSyncService : BackgroundService
{
    private readonly CatalogRefresher _refresher;
    private readonly PurchaseService _purchases;
    private readonly ArtistTagBackfill _tagBackfill;
    private readonly JitterPolicy _jitter;
    private readonly DailySyncSchedule _schedule;
    private readonly ILogger<CatalogSyncService> _logger;

    public CatalogSyncService(
        CatalogRefresher refresher, PurchaseService purchases, ArtistTagBackfill tagBackfill,
        JitterPolicy jitter, DailySyncSchedule schedule, ILogger<CatalogSyncService> logger)
    {
        _refresher = refresher;
        _purchases = purchases;
        _tagBackfill = tagBackfill;
        _jitter = jitter;
        _schedule = schedule;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        // Unscattered: this pass reads the user's own Plex server and Mongo, neither of which is
        // looking for a bot — so it runs at the hour it says it does.
        _jitter.RunDaily(_schedule.CatalogSync, TimeSpan.Zero, SyncOnce, stoppingToken, scatter: false);

    private async Task SyncOnce()
    {
        try
        {
            // Gap-fill, not a full sweep: after the one-off catch-up the only albums without a
            // recorded quality are new arrivals, and those cost one small read each.
            var result = await _refresher.Refresh(CatalogRefresher.QualityRead.GapFill);
            // Newly-arrived artists close out their purchase rows (→ in-library, off the buy list).
            await _purchases.Reconcile();
            // ...and finally get the verdict mood their rating couldn't write while they were outside
            // the library. A no-op when this pass found no arrivals, which is most nights.
            await _tagBackfill.Backfill(result.NewlyPresent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled catalog sync failed; will retry at the next interval");
        }
    }
}
