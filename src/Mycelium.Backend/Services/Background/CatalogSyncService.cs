using Mycelium.Backend.Services.Singletons;
using Mycelium.Plex.Services.Singletons;

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
    private readonly PlexServerTokenService _serverToken;
    private readonly PurchaseService _purchases;
    private readonly ArtistTagBackfill _tagBackfill;
    private readonly AlbumTagBackfill _albumTagBackfill;
    private readonly RecommendedArtistTagger _recommendedTagger;
    private readonly JitterPolicy _jitter;
    private readonly DailySyncSchedule _schedule;
    private readonly ILogger<CatalogSyncService> _logger;

    public CatalogSyncService(
        CatalogRefresher refresher, PlexServerTokenService serverToken, PurchaseService purchases,
        ArtistTagBackfill tagBackfill, AlbumTagBackfill albumTagBackfill,
        RecommendedArtistTagger recommendedTagger, JitterPolicy jitter,
        DailySyncSchedule schedule, ILogger<CatalogSyncService> logger)
    {
        _refresher = refresher;
        _serverToken = serverToken;
        _purchases = purchases;
        _tagBackfill = tagBackfill;
        _albumTagBackfill = albumTagBackfill;
        _recommendedTagger = recommendedTagger;
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
            // Before anything reads Plex: confirm the credential still works and ping plex.tv to push
            // its expiry back. This is the pass that runs at startup and then daily, which makes it
            // the right place for both — an expired token is found by the app rather than by whoever
            // next presses a button, and a token in daily use never goes cold enough to lapse.
            var credential = await _serverToken.Verify();
            if (credential.Valid == false)
            {
                // Verify() has already logged what's wrong and what to do about it. Reading the whole
                // library with a token we just watched Plex refuse would only restate that as a stack
                // trace. A null verdict is different — the server didn't answer, which may have passed.
                return;
            }

            // Gap-fill, not a full sweep: after the one-off catch-up the only albums without a
            // recorded quality are new arrivals, and those cost one small read each.
            var result = await _refresher.Refresh(CatalogRefresher.QualityRead.GapFill);
            // Newly-arrived artists close out their purchase rows (→ in-library, off the buy list).
            await _purchases.Reconcile();
            // ...and finally get the verdict mood their rating couldn't write while they were outside
            // the library. A no-op when this pass found no arrivals, which is most nights.
            await _tagBackfill.Backfill(result.NewlyPresent);
            // The same repair one level down, for collections. It can't key off NewlyPresent — a
            // compilation arriving usually adds a record to an umbrella act the library already had —
            // so it re-checks the (small) set of rated collections against what is now owned.
            await _albumTagBackfill.Backfill();
            // Last, because it reads the library listing this pass just refreshed and the verdicts the
            // two backfills just settled: which owned artists the user's likes point at, marked in Plex
            // as "<username>_recommended". A full reconcile rather than a top-up — the set moves with
            // every like, and nothing else is in a position to take a stale marker back off.
            await _recommendedTagger.Sync();
        }
        catch (PlexUnauthorizedException ex)
        {
            // Reachable when the token dies *between* the check above and the read — rare, and the
            // check has said nothing about it, so this line stands alone. No stack trace: nothing is
            // broken and there is nothing to debug.
            _logger.LogError(
                "Scheduled catalog sync cannot read Plex: {Reason} Re-link Plex in the dev panel.",
                ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled catalog sync failed; will retry at the next interval");
        }
    }
}
