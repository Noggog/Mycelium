namespace Mycelium.Interfaces;

/// <summary>
/// The seam for asking the library backend (Plex) to rescan so freshly-downloaded albums are picked
/// up promptly — without which the <see cref="PurchaseStatus.InLibrary"/> flip waits on the next daily
/// catalog refresh. <see cref="RequestScan"/> is fire-and-forget and <b>debounced</b>: a burst of
/// requests coalesces into a single scan once they quiet, so we never hammer Plex. The debounce is the
/// second line of defence, not the first — the download engine only asks once its queue has drained,
/// because the gap between two albums routinely outlasts any sane debounce window.
/// Gated off by default (a server-wide opt-in) and a no-op when disabled.
/// </summary>
public interface ILibraryScanner
{
    /// <summary>
    /// Requests a (debounced) library rescan. Returns immediately — the actual scan runs in the
    /// background after the debounce window, and overlapping requests collapse into one.
    /// </summary>
    /// <param name="fast">
    /// True when the caller is in a fast-mode burst, which shortens the debounce window to the
    /// fast one. The last request in a burst decides the window, so a single fast request pulls a
    /// pending scan forward rather than queueing a second one.
    /// </param>
    Task RequestScan(bool fast = false);
}
