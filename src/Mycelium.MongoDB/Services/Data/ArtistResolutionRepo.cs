using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed <see cref="IArtistResolutionRepo"/>. One doc per library artist in the
/// "artistResolutions" collection, keyed by the artist name. <c>needsAttention</c> is stored rather
/// than derived on read so the nav badge is a count, not a scan.
/// </summary>
public class ArtistResolutionRepo : IArtistResolutionRepo
{
    private const string CollectionName = "artistResolutions";
    private const string FieldStatus = "status";
    private const string FieldConfidence = "confidence";
    private const string FieldMbid = "mbid";
    private const string FieldName = "name";
    private const string FieldDisambiguation = "disambiguation";
    private const string FieldCurrentMbid = "currentMbid";
    private const string FieldOwnedAlbums = "ownedAlbums";
    private const string FieldCandidates = "candidates";
    private const string FieldAlbumOverlap = "albumOverlap";
    private const string FieldEvidence = "evidence";
    private const string FieldReason = "reason";
    private const string FieldCheckedAt = "checkedAt";
    private const string FieldNeedsAttention = "needsAttention";

    private readonly IMongoDbProvider _mongoDbProvider;

    public ArtistResolutionRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    public async Task<ArtistResolution[]> GetAll()
    {
        var docs = await Collection.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        return docs.Select(FromDocument).ToArray();
    }

    public async Task<ArtistResolution?> Get(string artist)
    {
        var doc = await Collection.Find(Builders<BsonDocument>.Filter.Eq("_id", artist)).FirstOrDefaultAsync();
        return doc is null ? null : FromDocument(doc);
    }

    public async Task<Dictionary<string, DateTimeOffset>> GetCheckedAt()
    {
        var docs = await Collection
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Project(Builders<BsonDocument>.Projection.Include(FieldCheckedAt))
            .ToListAsync();
        return docs.ToDictionary(
            d => d["_id"].AsString,
            d => new DateTimeOffset(d[FieldCheckedAt].ToUniversalTime()));
    }

    public Task<long> CountNeedingAttention() =>
        Collection.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq(FieldNeedsAttention, true));

    public Task Put(ArtistResolution resolution) =>
        Collection.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", resolution.Artist),
            ToDocument(resolution),
            new ReplaceOptions { IsUpsert = true });

    public Task DeleteAllExcept(IReadOnlyCollection<string> artists) =>
        Collection.DeleteManyAsync(Builders<BsonDocument>.Filter.Nin("_id", artists));

    private static BsonDocument ToDocument(ArtistResolution r) => new()
    {
        ["_id"] = r.Artist,
        [FieldStatus] = r.Status.ToString(),
        [FieldConfidence] = r.Confidence?.ToString() is { } c ? c : BsonNull.Value,
        [FieldMbid] = Nullable(r.Mbid),
        [FieldName] = Nullable(r.Name),
        [FieldDisambiguation] = Nullable(r.Disambiguation),
        [FieldCurrentMbid] = Nullable(r.CurrentMbid),
        [FieldOwnedAlbums] = r.OwnedAlbums,
        [FieldCandidates] = new BsonArray(r.Candidates.Select(c => new BsonDocument
        {
            [FieldMbid] = c.Mbid,
            [FieldName] = Nullable(c.Name),
            [FieldDisambiguation] = Nullable(c.Disambiguation),
            [FieldAlbumOverlap] = c.AlbumOverlap is { } o ? o : BsonNull.Value,
            [FieldEvidence] = new BsonArray(c.Evidence),
        })),
        [FieldReason] = r.Reason,
        [FieldCheckedAt] = r.CheckedAt.UtcDateTime,
        [FieldNeedsAttention] = r.NeedsAttention,
    };

    private static ArtistResolution FromDocument(BsonDocument d) => new(
        d["_id"].AsString,
        Enum.Parse<ArtistResolutionStatus>(d[FieldStatus].AsString),
        d.GetValue(FieldConfidence, BsonNull.Value) is { IsString: true } c
            ? Enum.Parse<ResolutionConfidence>(c.AsString)
            : null,
        String(d, FieldMbid),
        String(d, FieldName),
        String(d, FieldDisambiguation),
        String(d, FieldCurrentMbid),
        d.GetValue(FieldOwnedAlbums, 0).ToInt32(),
        d.GetValue(FieldCandidates, new BsonArray()).AsBsonArray
            .Select(v => v.AsBsonDocument)
            .Select(c => new ResolutionCandidate(
                c[FieldMbid].AsString,
                String(c, FieldName),
                String(c, FieldDisambiguation),
                c.GetValue(FieldAlbumOverlap, BsonNull.Value) is { IsInt32: true } o ? o.AsInt32 : null,
                c.GetValue(FieldEvidence, new BsonArray()).AsBsonArray.Select(e => e.AsString).ToArray()))
            .ToArray(),
        d.GetValue(FieldReason, "").AsString,
        new DateTimeOffset(d[FieldCheckedAt].ToUniversalTime()));

    private static BsonValue Nullable(string? value) => value is null ? BsonNull.Value : value;

    private static string? String(BsonDocument d, string field) =>
        d.GetValue(field, BsonNull.Value) is { IsString: true } v ? v.AsString : null;
}
