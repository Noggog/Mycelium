using Mycelium.Backend.Services.Singletons;

namespace Mycelium.Backend.Services.Background;

/// <summary>
/// Keeps the missing-album set fresh: runs the Deezer discography diff shortly after startup, then
/// every day at <see cref="DailySyncSchedule.AlbumSync"/> — half an hour behind the catalog sync, so
/// the catalog (its input) is populated first and the two Deezer-heavy / Plex-heavy passes don't
/// contend, on boot or on the daily anchor. A failed run is logged and retried next tick — the
/// per-user feed serves whatever was last persisted.
/// </summary>
public class AlbumSyncService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly MissingAlbumRefresher _refresher;
    private readonly PurchaseService _purchases;
    private readonly JitterPolicy _jitter;
    private readonly DailySyncSchedule _schedule;
    private readonly ILogger<AlbumSyncService> _logger;

    public AlbumSyncService(
        MissingAlbumRefresher refresher, PurchaseService purchases, JitterPolicy jitter,
        DailySyncSchedule schedule, ILogger<AlbumSyncService> logger)
    {
        _refresher = refresher;
        _purchases = purchases;
        _jitter = jitter;
        _schedule = schedule;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        // Scattered: this one is a Deezer discography call per owned artist, and Deezer does care what
        // a perfectly periodic caller looks like.
        _jitter.RunDaily(_schedule.AlbumSync, StartupDelay, SyncOnce, stoppingToken, scatter: true);

    private async Task SyncOnce()
    {
        try
        {
            // The Deezer diff, not the Plex catalog read — it works off whatever ownership (and
            // album quality) the last catalog sync stored.
            await _refresher.Refresh();
            // Albums that have since landed in the library close out their purchase rows.
            await _purchases.Reconcile();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled missing-album sync failed; will retry at the next interval");
        }
    }
}
