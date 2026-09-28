using Mycelium.Backend.Services.Singletons;

namespace Mycelium.Backend.Services.Background;

/// <summary>
/// Keeps every resolved artist's discography built and current (see <see cref="ArtistDiscographyBuilder"/>):
/// daily, it builds the artists that have none and rebuilds the ones whose discography has expired. The
/// Discover feed will read stored discographies of artists nobody opens, so this, not a page visit, is
/// what keeps them fresh.
/// </summary>
public class DiscographyService : BackgroundService
{
    /// <summary>Behind the identity pass, so the day's newly resolved artists are there to build.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(90);

    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly ArtistDiscographyBuilder _builder;
    private readonly JitterPolicy _jitter;
    private readonly ILogger<DiscographyService> _logger;

    public DiscographyService(
        ArtistDiscographyBuilder builder, JitterPolicy jitter, ILogger<DiscographyService> logger)
    {
        _builder = builder;
        _jitter = jitter;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _jitter.RunPeriodic(StartupDelay, Interval, BuildDue, stoppingToken);

    /// <summary>Public so it can be unit-tested without the timer.</summary>
    public async Task BuildDue()
    {
        try
        {
            await _builder.RunPass(all: false);
            var status = _builder.GetStatus();
            _logger.LogInformation(
                "Discography pass built {Processed} artist(s); {Unreachable} unanswered, {Errors} error(s)",
                status.Processed, status.Unreachable, status.Errors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discography pass failed; will retry at the next interval");
        }
    }
}
