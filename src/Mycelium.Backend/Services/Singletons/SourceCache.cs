using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mycelium.Interfaces;
using Mycelium.ListenBrainz.Services;
using Newtonsoft.Json;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// A cache of answers from rate-limited outside sources (MusicBrainz above all), in two layers: memory
/// in front, <see cref="ISourceCacheStore"/> (Mongo) behind.
///
/// <list type="bullet">
/// <item>A read that misses memory loads from the store, so a restart costs no source calls — memory
/// refills as things are asked for, rather than all at once at startup.</item>
/// <item>Each entry expires on its own clock, set by the caller from the answer itself (an artist
/// releasing things this year is worth re-asking about sooner than one who stopped in 1994). Staleness
/// is nothing more than an entry past its expiry.</item>
/// <item>A stale entry is still returned, and a refresh is queued behind it at background priority. At
/// one request a second, making a page wait on a refetch it could have rendered without is the wrong
/// trade.</item>
/// <item>A source that doesn't answer (the fetch returns null) never replaces what's cached: the stale
/// answer keeps being served until a real one arrives.</item>
/// </list>
/// </summary>
public sealed class SourceCache
{
    private readonly ISourceCacheStore _store;
    private readonly ILogger<SourceCache> _logger;
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<string, Memo> _memory = new();
    private readonly ConcurrentDictionary<string, Task> _refreshing = new();

    public SourceCache(ISourceCacheStore store, ILogger<SourceCache> logger)
        : this(store, logger, TimeProvider.System)
    {
    }

    internal SourceCache(ISourceCacheStore store, ILogger<SourceCache> logger, TimeProvider time)
    {
        _store = store;
        _logger = logger;
        _time = time;
    }

    /// <summary>
    /// The cached answer under <paramref name="key"/>, fetching it when there is none.
    /// </summary>
    /// <param name="fetch">Asks the source. Null means the source didn't answer.</param>
    /// <param name="lifetime">How long a given answer stays fresh.</param>
    /// <param name="fresh">
    /// Ask the source now whatever is cached — for a user who just corrected the source and wants to
    /// see it. Still falls back to the cached answer if the source doesn't answer.
    /// </param>
    /// <returns>Null only when nothing is cached and the source didn't answer.</returns>
    public async Task<T?> GetOrFetch<T>(
        string key, Func<Task<T?>> fetch, Func<T, TimeSpan> lifetime, bool fresh = false)
        where T : class
    {
        var cached = await Read<T>(key);
        if (cached is not null && !fresh)
        {
            if (cached.ExpiresAt <= _time.GetUtcNow())
            {
                QueueRefresh(key, fetch, lifetime);
            }
            return (T)cached.Value;
        }

        return await Fetch(key, fetch, lifetime) ?? (T?)cached?.Value;
    }

    /// <summary>
    /// Marks the entry as due now: the next read still gets the cached answer, and queues a refresh.
    /// For when something says the source has changed before the entry's own clock would notice.
    /// </summary>
    public async Task Expire(string key)
    {
        var now = _time.GetUtcNow();
        if (_memory.TryGetValue(key, out var memo))
        {
            _memory[key] = memo with { ExpiresAt = now };
        }
        await _store.Expire(key, now);
    }

    /// <summary>Completes once no refresh is running. For tests.</summary>
    internal Task WhenIdle() => Task.WhenAll(_refreshing.Values);

    private async Task<Memo?> Read<T>(string key)
    {
        if (_memory.TryGetValue(key, out var memo))
        {
            return memo;
        }

        SourceCacheEntry? entry;
        try
        {
            entry = await _store.Get(key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Source cache could not read {Key}; treating it as uncached", key);
            return null;
        }

        if (entry is null || JsonConvert.DeserializeObject<T>(entry.Json) is not { } value)
        {
            return null;
        }

        return _memory.GetOrAdd(key, new Memo(value, entry.ExpiresAt));
    }

    private async Task<T?> Fetch<T>(string key, Func<Task<T?>> fetch, Func<T, TimeSpan> lifetime)
        where T : class
    {
        var value = await fetch();
        if (value is null)
        {
            return null;
        }

        var fetchedAt = _time.GetUtcNow();
        var expiresAt = fetchedAt + lifetime(value);
        _memory[key] = new Memo(value, expiresAt);
        try
        {
            await _store.Put(new SourceCacheEntry(key, JsonConvert.SerializeObject(value), fetchedAt, expiresAt));
        }
        catch (Exception ex)
        {
            // Memory has it; only a restart would lose it.
            _logger.LogWarning(ex, "Source cache could not persist {Key}", key);
        }
        return value;
    }

    /// <summary>Starts a background refresh of <paramref name="key"/> unless one is already running.</summary>
    private void QueueRefresh<T>(string key, Func<Task<T?>> fetch, Func<T, TimeSpan> lifetime)
        where T : class
    {
        var started = new TaskCompletionSource();
        var task = started.Task.ContinueWith(_ => Refresh(), TaskScheduler.Default).Unwrap();
        if (_refreshing.TryAdd(key, task))
        {
            started.SetResult();
        }

        async Task Refresh()
        {
            try
            {
                using var background = MusicBrainzGate.Background();
                await Fetch(key, fetch, lifetime);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Source cache refresh of {Key} failed; still serving the stale answer", key);
            }
            finally
            {
                _refreshing.TryRemove(key, out _);
            }
        }
    }

    /// <param name="Value">The deserialised answer, kept as-is so a hit costs no parsing.</param>
    private sealed record Memo(object Value, DateTimeOffset ExpiresAt);
}
