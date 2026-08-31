using System.Globalization;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Reads whole collections out of Mongo as plain JSON, for the metadata archive
/// (see <see cref="IArchiveDump"/>).
///
/// <para>The only repo here that isn't shaped around a domain type. That's deliberate: the archive's
/// job is to preserve what was written, including fields no C# record models yet, so it reads the
/// documents rather than the read models. A field added to a collection tomorrow lands in tomorrow's
/// snapshot with no change here — the same defensive-reader posture every other repo takes, pointed
/// the other way.</para>
///
/// <para>Storage-specific types are flattened on the way out so nothing downstream has to know about
/// BSON. Crucially, <b>every conversion has to be stable</b>: the archive commits only when the file
/// bytes change, so a value that serialised two different ways on two runs would manufacture a diff
/// every night and drown the real ones.</para>
/// </summary>
public class ArchiveDumpRepo : IArchiveDump
{
    private readonly IMongoDbProvider _mongoDbProvider;

    public ArchiveDumpRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    public async Task<IReadOnlyList<JsonObject>> Dump(string collection)
    {
        var cursor = await _mongoDbProvider.database
            .GetCollection<BsonDocument>(collection)
            .FindAsync(Builders<BsonDocument>.Filter.Empty);

        var docs = await cursor.ToListAsync();
        return docs.Select(ToJson).ToArray();
    }

    /// <summary>
    /// One document, flattened. Nested documents and arrays recurse, so an embedded shape like
    /// <c>albumQuality: [{title, quality}]</c> or <c>reconsider: {average, ratedCount}</c> survives
    /// intact rather than being stringified.
    /// </summary>
    private static JsonObject ToJson(BsonDocument doc)
    {
        var obj = new JsonObject();
        foreach (var element in doc)
        {
            obj[element.Name] = ToNode(element.Value);
        }

        return obj;
    }

    private static JsonNode? ToNode(BsonValue value) => value.BsonType switch
    {
        BsonType.Null => null,
        BsonType.Boolean => JsonValue.Create(value.AsBoolean),
        BsonType.String => JsonValue.Create(value.AsString),
        BsonType.Int32 => JsonValue.Create((long)value.AsInt32),
        BsonType.Int64 => JsonValue.Create(value.AsInt64),

        // Widened to double rather than kept as decimal128 so that every numeric in the archive is one
        // of two CLR types and the canonical writer has two cases instead of five.
        BsonType.Double => JsonValue.Create(value.AsDouble),
        BsonType.Decimal128 => JsonValue.Create((double)value.AsDecimal128),

        // ISO-8601 UTC to whole seconds. Truncated on purpose: sub-second precision is noise nobody
        // will ever read, and it would make two snapshots of the same decision differ.
        BsonType.DateTime => JsonValue.Create(
            DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc)
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),

        BsonType.Array => ToArray(value.AsBsonArray),
        BsonType.Document => ToJson(value.AsBsonDocument),

        // ObjectId shouldn't occur — every _id in this system is a natural key — but a stray one is
        // better archived as its hex than dropped or thrown over.
        BsonType.ObjectId => JsonValue.Create(value.AsObjectId.ToString()),

        _ => JsonValue.Create(value.ToString()),
    };

    private static JsonArray ToArray(BsonArray array)
    {
        var result = new JsonArray();
        foreach (var item in array)
        {
            result.Add(ToNode(item));
        }

        return result;
    }
}
