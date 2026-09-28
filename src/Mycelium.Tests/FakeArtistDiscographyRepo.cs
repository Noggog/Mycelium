using Mycelium.Interfaces;

namespace Mycelium.Tests;

/// <summary>In-memory <see cref="IArtistDiscographyRepo"/>: one discography per artist MBID, last write wins.</summary>
internal sealed class FakeArtistDiscographyRepo : IArtistDiscographyRepo
{
    private readonly Dictionary<string, ArtistDiscography> _items = new();

    public IReadOnlyDictionary<string, ArtistDiscography> Items => _items;

    public Task<ArtistDiscography?> Get(string mbid) => Task.FromResult(_items.GetValueOrDefault(mbid));

    public Task<ArtistDiscography[]> GetAll() => Task.FromResult(_items.Values.ToArray());

    public Task<Dictionary<string, DateTimeOffset>> GetExpiresAt() =>
        Task.FromResult(_items.ToDictionary(e => e.Key, e => e.Value.ExpiresAt));

    public Task Put(ArtistDiscography discography)
    {
        _items[discography.Mbid] = discography;
        return Task.CompletedTask;
    }

    public Task DeleteAllExcept(IReadOnlyCollection<string> mbids)
    {
        foreach (var id in _items.Keys.Except(mbids).ToList())
        {
            _items.Remove(id);
        }
        return Task.CompletedTask;
    }
}
