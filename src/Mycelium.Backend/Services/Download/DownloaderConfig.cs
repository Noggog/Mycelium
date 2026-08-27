namespace Mycelium.Backend.Services.Download;

/// <summary>
/// Configuration for the Deezer download subsystem, read from environment variables in MainModule
/// (no hardcoded config). Whether the background drainer runs at all is *not* here — that's the
/// switch on the Download page, stored in Mongo (see DownloadSettings) — and manual "download now"
/// works either way. The Deezer <c>ARL</c> itself lives in <b>streamrip's own config</b> (bootstrapped
/// with <c>rip config</c> on the server), not here — no env var carries it and nothing caches it. The
/// one exception is deliberate: because the ARL expires and is the only credential streamrip accepts,
/// <see cref="DeezerCredentialService"/> can validate and rewrite that one key in place, so a user can
/// paste a fresh token into the page that reported the expiry instead of editing TOML over SSH. We own
/// the orchestration: what to grab, how fast, and where it lands.
/// </summary>
/// <param name="SettleInterval">How often to re-pull the Plex catalog while a just-downloaded album is
/// still waiting to appear in the library, so "Complete" rows close out in minutes rather than at the
/// next daily catalog sync.</param>
/// <param name="SettleWindow">How long after a download we keep watching for it to land. Past this,
/// the row is left for the daily sync — so an album that never arrives can't keep polling Plex.</param>
/// <param name="FastSettleInterval">Gap between the close-out re-checks of the fast-mode burst (below).</param>
/// <param name="FastSettleWindow">How long that burst runs for. In fast mode the user is watching the
/// Download page while albums land, and a 15-minute <paramref name="SettleInterval"/> — a free-running
/// timer whose phase has nothing to do with when the batch finished — leaves a finished album sitting on
/// "Complete" long after Plex can see it. So a drain in fast mode also fires a short burst of settle
/// passes, <paramref name="FastSettleInterval"/> apart for <paramref name="FastSettleWindow"/>, and the
/// row closes out while the user is still looking at it. Outside fast mode nothing changes.</param>
public record DownloaderConfig(
    string DownloadDir,
    string RipBinary,
    string Quality,
    /// <summary>
    /// Qualities to retry at, best first, when <paramref name="Quality"/> doesn't yield every track.
    /// A chain rather than a single step because Deezer's formats vary <i>per track</i>: an album can
    /// have no FLAC at all, a 320 master for one track and only 128 for the rest. streamrip 2.1.0 has
    /// no per-track downgrade of its own — when a format is missing it builds a URL on the retired
    /// e-cdns-proxy CDN, which no longer resolves — so walking the chain here is what recovers those
    /// tracks. Each pass keeps only what the previous ones missed, so quality never regresses.
    /// </summary>
    IReadOnlyList<string> FallbackQualities,
    string Codec,
    int BatchSize,
    TimeSpan ItemDelay,
    TimeSpan BatchInterval,
    TimeSpan DownloadTimeout,
    TimeSpan SettleInterval,
    TimeSpan SettleWindow,
    TimeSpan FastSettleInterval,
    TimeSpan FastSettleWindow);
