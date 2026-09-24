using System.Collections.Concurrent;
using FluentAssertions;
using Mycelium.ListenBrainz.Inputs;
using Mycelium.ListenBrainz.Services;
using Xunit;

namespace Mycelium.Tests;

public class MusicBrainzGateTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    // Timer resolution on a loaded CI box: the spacing is asserted with this much slack.
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(15);

    private readonly MusicBrainzGate _sut = new(
        new ListenBrainzEndpointInfo("https://mb", "https://lb", "contact", "algo", Enabled: true, Interval));

    public void Dispose() => _sut.Dispose();

    [Fact]
    public async Task Requests_run_one_at_a_time_and_start_at_least_the_interval_apart()
    {
        var running = 0;
        var overlapped = false;
        var starts = new ConcurrentQueue<DateTimeOffset>();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _sut.Run(async () =>
        {
            if (Interlocked.Increment(ref running) > 1) overlapped = true;
            starts.Enqueue(DateTimeOffset.UtcNow);
            // Slower than the interval: spacing starts alone would let the next one overlap this.
            await Task.Delay(Interval * 1.5);
            Interlocked.Decrement(ref running);
            return 0;
        })));

        overlapped.Should().BeFalse();
        var times = starts.ToArray();
        for (var i = 1; i < times.Length; i++)
        {
            (times[i] - times[i - 1]).Should().BeGreaterThanOrEqualTo(Interval - Slack);
        }
    }

    [Fact]
    public async Task An_interactive_request_goes_ahead_of_background_ones_already_waiting()
    {
        var order = new ConcurrentQueue<string>();
        var holding = new TaskCompletionSource();

        // Occupies the gate so everything below has to queue.
        var first = _sut.Run(async () => { await holding.Task; return 0; });

        Task<int> background1, background2;
        using (MusicBrainzGate.Background())
        {
            background1 = _sut.Run(() => { order.Enqueue("background 1"); return Task.FromResult(0); });
            background2 = _sut.Run(() => { order.Enqueue("background 2"); return Task.FromResult(0); });
        }
        var interactive = _sut.Run(() => { order.Enqueue("interactive"); return Task.FromResult(0); });

        holding.SetResult();
        await Task.WhenAll(first, background1, background2, interactive);

        order.Should().Equal("interactive", "background 1", "background 2");
    }

    [Fact]
    public async Task A_declined_request_is_retried_after_the_wait_and_holds_up_everything_behind_it()
    {
        var retryAfter = TimeSpan.FromMilliseconds(300);
        var attempts = new ConcurrentQueue<DateTimeOffset>();
        var declined = _sut.Run(() =>
        {
            attempts.Enqueue(DateTimeOffset.UtcNow);
            if (attempts.Count == 1) throw new MusicBrainzBusyException(retryAfter);
            return Task.FromResult("answered");
        });
        DateTimeOffset? behindAt = null;
        var behind = _sut.Run(() =>
        {
            behindAt = DateTimeOffset.UtcNow;
            return Task.FromResult(0);
        });

        (await declined).Should().Be("answered");
        await behind;

        var times = attempts.ToArray();
        times.Should().HaveCount(2);
        (times[1] - times[0]).Should().BeGreaterThanOrEqualTo(retryAfter - Slack);
        // The retry kept its place at the front: nothing else slipped in during the pause.
        behindAt.Should().BeOnOrAfter(times[1]);
    }

    [Fact]
    public async Task A_request_still_declined_after_every_retry_fails_to_its_caller()
    {
        var attempts = 0;
        var run = _sut.Run<int>(() =>
        {
            attempts++;
            throw new MusicBrainzBusyException(TimeSpan.FromMilliseconds(1));
        });

        await run.Invoking(t => t).Should().ThrowAsync<MusicBrainzBusyException>();
        attempts.Should().Be(MusicBrainzGate.MaxRetries + 1);
    }

    [Fact]
    public async Task Any_other_failure_reaches_the_caller_and_the_gate_keeps_serving()
    {
        var failing = _sut.Run<int>(() => throw new InvalidOperationException("boom"));
        var after = _sut.Run(() => Task.FromResult(7));

        await failing.Invoking(t => t).Should().ThrowAsync<InvalidOperationException>();
        (await after).Should().Be(7);
    }

    [Fact]
    public void Background_priority_lasts_until_the_scope_is_disposed()
    {
        MusicBrainzGate.CurrentPriority.Should().Be(MusicBrainzPriority.Interactive);
        using (MusicBrainzGate.Background())
        {
            MusicBrainzGate.CurrentPriority.Should().Be(MusicBrainzPriority.Background);
        }
        MusicBrainzGate.CurrentPriority.Should().Be(MusicBrainzPriority.Interactive);
    }

    [Fact]
    public void Backoff_takes_the_sources_word_and_otherwise_doubles_up_to_a_cap()
    {
        MusicBrainzGate.Backoff(1, TimeSpan.FromSeconds(5)).Should().Be(TimeSpan.FromSeconds(5));
        MusicBrainzGate.Backoff(1, null).Should().Be(TimeSpan.FromSeconds(2));
        MusicBrainzGate.Backoff(2, null).Should().Be(TimeSpan.FromSeconds(4));
        MusicBrainzGate.Backoff(10, null).Should().Be(TimeSpan.FromSeconds(60));
        MusicBrainzGate.Backoff(1, TimeSpan.FromMinutes(10)).Should().Be(TimeSpan.FromSeconds(60));
    }
}
