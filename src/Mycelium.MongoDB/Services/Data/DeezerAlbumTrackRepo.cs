using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed <see cref="IDeezerAlbumTrackRepo"/>. One doc per Deezer album in the
/// "deezerAlbumTracks" collection, keyed by the album id itself — the id is Deezer's, so it needs no
/// derived key, and an upsert on it makes re-learning the same album a no-op. Shaped exactly like
/// <see cref="DeezerAlbumArtistRepo"/>, whose memo it sits beside.
/// </summary>
public class DeezerAlbumTrackRepo : IDeezerAlbumTrackRepo
{
    private const string CollectionName = "deezerAlbumTracks";
    private const string FieldTitles = "titles";

    private readonly IMongoDbProvider _mongoDbProvider;

    public DeezerAlbumTrackRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    public async Task<Dictionary<long, IReadOnlyList<string>>> Get(IReadOnlyCollection<long> albumIds)
    {
        var result = new Dictionary<long, IReadOnlyList<string>>();
        if (albumIds.Count == 0) return result;

        var cursor = await Collection.FindAsync(
            Builders<BsonDocument>.Filter.In("_id", albumIds.Distinct()),
            new FindOptions<BsonDocument>
            {
                Projection = Builders<BsonDocument>.Projection.Include(FieldTitles),
            });

        foreach (var doc in await cursor.ToListAsync())
        {
            if (!doc.TryGetValue(FieldTitles, out var titles) || !titles.IsBsonArray)
            {
                continue;
            }

            result[doc["_id"].ToInt64()] = titles.AsBsonArray
                .Where(t => !t.IsBsonNull)
                .Select(t => t.AsString)
                .ToList();
        }

        return result;
    }

    public Task Put(IReadOnlyDictionary<long, IReadOnlyList<string>> titlesByAlbumId)
    {
        if (titlesByAlbumId.Count == 0) return Task.CompletedTask;

        var writes = titlesByAlbumId
            .Select(e => new UpdateOneModel<BsonDocument>(
                Builders<BsonDocument>.Filter.Eq("_id", e.Key),
                Builders<BsonDocument>.Update.Set(FieldTitles, new BsonArray(e.Value)))
            { IsUpsert = true })
            .ToList<WriteModel<BsonDocument>>();

        return Collection.BulkWriteAsync(writes);
    }
}
