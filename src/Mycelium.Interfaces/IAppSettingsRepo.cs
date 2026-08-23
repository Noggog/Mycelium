namespace Mycelium.Interfaces;

/// <summary>
/// Durable operator settings — the few switches flipped from the UI rather than baked into the
/// environment, stored so they survive a redeploy. Each getter returns null when the setting has
/// never been set, in which case the caller applies its own default.
/// </summary>
public interface IAppSettingsRepo
{
    /// <summary>
    /// Whether the background download drainer enqueues pending albums on its own, or null when it
    /// has never been toggled (the caller then applies its own default).
    /// </summary>
    Task<bool?> GetDownloadsAutomatic();

    /// <summary>Persists the drainer switch, replacing the default from then on.</summary>
    Task SetDownloadsAutomatic(bool automatic);

    /// <summary>
    /// When the current "fast mode" burst lapses, or null when fast mode has never been turned on (or
    /// was turned back off). A stamp in the past means the burst is over — nothing rewrites it, the
    /// deadline expiring is the whole mechanism — so callers compare it against the clock rather than
    /// treating a present value as "on".
    /// </summary>
    Task<DateTimeOffset?> GetDownloadsFastUntil();

    /// <summary>Persists the fast-mode deadline; null clears it (back to the normal batched pace).</summary>
    Task SetDownloadsFastUntil(DateTimeOffset? until);
}
