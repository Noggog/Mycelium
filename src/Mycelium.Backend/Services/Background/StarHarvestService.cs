using Mycelium.Backend.Services.Singletons;

namespace Mycelium.Backend.Services.Background;

/// <summary>
/// Mirrors every linked user's Plex song ratings into Mongo on a slow cadence, so the metadata archive
/// has something to commit. See <see cref="StarHarvester"/> for why the copy matters.
///
/// <para>Runs on <see cref="ReconsiderPolicy.Interval"/> — the same weekly clock as
/// <see cref="ReconsiderSweepService"/>, deliberately, because the two read the same underlying data
/// and there is no sense in visiting Plex on two different schedules for it. It does <em>not</em>
/// share that sweep's reads: the reconsider pass visits only thumbed, owned artists and keeps
/// averages, whereas an archive needs every rated track and the identity to go with it. One paged
/// per-account sweep turns out to be cheaper than the per-artist requests it makes anyway.</para>
///
/// <para>Offset past the reconsider sweep's own startup delay so a boot isn't several heavy Plex
/// passes at once. Unscattered: it reads the user's own server, which is not looking for a bot.</para>
/// </summary>
public class StarHarvestService : BackgroundService
{
    /// <summary>Behind the reconsider sweep (10 min) and the daily syncs, so boot stays calm.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(20);

    private readonly StarHarvester _harvester;
    private readonly MetadataArchiveConfig _archive;
    private readonly ReconsiderPolicy _policy;
    private readonly JitterPolicy _jitter;
    private readonly ILogger<StarHarvestService> _logger;

    public StarHarvestService(
        StarHarvester harvester,
        MetadataArchiveConfig archive,
        ReconsiderPolicy policy,
        JitterPolicy jitter,
        ILogger<StarHarvestService> logger)
    {
        _harvester = harvester;
        _archive = archive;
        _policy = policy;
        _jitter = jitter;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The archive is the only thing that reads this mirror, so with archiving off the sweep would
        // be a weekly full-library read per account in service of nothing. The dev endpoint still
        // works either way, for anyone who wants to fill the mirror by hand.
        if (!_archive.Enabled)
        {
            _logger.LogInformation(
                "Star harvest is off (set METADATA_REPO_PATH to enable the metadata archive)");
            return Task.CompletedTask;
        }

        return _jitter.RunPeriodic(StartupDelay, _policy.Interval, HarvestOnce, stoppingToken, scatter: false);
    }

    /// <summary>Public so it can be unit-tested without the timer.</summary>
    public async Task HarvestOnce()
    {
        try
        {
            await _harvester.HarvestAll();
        }
        catch (Exception ex)
        {
            // HarvestAll already swallows per-user failures; this is the belt for anything that escapes
            // it, since an exception out of ExecuteAsync would stop the host.
            _logger.LogError(ex, "Star harvest pass failed; will retry at the next interval");
        }
    }
}
