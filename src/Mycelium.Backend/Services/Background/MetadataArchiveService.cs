using Mycelium.Backend.Services.Archive;
using Mycelium.Backend.Services.Singletons;

namespace Mycelium.Backend.Services.Background;

/// <summary>
/// Commits a snapshot of the metadata archive once a day, at
/// <see cref="MetadataArchiveConfig.SnapshotAt"/> (default 8am, i.e. two hours past
/// <c>DAILY_SYNC_HOUR</c>).
///
/// <para>The offset is the point: the catalog sync runs at the sync hour and the Deezer album diff
/// thirty minutes after it, so an archive pass anchored two hours later records a library that has
/// just been re-read rather than one it is racing. A snapshot taken mid-sync isn't wrong, but it
/// would show yesterday's arrivals landing a day late for ever.</para>
///
/// <para>Unscattered: this pass reads Mongo and writes to a local git repository, neither of which is
/// looking for a bot — so it runs at the hour it says it does. A failed pass is logged and retried on
/// the next tick; there is no catch-up, because the next snapshot captures the same state anyway.</para>
///
/// <para>Does nothing at all when <c>METADATA_REPO_PATH</c> is unset, so a deployment that hasn't
/// configured an archive behaves exactly as it did before this existed.</para>
/// </summary>
public class MetadataArchiveService : BackgroundService
{
    private readonly MetadataArchiver _archiver;
    private readonly MetadataArchiveConfig _config;
    private readonly JitterPolicy _jitter;
    private readonly ILogger<MetadataArchiveService> _logger;

    public MetadataArchiveService(
        MetadataArchiver archiver,
        MetadataArchiveConfig config,
        JitterPolicy jitter,
        ILogger<MetadataArchiveService> logger)
    {
        _archiver = archiver;
        _config = config;
        _jitter = jitter;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation(
                "Metadata archiving is off (set METADATA_REPO_PATH to enable it)");
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "Metadata archive: snapshotting daily at {At} into {Path}{Remote}",
            _config.SnapshotAt, _config.RepoPath,
            // SafeRemote, never Remote: the URL normally carries an access token, and this line goes
            // to the rolling log file on every start.
            string.IsNullOrWhiteSpace(_config.Remote) ? " (local only)" : $", pushing to {_config.SafeRemote}");

        return _jitter.RunDaily(_config.SnapshotAt, TimeSpan.Zero, SnapshotOnce, stoppingToken, scatter: false);
    }

    /// <summary>Public so it can be unit-tested without the timer.</summary>
    public async Task SnapshotOnce()
    {
        // MetadataArchiver swallows its own failures and reports them in the result, so there is
        // nothing here that could escape ExecuteAsync and stop the host.
        await _archiver.Snapshot();
    }
}
