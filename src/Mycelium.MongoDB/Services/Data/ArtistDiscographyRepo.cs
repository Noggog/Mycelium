using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed <see cref="IArtistDiscographyRepo"/>. One doc per MusicBrainz artist in the
/// "artistDiscography" collection, keyed by the artist MBID.
///
/// <para>The discography sits whole under <c>data</c>, as real nested fields rather than a JSON string, so
/// it can be queried into later (Discover reading release groups straight from here). <c>expiresAt</c> is
/// also kept at the top level as a date, for picking the ones due.</para>
/// </summary>
public class ArtistDiscographyRepo : IArtistDiscographyRepo
{
    private const string CollectionName = "artistDiscography";
    private const string FieldExpiresAt = "expiresAt";
    private const string FieldData = "data";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IMongoDbProvider _mongoDbProvider;

    public ArtistDiscographyRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    public async Task<ArtistDiscography?> Get(string mbid)
    {
        var doc = await Collection.Find(Builders<BsonDocument>.Filter.Eq("_id", mbid)).FirstOrDefaultAsync();
        return doc is null ? null : FromDocument(doc);
    }

    public async Task<ArtistDiscography[]> GetAll()
    {
        var docs = await Collection.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        return docs.Select(FromDocument).OfType<ArtistDiscography>().ToArray();
    }

    public async Task<Dictionary<string, DateTimeOffset>> GetExpiresAt()
    {
        var docs = await Collection
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Project(Builders<BsonDocument>.Projection.Include(FieldExpiresAt))
            .ToListAsync();
        return docs.ToDictionary(
            d => d["_id"].AsString,
            d => new DateTimeOffset(d[FieldExpiresAt].ToUniversalTime()));
    }

    public Task Put(ArtistDiscography discography) =>
        Collection.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", discography.Mbid),
            ToDocument(discography),
            new ReplaceOptions { IsUpsert = true });

    public Task DeleteAllExcept(IReadOnlyCollection<string> mbids) =>
        Collection.DeleteManyAsync(Builders<BsonDocument>.Filter.Nin("_id", mbids));

    public static BsonDocument ToDocument(ArtistDiscography discography) => new()
    {
        ["_id"] = discography.Mbid,
        [FieldExpiresAt] = discography.ExpiresAt.UtcDateTime,
        [FieldData] = BsonDocument.Parse(JsonSerializer.Serialize(discography, Json)),
    };

    /// <summary>Null for a doc whose data can't be read back (a shape from before a change); it is rebuilt.</summary>
    public static ArtistDiscography? FromDocument(BsonDocument doc)
    {
        if (!doc.TryGetValue(FieldData, out var data) || !data.IsBsonDocument)
        {
            return null;
        }

        var json = data.AsBsonDocument.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson });
        try
        {
            return JsonSerializer.Deserialize<ArtistDiscography>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
