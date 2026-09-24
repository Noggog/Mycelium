using Mycelium.Interfaces;

namespace Mycelium.Tests;

/// <summary>
/// In-memory <see cref="ISourceCacheStore"/>, standing in for the Mongo collection. Shared between two
/// <see cref="Backend.Services.Singletons.SourceCache"/> instances, it plays the part of what survives
/// a restart.
/// </summary>
internal sealed class FakeSourceCacheStore : ISourceCacheStore
{
    private readonly Dictionary<string, SourceCacheEntry> _entries = new();

    public IReadOnlyDictionary<string, SourceCacheEntry> Entries => _entries;

    public Task<SourceCacheEntry?> Get(string key) =>
        Task.FromResult(_entries.GetValueOrDefault(key));

    public Task Put(SourceCacheEntry entry)
    {
        _entries[entry.Key] = entry;
        return Task.CompletedTask;
    }

    public Task Expire(string key, DateTimeOffset at)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            _entries[key] = entry with { ExpiresAt = at };
        }
        return Task.CompletedTask;
    }
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
