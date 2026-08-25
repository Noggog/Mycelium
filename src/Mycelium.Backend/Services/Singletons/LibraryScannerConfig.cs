// Lives in the Mycelium.Backend root namespace (NOT Services.Singletons) on purpose: MainModule's
// assembly scan sweeps Services.Singletons AsSelf via reflection, which would shadow the env-built
// RegisterInstance below with a non-constructable reflection registration (no parameterless ctor) and
// fail activation. Config records belong outside the scanned namespace — same as RelatedStalenessPolicy.
namespace Mycelium.Backend;

/// <summary>
/// Configuration for the post-download Plex rescan, read from environment variables in MainModule
/// (no hardcoded config). <see cref="Enabled"/> (<c>PLEX_RESCAN_AFTER_DOWNLOAD</c>) is a server-wide
/// opt-in, off by default; <see cref="Debounce"/> (<c>PLEX_RESCAN_DEBOUNCE_MINUTES</c>) is how long
/// downloads must quiet down before a single coalesced scan fires.
///
/// <para><see cref="FastDebounce"/> (<c>PLEX_RESCAN_FAST_DEBOUNCE_SECONDS</c>) is the same window
/// during a fast-mode burst, where the whole point is that nothing waits on the next tick: albums land
/// back-to-back and the user is watching the panel, so a five-minute settle would leave the queue
/// looking stalled long after the files are on disk. Never longer than <see cref="Debounce"/> —
/// a deployment that already scans more eagerly than this keeps its own pace.</para>
/// </summary>
public record LibraryScannerConfig(bool Enabled, TimeSpan Debounce, TimeSpan FastDebounce);
