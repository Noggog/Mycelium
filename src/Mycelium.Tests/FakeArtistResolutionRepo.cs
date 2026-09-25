using Mycelium.Interfaces;

namespace Mycelium.Tests;

/// <summary>In-memory <see cref="IArtistResolutionRepo"/>: one resolution per library artist id, last write wins.</summary>
internal sealed class FakeArtistResolutionRepo : IArtistResolutionRepo
{
    private readonly Dictionary<string, ArtistResolution> _items = new();
    private readonly Dictionary<string, MusicBrainzIdentity> _pins = new();

    public IReadOnlyDictionary<string, ArtistResolution> Items => _items;

    public void Seed(ArtistResolution resolution) => _items[resolution.Id] = resolution;

    public Task<ArtistResolution[]> GetAll() => Task.FromResult(_items.Values.ToArray());

    public Task<ArtistResolution?> Get(string id) => Task.FromResult(_items.GetValueOrDefault(id));

    public Task<Dictionary<string, DateTimeOffset>> GetCheckedAt() =>
        Task.FromResult(_items.ToDictionary(e => e.Key, e => e.Value.CheckedAt));

    public Task<long> CountNeedingAttention() => Task.FromResult((long)_items.Values.Count(r => r.NeedsAttention));

    public Task Put(ArtistResolution resolution)
    {
        _items[resolution.Id] = resolution;
        return Task.CompletedTask;
    }

    public Task DeleteAllExcept(IReadOnlyCollection<string> ids)
    {
        foreach (var id in _items.Keys.Except(ids).ToList())
        {
            _items.Remove(id);
        }
        return Task.CompletedTask;
    }

    public Task<MusicBrainzIdentity?> GetPin(string id) => Task.FromResult(_pins.GetValueOrDefault(id));

    public Task SetPin(string id, MusicBrainzIdentity? identity)
    {
        if (identity is null) _pins.Remove(id);
        else _pins[id] = identity;
        return Task.CompletedTask;
    }
}
