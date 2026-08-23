using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// The live automatic/manual switch for the download drainer, plus the temporary "fast mode" burst. Owned entirely by the switch on the
/// Download page and persisted in Mongo, so it survives a redeploy — deliberately not an env var as
/// well, since a second source of truth could only contradict what the UI shows. Read through on every
/// check rather than cached at startup, so toggling takes effect on the next drainer tick instead of
/// needing a restart.
/// </summary>
public class DownloadSettings
{
    /// <summary>What a store that's never been toggled means: draining unattended is the normal mode.</summary>
    private const bool DefaultAutomatic = true;

    /// <summary>
    /// How long a fast-mode burst lasts. Deliberately a fixed window rather than a sticky switch: fast
    /// mode drops the batch cap that paces the drainer against Deezer, so it's something you turn on to
    /// clear a backlog and want back off again — and a deadline means forgetting to switch it off costs
    /// an hour, not a week.
    /// </summary>
    public static readonly TimeSpan FastDuration = TimeSpan.FromHours(1);

    private readonly IAppSettingsRepo _repo;
    private readonly ILogger<DownloadSettings> _logger;

    public DownloadSettings(IAppSettingsRepo repo, ILogger<DownloadSettings> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    /// <summary>Whether the background drainer should enqueue on its own. Manual "download now" works
    /// either way — this governs only the unattended pass.</summary>
    public async Task<bool> Automatic() => await _repo.GetDownloadsAutomatic() ?? DefaultAutomatic;

    public async Task SetAutomatic(bool automatic)
    {
        await _repo.SetDownloadsAutomatic(automatic);
        _logger.LogInformation("Automatic downloads switched {State}", automatic ? "on" : "off");
    }

    /// <summary>
    /// When the current fast-mode burst lapses, or null when fast mode isn't on. A stored deadline that
    /// has passed reads as null, so the burst ends by itself — no timer, and a restart mid-hour picks
    /// up whatever is left of it rather than resetting or stranding it.
    /// </summary>
    public async Task<DateTimeOffset?> FastUntil()
    {
        var until = await _repo.GetDownloadsFastUntil();
        return until > DateTimeOffset.UtcNow ? until : null;
    }

    /// <summary>Starts a fresh <see cref="FastDuration"/> burst, or ends one early. Returns the new
    /// deadline (null when switched off) so the caller can hand it straight back to the page.</summary>
    public async Task<DateTimeOffset?> SetFast(bool fast)
    {
        var until = fast ? DateTimeOffset.UtcNow + FastDuration : (DateTimeOffset?)null;
        await _repo.SetDownloadsFastUntil(until);
        _logger.LogInformation(
            "Fast downloads switched {State}{Until}",
            fast ? "on" : "off",
            until is null ? "" : $" until {until:HH:mm:ss} UTC");
        return until;
    }
}
