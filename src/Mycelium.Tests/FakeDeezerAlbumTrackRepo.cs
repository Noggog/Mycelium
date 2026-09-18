using Mycelium.Interfaces;

namespace Mycelium.Tests;

/// <summary>
/// In-memory <see cref="IDeezerAlbumTrackRepo"/>. Mirrors the Mongo upsert (one entry per Deezer album
/// id, last write wins) so a test can both seed the memo — standing in for what an earlier sweep
/// learned — and assert on what a run wrote back into it. The sibling of
/// <see cref="FakeDeezerAlbumArtistRepo"/>, and shaped the same way for the same reasons.
/// </summary>
internal sealed class FakeDeezerAlbumTrackRepo : IDeezerAlbumTrackRepo
{
    private readonly Dictionary<long, IReadOnlyList<string>> _items = new();

    public IReadOnlyDictionary<long, IReadOnlyList<string>> Items => _items;

    public void Seed(long albumId, params string[] titles) => _items[albumId] = titles;

    public Task<Dictionary<long, IReadOnlyList<string>>> Get(IReadOnlyCollection<long> albumIds) =>
        Task.FromResult(albumIds
            .Distinct()
            .Where(_items.ContainsKey)
            .ToDictionary(id => id, id => _items[id]));

    public Task Put(IReadOnlyDictionary<long, IReadOnlyList<string>> titlesByAlbumId)
    {
        foreach (var (id, titles) in titlesByAlbumId)
        {
            _items[id] = titles;
        }
        return Task.CompletedTask;
    }
}
