using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.ListenBrainz.Services;

namespace Mycelium.Backend.Services.Background;

/// <summary>
/// Keeps every user's recommendation queue topped up: shortly after startup, then on a fixed cadence,
/// runs a gentle additive <see cref="IQueueReplenisher.TopUp"/> per user — growing the frontier and
/// refreshing stale similarity edges without disturbing pending order (decisions already expand the
/// frontier live, so this only covers staleness + slow drift). Per-user failures are logged and
/// skipped so one bad user doesn't abort the pass; a failed pass retries at the next interval.
/// </summary>
public class QueueReplenishService : BackgroundService
{
    private readonly IUserQueueRepo _queue;
    private readonly IQueueReplenisher _replenisher;
    private readonly ReplenishConfig _config;
    private readonly JitterPolicy _jitter;
    private readonly ILogger<QueueReplenishService> _logger;

    public QueueReplenishService(
        IUserQueueRepo queue,
        IQueueReplenisher replenisher,
        ReplenishConfig config,
        JitterPolicy jitter,
        ILogger<QueueReplenishService> logger)
    {
        _queue = queue;
        _replenisher = replenisher;
        _config = config;
        _jitter = jitter;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _jitter.RunPeriodic(_config.StartupDelay, _config.Interval, ReplenishAll, stoppingToken);

    /// <summary>Tops up every user once. Public so it can be unit-tested without the timer.</summary>
    public async Task ReplenishAll()
    {
        // Topping up resolves artists on MusicBrainz; none of it is something a user is waiting on.
        using var background = MusicBrainzGate.Background();

        string[] userIds;
        try
        {
            userIds = await _queue.GetAllUserIds();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Queue replenish pass could not enumerate users; will retry next interval");
            return;
        }

        foreach (var userId in userIds)
        {
            try
            {
                await _replenisher.TopUp(userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Queue replenish failed for {User}; skipping to the next user", userId);
            }
        }
    }
}
