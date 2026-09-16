using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed similarity graph. One document per (artist, source) in the "relatedArtists"
/// collection, keyed "{source}:{artist}", following the clean BsonDocument-mapping style of
/// <see cref="ArtistCatalogRepo"/> (not the JSON-blob style of the older recommendations repo).
/// </summary>
public class RelatedArtistRepo : IRelatedArtistRepo
{
    private const string CollectionName = "relatedArtists";
    private const string FieldArtist = "artist";
    private const string FieldSource = "source";
    private const string FieldFetchedAt = "fetchedAt";
    private const string FieldRelated = "related";
    private const string FieldName = "name";
    private const string FieldImageUrl = "imageUrl";

    private readonly IMongoDbProvider _mongoDbProvider;

    public RelatedArtistRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    private static string DocId(ArtistKey artist, string source) => $"{source}:{artist.ArtistName}";

    public async Task Upsert(ArtistRelations relations)
    {
        var related = new BsonArray(relations.Related.Select(r => new BsonDocument
        {
            { FieldName, r.ArtistKey.ArtistName },
            { FieldImageUrl, (BsonValue?)r.ImageUrl ?? BsonNull.Value },
        }));

        var doc = new BsonDocument
        {
            { "_id", DocId(relations.Artist, relations.Source) },
            { FieldArtist, relations.Artist.ArtistName },
            { FieldSource, relations.Source },
            { FieldFetchedAt, relations.FetchedAt.UtcDateTime },
            { FieldRelated, related },
        };

        await Collection.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", doc["_id"]),
            doc,
            new ReplaceOptions { IsUpsert = true });
    }

    public async Task<ArtistRelations?> Get(ArtistKey artist, string source)
    {
        var cursor = await Collection.FindAsync(
            Builders<BsonDocument>.Filter.Eq("_id", DocId(artist, source)));
        var doc = await cursor.FirstOrDefaultAsync();
        return doc == null ? null : ToArtistRelations(doc);
    }

    public async Task<IReadOnlyList<ArtistRelations>> GetAllSources(ArtistKey artist)
    {
        var cursor = await Collection.FindAsync(
            Builders<BsonDocument>.Filter.Eq(FieldArtist, artist.ArtistName));
        return (await cursor.ToListAsync()).Select(ToArtistRelations).ToArray();
    }

    public async Task DeleteAllSources(ArtistKey artist) =>
        await Collection.DeleteManyAsync(
            Builders<BsonDocument>.Filter.Eq(FieldArtist, artist.ArtistName));

    public async Task<IReadOnlyList<string>> GetArtistNamesWithEdges(string source)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq(FieldSource, source),
            // "related.0 exists" is "the array has at least one element".
            Builders<BsonDocument>.Filter.Exists($"{FieldRelated}.0"));
        var cursor = await Collection.FindAsync(filter, new FindOptions<BsonDocument>
        {
            Projection = Builders<BsonDocument>.Projection.Include(FieldArtist),
        });

        return (await cursor.ToListAsync())
            .Select(d => d.TryGetValue(FieldArtist, out var a) && a.IsString ? a.AsString : null)
            .Where(a => !string.IsNullOrEmpty(a))
            .Select(a => a!)
            .ToArray();
    }

    private static ArtistRelations ToArtistRelations(BsonDocument doc)
    {
        var artist = doc.TryGetValue(FieldArtist, out var a) && !a.IsBsonNull ? a.AsString : "";
        var source = doc.TryGetValue(FieldSource, out var s) && !s.IsBsonNull ? s.AsString : "";

        var fetchedAt = doc.TryGetValue(FieldFetchedAt, out var f) && f.IsValidDateTime
            ? new DateTimeOffset(f.ToUniversalTime(), TimeSpan.Zero)
            : default;

        var related = new List<RelatedArtist>();
        if (doc.TryGetValue(FieldRelated, out var r) && r.IsBsonArray)
        {
            foreach (var element in r.AsBsonArray)
            {
                if (!element.IsBsonDocument) continue;
                var rd = element.AsBsonDocument;
                var name = rd.TryGetValue(FieldName, out var n) && !n.IsBsonNull ? n.AsString : null;
                if (string.IsNullOrEmpty(name)) continue;
                var imageUrl = rd.TryGetValue(FieldImageUrl, out var img) && !img.IsBsonNull ? img.AsString : null;
                related.Add(new RelatedArtist(new ArtistKey(name), imageUrl));
            }
        }

        return new ArtistRelations(new ArtistKey(artist), source, related, fetchedAt);
    }
}
