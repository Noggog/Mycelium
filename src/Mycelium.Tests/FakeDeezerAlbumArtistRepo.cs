using Mycelium.Interfaces;

namespace Mycelium.Tests;

/// <summary>
/// In-memory <see cref="IDeezerAlbumArtistRepo"/>. Mirrors the Mongo upsert (one entry per Deezer album
/// id, last write wins) so a test can both seed the memo — standing in for what an earlier sweep
/// learned — and assert on what a run wrote back into it.
/// </summary>
internal sealed class FakeDeezerAlbumArtistRepo : IDeezerAlbumArtistRepo
{
    private readonly Dictionary<long, string> _items = new();

    public IReadOnlyDictionary<long, string> Items => _items;

    public void Seed(long albumId, string artist) => _items[albumId] = artist;

    public Task<Dictionary<long, string>> Get(IReadOnlyCollection<long> albumIds) =>
        Task.FromResult(albumIds
            .Distinct()
            .Where(_items.ContainsKey)
            .ToDictionary(id => id, id => _items[id]));

    public Task Put(IReadOnlyDictionary<long, string> artistsByAlbumId)
    {
        foreach (var (id, artist) in artistsByAlbumId)
        {
            _items[id] = artist;
        }
        return Task.CompletedTask;
    }
}
