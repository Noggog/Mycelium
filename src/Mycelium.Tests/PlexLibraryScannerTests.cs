using System.Reactive.Concurrency;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;
using Mycelium.Backend;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Plex;
using Mycelium.Plex.Services.Singletons;
using Xunit;

namespace Mycelium.Tests;

public class PlexLibraryScannerTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FastDebounce = TimeSpan.FromSeconds(30);

    // A scanner whose actual Plex hit is replaced by a counter, so we test the debounce/gate logic
    // without any HTTP, and on a TestScheduler so the debounce clock is deterministic (no Task.Delay).
    // The PlexApi instance only satisfies the base ctor — it's never touched (Scan() is overridden).
    private sealed class TestScanner : PlexLibraryScanner
    {
        public int ScanCount;

        public TestScanner(LibraryScannerConfig config, IScheduler scheduler)
            : base(
                new PlexApi(new PlexEndpointInfo("http://localhost"), new StaticPlexTokenSource("token"),
                    NullLogger<PlexApi>.Instance),
                config,
                NullLogger<PlexLibraryScanner>.Instance,
                scheduler)
        {
        }

        protected override Task Scan()
        {
            Interlocked.Increment(ref ScanCount);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Disabled_scanner_never_scans()
    {
        var scheduler = new TestScheduler();
        var sut = new TestScanner(new LibraryScannerConfig(Enabled: false, Debounce, FastDebounce), scheduler);

        await sut.RequestScan();
        scheduler.AdvanceBy(Debounce.Ticks * 2);

        sut.ScanCount.Should().Be(0);
    }

    [Fact]
    public async Task Enabled_scanner_scans_once_the_debounce_window_elapses()
    {
        var scheduler = new TestScheduler();
        var sut = new TestScanner(new LibraryScannerConfig(Enabled: true, Debounce, FastDebounce), scheduler);

        await sut.RequestScan();
        sut.ScanCount.Should().Be(0);      // nothing until the quiet window passes

        scheduler.AdvanceBy(Debounce.Ticks + 1);
        sut.ScanCount.Should().Be(1);
    }

    [Fact]
    public async Task A_burst_of_requests_coalesces_into_a_single_scan()
    {
        // A draining batch fires many RequestScan calls in quick succession; the trailing debounce
        // folds them into exactly one scan once the window finally elapses.
        var scheduler = new TestScheduler();
        var sut = new TestScanner(new LibraryScannerConfig(Enabled: true, Debounce, FastDebounce), scheduler);

        await sut.RequestScan();
        await sut.RequestScan();
        await sut.RequestScan();

        scheduler.AdvanceBy(Debounce.Ticks + 1);

        sut.ScanCount.Should().Be(1);
    }

    [Fact]
    public async Task A_later_batch_triggers_a_fresh_scan()
    {
        var scheduler = new TestScheduler();
        var sut = new TestScanner(new LibraryScannerConfig(Enabled: true, Debounce, FastDebounce), scheduler);

        await sut.RequestScan();
        scheduler.AdvanceBy(Debounce.Ticks + 1);
        sut.ScanCount.Should().Be(1);

        // A second, later burst (after the first scan fired) produces its own scan.
        await sut.RequestScan();
        scheduler.AdvanceBy(Debounce.Ticks + 1);
        sut.ScanCount.Should().Be(2);
    }

    [Fact]
    public async Task A_fast_mode_request_scans_on_the_short_window()
    {
        var scheduler = new TestScheduler();
        var sut = new TestScanner(new LibraryScannerConfig(Enabled: true, Debounce, FastDebounce), scheduler);

        await sut.RequestScan(fast: true);

        scheduler.AdvanceBy(FastDebounce.Ticks + 1);
        sut.ScanCount.Should().Be(1);   // not the five-minute wait
    }

    [Fact]
    public async Task A_fast_request_mid_burst_pulls_the_pending_scan_forward()
    {
        // Fast mode flips on partway through a draining batch: the window is re-decided per request,
        // so the burst settles on the short one rather than serving out the normal window it started with.
        var scheduler = new TestScheduler();
        var sut = new TestScanner(new LibraryScannerConfig(Enabled: true, Debounce, FastDebounce), scheduler);

        await sut.RequestScan();
        await sut.RequestScan(fast: true);

        scheduler.AdvanceBy(FastDebounce.Ticks + 1);
        sut.ScanCount.Should().Be(1);
    }

    [Fact]
    public async Task A_burst_of_fast_requests_still_coalesces_into_one_scan()
    {
        var scheduler = new TestScheduler();
        var sut = new TestScanner(new LibraryScannerConfig(Enabled: true, Debounce, FastDebounce), scheduler);

        await sut.RequestScan(fast: true);
        await sut.RequestScan(fast: true);
        await sut.RequestScan(fast: true);

        scheduler.AdvanceBy(FastDebounce.Ticks + 1);
        sut.ScanCount.Should().Be(1);
    }
}
