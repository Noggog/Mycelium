using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// <see cref="ILibraryScanner"/> over Plex. Asks Plex to rescan the music section after albums land so
/// the catalog (and the <see cref="PurchaseStatus.InLibrary"/> flip) updates promptly instead of
/// waiting for the daily refresh.
///
/// <para><b>Debounced via Rx.</b> Callers can produce a burst of <see cref="RequestScan"/> calls — the
/// download engine asks once its queue drains, and several batches can drain in quick succession.
/// Each pushes onto a <see cref="Subject{T}"/>; <c>Throttle</c> (Rx's
/// trailing debounce) emits one value only after a window of silence, and <c>Concat</c> serializes the
/// resulting scans so they never overlap. Net effect: one scan shortly after the batch goes quiet,
/// however many albums it held.</para>
///
/// <para>The window is per-request rather than fixed, which is why this uses the duration-<i>selector</i>
/// overload of <c>Throttle</c>: a fast-mode burst asks for <see cref="LibraryScannerConfig.FastDebounce"/>
/// (seconds) instead of the normal <see cref="LibraryScannerConfig.Debounce"/> (minutes), so albums show up
/// in Plex while the user is still watching them land. Each request restarts the window with its own
/// duration, so the last request in a burst is the one that decides when the scan fires.</para>
///
/// <para>Off unless <c>PLEX_RESCAN_AFTER_DOWNLOAD</c> is set — when disabled no pipeline is built and
/// <see cref="RequestScan"/> is a no-op. Scan failures are logged, never thrown — a rescan is
/// best-effort and must not disturb the download loop.</para>
/// </summary>
public class PlexLibraryScanner : ILibraryScanner, IDisposable
{
    private readonly PlexApi _plexApi;
    private readonly LibraryScannerConfig _config;
    private readonly ILogger<PlexLibraryScanner> _logger;

    private readonly Subject<bool> _requests = new();
    private readonly IDisposable? _subscription;

    public PlexLibraryScanner(PlexApi plexApi, LibraryScannerConfig config, ILogger<PlexLibraryScanner> logger)
        : this(plexApi, config, logger, DefaultScheduler.Instance)
    {
    }

    /// <summary>Scheduler-injecting ctor so the debounce clock is deterministic under a TestScheduler.</summary>
    protected PlexLibraryScanner(
        PlexApi plexApi, LibraryScannerConfig config, ILogger<PlexLibraryScanner> logger, IScheduler scheduler)
    {
        _plexApi = plexApi;
        _config = config;
        _logger = logger;

        if (_config.Enabled)
        {
            _subscription = _requests
                .Throttle(fast => Observable.Timer(fast ? _config.FastDebounce : _config.Debounce, scheduler))
                .Select(_ => Observable.FromAsync(ScanSafely))
                .Concat()
                .Subscribe();
        }
    }

    public Task RequestScan(bool fast = false)
    {
        if (_config.Enabled)
        {
            _requests.OnNext(fast);
        }
        return Task.CompletedTask;
    }

    private async Task ScanSafely()
    {
        try
        {
            await Scan();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Targeted Plex rescan failed");
        }
    }

    /// <summary>The actual Plex hit. Virtual so the debounce pipeline can be unit-tested without HTTP.</summary>
    protected virtual async Task Scan()
    {
        var library = await _plexApi.ResolveLibrary();
        await _plexApi.RefreshLibrary(library.Key);
        _logger.LogInformation(
            "Triggered targeted Plex rescan of library {Library} ({Key}) after download activity",
            library.Title, library.Key);
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _requests.Dispose();
    }
}
