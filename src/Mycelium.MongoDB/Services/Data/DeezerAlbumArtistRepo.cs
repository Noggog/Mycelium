using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed <see cref="IDeezerAlbumArtistRepo"/>. One doc per Deezer album in the
/// "deezerAlbumArtists" collection, keyed by the album id itself — the id is Deezer's, so it needs no
/// derived key, and an upsert on it makes re-learning the same album a no-op.
/// </summary>
public class DeezerAlbumArtistRepo : IDeezerAlbumArtistRepo
{
    private const string CollectionName = "deezerAlbumArtists";
    private const string FieldArtist = "artist";

    private readonly IMongoDbProvider _mongoDbProvider;

    public DeezerAlbumArtistRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    public async Task<Dictionary<long, string>> Get(IReadOnlyCollection<long> albumIds)
    {
        var result = new Dictionary<long, string>();
        if (albumIds.Count == 0) return result;

        var cursor = await Collection.FindAsync(
            Builders<BsonDocument>.Filter.In("_id", albumIds.Distinct()),
            new FindOptions<BsonDocument>
            {
                Projection = Builders<BsonDocument>.Projection.Include(FieldArtist),
            });

        foreach (var doc in await cursor.ToListAsync())
        {
            if (doc.TryGetValue(FieldArtist, out var artist) && !artist.IsBsonNull)
            {
                result[doc["_id"].ToInt64()] = artist.AsString;
            }
        }

        return result;
    }

    public Task Put(IReadOnlyDictionary<long, string> artistsByAlbumId)
    {
        if (artistsByAlbumId.Count == 0) return Task.CompletedTask;

        var writes = artistsByAlbumId
            .Select(e => new UpdateOneModel<BsonDocument>(
                Builders<BsonDocument>.Filter.Eq("_id", e.Key),
                Builders<BsonDocument>.Update.Set(FieldArtist, e.Value)) { IsUpsert = true })
            .ToList<WriteModel<BsonDocument>>();

        return Collection.BulkWriteAsync(writes);
    }
}
