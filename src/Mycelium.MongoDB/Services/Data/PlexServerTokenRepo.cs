using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed store of the server's own Plex token (see <see cref="IPlexServerTokenRepo"/>). A single
/// document in the "appSettings" collection under <c>_id: "plexServerToken"</c> — the same collection
/// the operator switches live in, since this is one more thing set from the UI rather than the
/// environment.
/// </summary>
public class PlexServerTokenRepo : IPlexServerTokenRepo
{
    private const string CollectionName = "appSettings";
    private const string DocId = "plexServerToken";
    private const string FieldToken = "token";
    private const string FieldUsername = "username";
    private const string FieldEmail = "email";
    private const string FieldLinkedAt = "linkedAt";

    private readonly IMongoDbProvider _mongoDbProvider;

    public PlexServerTokenRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    public async Task<PlexServerCredential?> Get()
    {
        var cursor = await Collection.FindAsync(Builders<BsonDocument>.Filter.Eq("_id", DocId));
        var doc = await cursor.FirstOrDefaultAsync();
        if (doc == null)
        {
            return null;
        }

        string? Str(string field) =>
            doc.TryGetValue(field, out var v) && !v.IsBsonNull ? v.AsString : null;

        // A row with no token is indistinguishable from never having linked: fall back to the
        // environment rather than hand out an empty token that would 401 on every call.
        var token = Str(FieldToken);
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        return new PlexServerCredential(
            Token: token,
            Username: Str(FieldUsername),
            Email: Str(FieldEmail),
            LinkedAt: doc.TryGetValue(FieldLinkedAt, out var at) && at.IsValidDateTime
                ? new DateTimeOffset(at.ToUniversalTime(), TimeSpan.Zero)
                : default);
    }

    public Task Set(PlexServerCredential credential) =>
        Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", DocId),
            Builders<BsonDocument>.Update
                .Set(FieldToken, credential.Token)
                .Set(FieldUsername, (BsonValue?)credential.Username ?? BsonNull.Value)
                .Set(FieldEmail, (BsonValue?)credential.Email ?? BsonNull.Value)
                .Set(FieldLinkedAt, credential.LinkedAt.UtcDateTime),
            new UpdateOptions { IsUpsert = true });

    public Task Clear() =>
        Collection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", DocId));
}
