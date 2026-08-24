using System.Text.Json.Serialization;

namespace Mycelium.Interfaces;

/// <summary>
/// Why a download attempt failed, when the reason changes what the user should do about it. The
/// distinction that matters is <em>retryable</em> vs <em>blocked</em>: a row that failed because
/// Deezer wouldn't serve a track is worth retrying, while one that failed because the session token
/// was rejected will fail identically every time until a human replaces the credential. Without that
/// split the Download page can only say "couldn't grab these — retry", which is actively misleading
/// advice for the case a retry cannot fix.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DownloadFailure
{
    /// <summary>No failure — the attempt succeeded, or the row has never been attempted.</summary>
    None,

    /// <summary>Failed for a reason we couldn't classify. Retry is reasonable.</summary>
    Unknown,

    /// <summary>
    /// Deezer rejected the stored session token (streamrip's ARL): expired, revoked, or invalidated
    /// by a password change. Systemic — every queued album fails the same way, and no retry helps
    /// until the ARL is replaced. See DEPLOYMENT.md §4.
    /// </summary>
    DeezerAuth,

    /// <summary>
    /// No Deezer credential is configured at all (streamrip raised MissingCredentialsError). Same
    /// systemic shape as <see cref="DeezerAuth"/>, but the fix is first-time setup rather than a
    /// refresh, so it's worth telling apart.
    /// </summary>
    DeezerCredentialsMissing,

    /// <summary>
    /// Deezer served no tracks at any quality in the chain — typically a geo-block or a pulled
    /// master. Specific to this album; other downloads are unaffected.
    /// </summary>
    NoTracksAvailable,
}

/// <summary>
/// Extensions describing what a <see cref="DownloadFailure"/> means for the queue as a whole.
/// </summary>
public static class DownloadFailureExtensions
{
    /// <summary>
    /// Whether this failure blocks <em>every</em> download rather than just its own row — i.e. the
    /// credential is bad, so draining the queue would only manufacture more identical failures. The
    /// UI uses this to raise one banner instead of decorating N rows, and the downloader uses it to
    /// stop walking the quality ladder on a pass that could never have succeeded at any quality.
    /// </summary>
    public static bool IsSystemic(this DownloadFailure failure) =>
        failure is DownloadFailure.DeezerAuth or DownloadFailure.DeezerCredentialsMissing;
}

/// <summary>
/// The result of one acquisition attempt: whether the backend took it, and — when it didn't — why,
/// so the reason can be persisted on the row and shown rather than living only in the server log.
/// </summary>
/// <param name="Accepted">True if the backend acquired/accepted the item (caller advances it to
/// <see cref="PurchaseStatus.Sent"/>).</param>
/// <param name="Failure">Why it didn't, or <see cref="DownloadFailure.None"/> when it did.</param>
/// <param name="Acquired">
/// What actually came down, which is not the same as what was asked for: the fallback ladder means a
/// lossless request routinely returns 320 for an album Deezer has no lossless master of. Only the
/// downloader can know this — the files alone can't say what was <em>wanted</em> — and it is what
/// tells a later pass whether re-requesting the album could ever do better. Null when the backend
/// couldn't say (or nothing was acquired).
/// </param>
public readonly record struct DownloadOutcome(
    bool Accepted,
    DownloadFailure Failure = DownloadFailure.None,
    AudioQuality? Acquired = null)
{
    public static DownloadOutcome Success(AudioQuality? acquired = null) => new(true, Acquired: acquired);

    public static DownloadOutcome Failed(DownloadFailure failure = DownloadFailure.Unknown) =>
        new(false, failure);
}

/// <summary>
/// The seam to an external acquisition backend (e.g. Lidarr). Phase 5 ships a no-op implementation;
/// a real target is plugged in later behind this interface without touching the purchase list or its
/// UI. The Library refresh closes the loop — once an ordered item appears in Plex its purchase row
/// flips to <see cref="PurchaseStatus.InLibrary"/> and drops off the list.
/// </summary>
public interface IDownloader
{
    /// <summary>A human-readable name for the active backend, surfaced in logs/UI.</summary>
    string Name { get; }

    /// <summary>
    /// Requests acquisition of one purchase item. Returns <see cref="DownloadOutcome.Accepted"/> true
    /// if the backend took it (the caller then advances the item to
    /// <see cref="PurchaseStatus.Sent"/>); otherwise the outcome carries why, which is persisted on
    /// the row so the Download page can explain a failure a retry won't fix. The no-op stub logs and
    /// accepts, so the manual "mark ordered" flow works today.
    /// </summary>
    Task<DownloadOutcome> Request(PurchaseItem item);
}
