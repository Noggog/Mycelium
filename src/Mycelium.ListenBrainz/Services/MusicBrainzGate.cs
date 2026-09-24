using Mycelium.ListenBrainz.Inputs;

namespace Mycelium.ListenBrainz.Services;

/// <summary>Which queue a MusicBrainz request waits in. See <see cref="MusicBrainzGate"/>.</summary>
public enum MusicBrainzPriority
{
    /// <summary>Someone is waiting on a page. The default.</summary>
    Interactive,

    /// <summary>A sweep or backfill. Only runs when no interactive request is waiting.</summary>
    Background,
}

/// <summary>
/// Thrown by a unit of work to say MusicBrainz declined it (HTTP 503, or 429) and it should be run
/// again after <see cref="RetryAfter"/> — or after the gate's own backoff when MusicBrainz didn't say.
/// </summary>
public sealed class MusicBrainzBusyException(TimeSpan? retryAfter)
    : Exception("MusicBrainz declined the request (rate limited)")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// The single door every MusicBrainz request goes through: one request in flight at a time, request
/// starts spaced at least <see cref="ListenBrainzEndpointInfo.MusicBrainzMinInterval"/> apart, and
/// interactive requests served before background ones.
///
/// <para>MusicBrainz allows about one request per second per IP and answers anything faster with a
/// 503. Spacing starts alone is not enough — a slow response would otherwise overlap the next request
/// — so work is fully serialised through one worker. A 503 pauses the <em>whole</em> queue, not just
/// the request that hit it: whatever tripped the limit (a burst from elsewhere on the same IP) is still
/// in effect for the next request too. The declined request keeps its place at the front.</para>
///
/// <para>Priority is ambient rather than a parameter on every call: a background pass wraps itself in
/// <see cref="Background"/> once, and every request it makes — however deep in the call graph — waits
/// behind anything a user is waiting on.</para>
/// </summary>
public sealed class MusicBrainzGate : IDisposable
{
    /// <summary>How many times a declined request is retried before its caller is told it failed.</summary>
    public const int MaxRetries = 3;

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private static readonly AsyncLocal<MusicBrainzPriority?> Ambient = new();

    private readonly TimeSpan _interval;
    private readonly object _lock = new();
    private readonly LinkedList<Ticket> _interactive = new();
    private readonly LinkedList<Ticket> _background = new();
    private readonly SemaphoreSlim _pending = new(0);
    private readonly CancellationTokenSource _stop = new();
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;

    public MusicBrainzGate(ListenBrainzEndpointInfo endpointInfo)
    {
        _interval = endpointInfo.MusicBrainzMinInterval;
        _ = Task.Run(Pump);
    }

    /// <summary>The priority requests made from the current async flow are queued at.</summary>
    public static MusicBrainzPriority CurrentPriority => Ambient.Value ?? MusicBrainzPriority.Interactive;

    /// <summary>
    /// Marks every MusicBrainz request made from the current async flow, until disposed, as background
    /// work. <c>using var _ = MusicBrainzGate.Background();</c> at the top of a sweep.
    /// </summary>
    public static IDisposable Background()
    {
        var prior = Ambient.Value;
        Ambient.Value = MusicBrainzPriority.Background;
        return new Restore(prior);
    }

    /// <summary>
    /// Queues <paramref name="work"/> — which should make exactly one HTTP request — and completes with
    /// its result once it has run. <paramref name="work"/> throws <see cref="MusicBrainzBusyException"/>
    /// to be retried; after <see cref="MaxRetries"/> retries that exception reaches the caller.
    /// </summary>
    public Task<T> Run<T>(Func<Task<T>> work, CancellationToken cancellationToken = default)
    {
        var ticket = new Ticket<T>(work, cancellationToken);
        lock (_lock)
        {
            (CurrentPriority == MusicBrainzPriority.Background ? _background : _interactive).AddLast(ticket);
        }
        _pending.Release();
        return ticket.Task;
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_lock)
        {
            foreach (var ticket in _interactive.Concat(_background))
            {
                ticket.Cancel();
            }
            _interactive.Clear();
            _background.Clear();
        }
    }

    private async Task Pump()
    {
        try
        {
            while (true)
            {
                await _pending.WaitAsync(_stop.Token);

                // Wait out the spacing *before* choosing what to run, so an interactive request that
                // arrives during the wait still goes ahead of a background one already queued.
                var wait = _nextAllowed - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _stop.Token);
                }

                var ticket = Dequeue();
                if (ticket.IsCancelled)
                {
                    ticket.Cancel();
                    continue;
                }

                _nextAllowed = DateTimeOffset.UtcNow + _interval;
                var retryAfter = await ticket.Execute();
                if (retryAfter is { } delay)
                {
                    // Declined: pause everything, and put this request back at the front of its queue.
                    _nextAllowed = DateTimeOffset.UtcNow + Max(delay, _interval);
                    Requeue(ticket);
                    _pending.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private Ticket Dequeue()
    {
        lock (_lock)
        {
            var queue = _interactive.Count > 0 ? _interactive : _background;
            var ticket = queue.First!.Value;
            queue.RemoveFirst();
            return ticket;
        }
    }

    private void Requeue(Ticket ticket)
    {
        lock (_lock)
        {
            (ticket.Priority == MusicBrainzPriority.Background ? _background : _interactive).AddFirst(ticket);
        }
    }

    /// <summary>The wait before retry <paramref name="attempt"/> (1-based): MusicBrainz's word if it gave one.</summary>
    internal static TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        var delay = retryAfter ?? TimeSpan.FromSeconds(2 * Math.Pow(2, attempt - 1));
        return delay > MaxBackoff ? MaxBackoff : delay;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private abstract class Ticket(CancellationToken cancellationToken)
    {
        public MusicBrainzPriority Priority { get; } = CurrentPriority;
        protected CancellationToken CancellationToken { get; } = cancellationToken;
        public bool IsCancelled => CancellationToken.IsCancellationRequested;
        protected int Attempts { get; set; }

        /// <summary>Runs the work. Returns how long to pause before retrying it, or null when it finished.</summary>
        public abstract Task<TimeSpan?> Execute();

        public abstract void Cancel();
    }

    private sealed class Ticket<T>(Func<Task<T>> work, CancellationToken cancellationToken)
        : Ticket(cancellationToken)
    {
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _completion.Task;

        public override async Task<TimeSpan?> Execute()
        {
            try
            {
                _completion.TrySetResult(await work());
                return null;
            }
            catch (MusicBrainzBusyException busy) when (Attempts < MaxRetries)
            {
                Attempts++;
                return Backoff(Attempts, busy.RetryAfter);
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
                return null;
            }
        }

        public override void Cancel() => _completion.TrySetCanceled(CancellationToken);
    }

    private sealed class Restore(MusicBrainzPriority? prior) : IDisposable
    {
        public void Dispose() => Ambient.Value = prior;
    }
}
