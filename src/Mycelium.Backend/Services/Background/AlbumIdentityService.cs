using Mycelium.Backend.Services.Singletons;

namespace Mycelium.Backend.Services.Background;

/// <summary>
/// Drips owned albums through MusicBrainz until every one of them has a release-group MBID, so the
/// archive keys albums on something more durable than a title. See <see cref="AlbumIdentityResolver"/>
/// for why an MBID is worth the wait.
///
/// <para>Daily, and bounded per pass. MusicBrainz allows one request a second, so a slice of a few
/// thousand albums is tens of minutes of steady, well-behaved traffic — which is the right shape for
/// a backfill nothing is waiting on. Once the library is covered, a pass finds no gaps and costs one
/// catalog read; new arrivals are picked up the next day.</para>
///
/// <para>Scattered, unlike the Plex-facing sweeps: this one talks to a shared public service, and
/// every deployment starting its pass on the same clock tick is exactly what rate limits exist to
/// discourage.</para>
/// </summary>
public class AlbumIdentityService : BackgroundService
{
    /// <summary>
    /// Behind the catalog and album syncs, so a boot doesn't run several heavy passes at once — and
    /// so the day's newly-synced albums are already in the catalog when this looks for gaps.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(45);

    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly AlbumIdentityResolver _resolver;
    private readonly AlbumIdentityConfig _config;
    private readonly JitterPolicy _jitter;
    private readonly ILogger<AlbumIdentityService> _logger;

    private CancellationToken _stopping;

    public AlbumIdentityService(
        AlbumIdentityResolver resolver,
        AlbumIdentityConfig config,
        JitterPolicy jitter,
        ILogger<AlbumIdentityService> logger)
    {
        _resolver = resolver;
        _config = config;
        _jitter = jitter;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation(
                "Album identity backfill is off (ALBUM_MBID_BATCH is {Batch})", _config.BatchSize);
            return Task.CompletedTask;
        }

        _stopping = stoppingToken;
        return _jitter.RunPeriodic(StartupDelay, Interval, ResolveOnce, stoppingToken);
    }

    /// <summary>Public so it can be unit-tested without the timer.</summary>
    public async Task ResolveOnce()
    {
        try
        {
            // The token is handed down so a shutdown ends the pass at the next album rather than
            // waiting out however many rate-limited lookups are left in the slice.
            await _resolver.ResolveSome(_config.BatchSize, _stopping);
        }
        catch (Exception ex)
        {
            // ResolveSome already swallows per-album failures; this is the belt for anything escaping
            // it, since an exception out of ExecuteAsync would stop the host.
            _logger.LogError(ex, "Album identity pass failed; will retry at the next interval");
        }
    }
}
