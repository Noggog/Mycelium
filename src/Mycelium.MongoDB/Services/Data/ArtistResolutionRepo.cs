using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed <see cref="IArtistResolutionRepo"/>. One doc per library artist in the
/// "artistResolutions" collection, keyed by <see cref="ArtistResolution.Id"/>. <c>needsAttention</c> is
/// stored rather than derived on read so the nav badge is a count, not a scan.
///
/// <para>Pins on Plex artists that share a name live in their own collection, "libraryArtistPins":
/// resolutions are rewritten wholesale on every check, and a person's choice must outlive that.</para>
/// </summary>
public class ArtistResolutionRepo : IArtistResolutionRepo
{
    private const string CollectionName = "artistResolutions";
    private const string PinCollectionName = "libraryArtistPins";
    private const string FieldArtist = "artist";
    private const string FieldPlexArtistKey = "plexArtistKey";
    private const string FieldAlbums = "albums";
    private const string FieldMatchedAlbums = "matchedAlbums";
    private const string FieldReleaseGroups = "releaseGroups";
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

    private IMongoCollection<BsonDocument> Pins =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(PinCollectionName);

    public async Task<ArtistResolution[]> GetAll()
    {
        var docs = await Collection.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        return docs.Select(FromDocument).ToArray();
    }

    public async Task<ArtistResolution?> Get(string id)
    {
        var doc = await Collection.Find(Builders<BsonDocument>.Filter.Eq("_id", id)).FirstOrDefaultAsync();
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
            Builders<BsonDocument>.Filter.Eq("_id", resolution.Id),
            ToDocument(resolution),
            new ReplaceOptions { IsUpsert = true });

    public Task DeleteAllExcept(IReadOnlyCollection<string> ids) =>
        Collection.DeleteManyAsync(Builders<BsonDocument>.Filter.Nin("_id", ids));

    public async Task<MusicBrainzIdentity?> GetPin(string id)
    {
        var doc = await Pins.Find(Builders<BsonDocument>.Filter.Eq("_id", id)).FirstOrDefaultAsync();
        return doc is null || String(doc, FieldMbid) is not { } mbid
            ? null
            : new MusicBrainzIdentity(mbid, String(doc, FieldName), String(doc, FieldDisambiguation));
    }

    public Task SetPin(string id, MusicBrainzIdentity? identity) =>
        identity is null
            ? Pins.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", id))
            : Pins.ReplaceOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", id),
                new BsonDocument
                {
                    ["_id"] = id,
                    [FieldMbid] = identity.Mbid,
                    [FieldName] = Nullable(identity.Name),
                    [FieldDisambiguation] = Nullable(identity.Disambiguation),
                },
                new ReplaceOptions { IsUpsert = true });

    private static BsonDocument ToDocument(ArtistResolution r) => new()
    {
        ["_id"] = r.Id,
        [FieldArtist] = r.Artist,
        [FieldPlexArtistKey] = r.PlexArtistKey is { } k ? k : BsonNull.Value,
        [FieldAlbums] = new BsonArray(r.Albums ?? []),
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
            [FieldMatchedAlbums] = c.MatchedAlbums is { } m ? new BsonArray(m) : BsonNull.Value,
            [FieldReleaseGroups] = c.ReleaseGroups is { } g ? g : BsonNull.Value,
        })),
        [FieldReason] = r.Reason,
        [FieldCheckedAt] = r.CheckedAt.UtcDateTime,
        [FieldNeedsAttention] = r.NeedsAttention,
    };

    private static ArtistResolution FromDocument(BsonDocument d) => new(
        String(d, FieldArtist) ?? d["_id"].AsString,
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
                c.GetValue(FieldEvidence, new BsonArray()).AsBsonArray.Select(e => e.AsString).ToArray(),
                Strings(c, FieldMatchedAlbums),
                c.GetValue(FieldReleaseGroups, BsonNull.Value) is { IsInt32: true } g ? g.AsInt32 : null))
            .ToArray(),
        d.GetValue(FieldReason, "").AsString,
        new DateTimeOffset(d[FieldCheckedAt].ToUniversalTime()),
        d.GetValue(FieldPlexArtistKey, BsonNull.Value) is { IsInt32: true } k ? k.AsInt32 : null,
        Strings(d, FieldAlbums));

    private static string[]? Strings(BsonDocument d, string field) =>
        d.GetValue(field, BsonNull.Value) is { IsBsonArray: true } a
            ? a.AsBsonArray.Where(v => v.IsString).Select(v => v.AsString).ToArray()
            : null;

    private static BsonValue Nullable(string? value) => value is null ? BsonNull.Value : value;

    private static string? String(BsonDocument d, string field) =>
        d.GetValue(field, BsonNull.Value) is { IsString: true } v ? v.AsString : null;
}
