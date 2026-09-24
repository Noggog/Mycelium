using Mycelium.Backend.Services.Singletons;

namespace Mycelium.Backend.Services.Background;

/// <summary>
/// Keeps every library artist's MusicBrainz resolution current (see <see cref="ArtistIdentityAuditor"/>):
/// daily, it checks artists never checked and those whose last check is older than
/// <see cref="ArtistIdentityAuditor.RecheckAfter"/>. The first pass over a large library is hours of
/// one-request-a-second traffic, at background priority; after that a day's pass is the new arrivals
/// and a thirtieth of the rest.
///
/// <para>Scattered, like the album-identity backfill: it talks to a shared public service.</para>
/// </summary>
public class ArtistIdentityService : BackgroundService
{
    /// <summary>Behind the catalog sync, so the day's new artists are in the catalog when this runs.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(60);

    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly ArtistIdentityAuditor _auditor;
    private readonly JitterPolicy _jitter;
    private readonly ILogger<ArtistIdentityService> _logger;

    public ArtistIdentityService(
        ArtistIdentityAuditor auditor, JitterPolicy jitter, ILogger<ArtistIdentityService> logger)
    {
        _auditor = auditor;
        _jitter = jitter;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _jitter.RunPeriodic(StartupDelay, Interval, CheckDue, stoppingToken);

    /// <summary>Public so it can be unit-tested without the timer.</summary>
    public async Task CheckDue()
    {
        try
        {
            await _auditor.RunPass(all: false);
            var status = _auditor.GetStatus();
            _logger.LogInformation(
                "Artist identity pass checked {Processed} artist(s); {Unreachable} unanswered, {Errors} error(s)",
                status.Processed, status.Unreachable, status.Errors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Artist identity pass failed; will retry at the next interval");
        }
    }
}
