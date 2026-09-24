using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed <see cref="ISourceCacheStore"/>. One doc per key in the "sourceCache" collection, the
/// answer kept as a JSON string — it is a cache of someone else's payload, never queried into.
///
/// <para>An entry is kept well past its expiry, because a stale answer is still served while the
/// refresh runs (and is all there is when the source is down). Past <see cref="PurgeAfter"/> beyond
/// expiry nothing has asked for it in months, and a TTL index deletes it, so the collection doesn't
/// accumulate every artist anyone ever glanced at.</para>
/// </summary>
public class SourceCacheStore : ISourceCacheStore
{
    private const string CollectionName = "sourceCache";
    private const string FieldJson = "json";
    private const string FieldFetchedAt = "fetchedAt";
    private const string FieldExpiresAt = "expiresAt";
    private const string FieldPurgeAt = "purgeAt";

    /// <summary>How long past expiry an entry nobody has refreshed survives.</summary>
    public static readonly TimeSpan PurgeAfter = TimeSpan.FromDays(180);

    private readonly IMongoDbProvider _mongoDbProvider;
    private readonly Lazy<Task> _index;

    public SourceCacheStore(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
        _index = new Lazy<Task>(EnsureIndex);
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    public async Task<SourceCacheEntry?> Get(string key)
    {
        var doc = await Collection.Find(Builders<BsonDocument>.Filter.Eq("_id", key)).FirstOrDefaultAsync();
        if (doc is null || !doc.TryGetValue(FieldJson, out var json) || !json.IsString)
        {
            return null;
        }

        return new SourceCacheEntry(
            key,
            json.AsString,
            new DateTimeOffset(doc[FieldFetchedAt].ToUniversalTime()),
            new DateTimeOffset(doc[FieldExpiresAt].ToUniversalTime()));
    }

    public async Task Put(SourceCacheEntry entry)
    {
        await _index.Value;
        var doc = new BsonDocument
        {
            ["_id"] = entry.Key,
            [FieldJson] = entry.Json,
            [FieldFetchedAt] = entry.FetchedAt.UtcDateTime,
            [FieldExpiresAt] = entry.ExpiresAt.UtcDateTime,
            [FieldPurgeAt] = (entry.ExpiresAt + PurgeAfter).UtcDateTime,
        };
        await Collection.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", entry.Key), doc, new ReplaceOptions { IsUpsert = true });
    }

    public Task Expire(string key, DateTimeOffset at) =>
        Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", key),
            Builders<BsonDocument>.Update
                .Set(FieldExpiresAt, at.UtcDateTime)
                .Set(FieldPurgeAt, (at + PurgeAfter).UtcDateTime));

    /// <summary>
    /// Creates the purge index. Idempotent — creating an index that already exists with the same
    /// options is a no-op. A failure is swallowed rather than cached into every later write: the index
    /// is housekeeping, and the cache works without it.
    /// </summary>
    private async Task EnsureIndex()
    {
        try
        {
            await Collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending(FieldPurgeAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "purgeAt_ttl" }));
        }
        catch (MongoException)
        {
        }
    }
}
