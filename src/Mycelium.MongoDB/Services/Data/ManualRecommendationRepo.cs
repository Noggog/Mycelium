using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed hand-entered recommendations, one document per pair in "manualRecommendations".
/// Deliberately not rows in "relatedArtists": that collection is the sources' cache, rewritten per
/// (artist, source) and wiped wholesale when an artist's source link is detached, and a pair someone
/// typed in must survive both.
/// </summary>
public class ManualRecommendationRepo : IManualRecommendationRepo
{
    private const string CollectionName = "manualRecommendations";
    private const string FieldArtistA = "artistA";
    private const string FieldArtistB = "artistB";
    private const string FieldAddedBy = "addedBy";
    private const string FieldAddedAt = "addedAt";

    private readonly IMongoDbProvider _mongoDbProvider;

    public ManualRecommendationRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    public async Task<IReadOnlyList<ManualRecommendation>> GetAll()
    {
        var cursor = await Collection.FindAsync(Builders<BsonDocument>.Filter.Empty);
        return (await cursor.ToListAsync()).Select(ToRecommendation).ToArray();
    }

    public async Task Upsert(ManualRecommendation recommendation)
    {
        var doc = new BsonDocument
        {
            { "_id", recommendation.Id },
            { FieldArtistA, recommendation.ArtistA },
            { FieldArtistB, recommendation.ArtistB },
            { FieldAddedBy, (BsonValue?)recommendation.AddedBy ?? BsonNull.Value },
            { FieldAddedAt, recommendation.AddedAt.UtcDateTime },
        };

        await Collection.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", recommendation.Id),
            doc,
            new ReplaceOptions { IsUpsert = true });
    }

    public async Task<bool> Delete(string id)
    {
        var result = await Collection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", id));
        return result.DeletedCount > 0;
    }

    private static ManualRecommendation ToRecommendation(BsonDocument doc)
    {
        static string? Str(BsonDocument d, string field) =>
            d.TryGetValue(field, out var v) && v.IsString ? v.AsString : null;

        var addedAt = doc.TryGetValue(FieldAddedAt, out var at) && at.IsValidDateTime
            ? new DateTimeOffset(at.ToUniversalTime(), TimeSpan.Zero)
            : default;

        return new ManualRecommendation(
            doc["_id"].AsString,
            Str(doc, FieldArtistA) ?? "",
            Str(doc, FieldArtistB) ?? "",
            Str(doc, FieldAddedBy),
            addedAt);
    }
}
