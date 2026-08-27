using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed store of the automation tokens (see <see cref="IApiTokenRepo"/>). One document per
/// token in the "apiTokens" collection, keyed by the token's public id.
///
/// <para>The secret hash is stored as BSON binary rather than a hex string: it is bytes, it is never
/// read by a human, and keeping it binary means nothing in this file is tempted to compare it as a
/// string (which would not be constant time). Nothing here ever sees or stores the token itself.</para>
/// </summary>
public class ApiTokenRepo : IApiTokenRepo
{
    private const string CollectionName = "apiTokens";
    private const string FieldSubject = "subject";
    private const string FieldName = "name";
    private const string FieldSecretHash = "secretHash";
    private const string FieldDevScope = "devScope";
    private const string FieldCreatedAt = "createdAt";
    private const string FieldExpiresAt = "expiresAt";
    private const string FieldRevokedAt = "revokedAt";

    private readonly IMongoDbProvider _mongoDbProvider;

    public ApiTokenRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    public Task Add(ApiTokenRecord token) =>
        Collection.InsertOneAsync(new BsonDocument
        {
            { "_id", token.Id },
            { FieldSubject, token.Subject },
            { FieldName, token.Name },
            { FieldSecretHash, new BsonBinaryData(token.SecretHash) },
            { FieldDevScope, token.DevScope },
            { FieldCreatedAt, token.CreatedAt.UtcDateTime },
            { FieldExpiresAt, token.ExpiresAt is { } e ? e.UtcDateTime : BsonNull.Value },
            { FieldRevokedAt, BsonNull.Value },
        });

    public async Task<ApiTokenRecord?> Get(string id)
    {
        var cursor = await Collection.FindAsync(Builders<BsonDocument>.Filter.Eq("_id", id));
        var doc = await cursor.FirstOrDefaultAsync();
        return doc == null ? null : ToRecord(doc);
    }

    public async Task<ApiTokenRecord[]> GetForSubject(string subject)
    {
        var cursor = await Collection.FindAsync(Builders<BsonDocument>.Filter.Eq(FieldSubject, subject));
        return (await cursor.ToListAsync())
            .Select(ToRecord)
            .OrderByDescending(t => t.CreatedAt)
            .ToArray();
    }

    public async Task<bool> Revoke(string id, string subject, DateTimeOffset at)
    {
        // Both halves of the filter matter. The subject stops one user revoking another's token by
        // guessing an id; the null revokedAt makes a second revoke a no-op rather than a silent
        // rewrite of when it happened, so the returned flag means "this call is what stopped it".
        var result = await Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id)
            & Builders<BsonDocument>.Filter.Eq(FieldSubject, subject)
            & Builders<BsonDocument>.Filter.Eq(FieldRevokedAt, BsonNull.Value),
            Builders<BsonDocument>.Update.Set(FieldRevokedAt, at.UtcDateTime));
        return result.ModifiedCount > 0;
    }

    private static ApiTokenRecord ToRecord(BsonDocument doc)
    {
        DateTimeOffset? Date(string field) =>
            doc.TryGetValue(field, out var v) && v.IsValidDateTime
                ? new DateTimeOffset(v.ToUniversalTime(), TimeSpan.Zero)
                : null;

        return new ApiTokenRecord(
            Id: doc["_id"].AsString,
            Subject: doc[FieldSubject].AsString,
            Name: doc.TryGetValue(FieldName, out var n) && !n.IsBsonNull ? n.AsString : "",
            SecretHash: doc.TryGetValue(FieldSecretHash, out var h) && h.IsBsonBinaryData
                ? h.AsBsonBinaryData.Bytes
                : Array.Empty<byte>(),
            DevScope: doc.TryGetValue(FieldDevScope, out var d) && d.IsBoolean && d.AsBoolean,
            CreatedAt: Date(FieldCreatedAt) ?? default,
            ExpiresAt: Date(FieldExpiresAt),
            RevokedAt: Date(FieldRevokedAt));
    }
}
