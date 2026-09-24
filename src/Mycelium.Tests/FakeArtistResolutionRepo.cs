using Mycelium.Interfaces;

namespace Mycelium.Tests;

/// <summary>In-memory <see cref="IArtistResolutionRepo"/>: one resolution per artist name, last write wins.</summary>
internal sealed class FakeArtistResolutionRepo : IArtistResolutionRepo
{
    private readonly Dictionary<string, ArtistResolution> _items = new();

    public IReadOnlyDictionary<string, ArtistResolution> Items => _items;

    public void Seed(ArtistResolution resolution) => _items[resolution.Artist] = resolution;

    public Task<ArtistResolution[]> GetAll() => Task.FromResult(_items.Values.ToArray());

    public Task<ArtistResolution?> Get(string artist) => Task.FromResult(_items.GetValueOrDefault(artist));

    public Task<Dictionary<string, DateTimeOffset>> GetCheckedAt() =>
        Task.FromResult(_items.ToDictionary(e => e.Key, e => e.Value.CheckedAt));

    public Task<long> CountNeedingAttention() => Task.FromResult((long)_items.Values.Count(r => r.NeedsAttention));

    public Task Put(ArtistResolution resolution)
    {
        _items[resolution.Artist] = resolution;
        return Task.CompletedTask;
    }

    public Task DeleteAllExcept(IReadOnlyCollection<string> artists)
    {
        foreach (var name in _items.Keys.Except(artists).ToList())
        {
            _items.Remove(name);
        }
        return Task.CompletedTask;
    }
}
