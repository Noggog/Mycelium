using Mycelium.Backend.Services.Singletons;

namespace Mycelium.Backend.Services.Background;

/// <summary>
/// Mirrors every linked user's Plex playlists into Mongo on a slow cadence, so the metadata archive
/// has something to commit. See <see cref="PlaylistHarvester"/> for why the copy matters.
///
/// <para>Weekly, on the same <see cref="ReconsiderPolicy.Interval"/> clock as the star harvest and the
/// reconsider sweep — playlists change slowly, and there is no sense visiting Plex on a third
/// schedule. Offset behind the star harvest so a boot isn't several library reads at once.</para>
///
/// <para>Unscattered: it reads the user's own server, which is not looking for a bot.</para>
/// </summary>
public class PlaylistHarvestService : BackgroundService
{
    /// <summary>Behind the star harvest (20 min), which is the heavier of the two.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(30);

    private readonly PlaylistHarvester _harvester;
    private readonly MetadataArchiveConfig _archive;
    private readonly ReconsiderPolicy _policy;
    private readonly JitterPolicy _jitter;
    private readonly ILogger<PlaylistHarvestService> _logger;

    public PlaylistHarvestService(
        PlaylistHarvester harvester,
        MetadataArchiveConfig archive,
        ReconsiderPolicy policy,
        JitterPolicy jitter,
        ILogger<PlaylistHarvestService> logger)
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
                "Playlist harvest is off (set METADATA_REPO_PATH to enable the metadata archive)");
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
            _logger.LogError(ex, "Playlist harvest pass failed; will retry at the next interval");
        }
    }
}
