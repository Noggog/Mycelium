using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed store of linked Plex accounts (see <see cref="IPlexLinkRepo"/>). One document per app
/// user in the "plexLinks" collection, keyed by OIDC subject — the same key <see cref="UserRepo"/> uses,
/// so a link is simply absent for users who never connected one.
/// </summary>
public class PlexLinkRepo : IPlexLinkRepo
{
    private const string CollectionName = "plexLinks";
    private const string FieldAccountId = "accountId";
    private const string FieldUsername = "username";
    private const string FieldEmail = "email";
    private const string FieldServerToken = "serverToken";
    private const string FieldLinkedAt = "linkedAt";

    private readonly IMongoDbProvider _mongoDbProvider;

    public PlexLinkRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    public async Task<PlexLink?> Get(string subject)
    {
        var cursor = await Collection.FindAsync(Builders<BsonDocument>.Filter.Eq("_id", subject));
        var doc = await cursor.FirstOrDefaultAsync();
        if (doc == null)
        {
            return null;
        }

        string? Str(string field) =>
            doc.TryGetValue(field, out var v) && !v.IsBsonNull ? v.AsString : null;

        // A row with no token is unusable — treat it as unlinked rather than handing callers an empty
        // token that would 401 on every Plex call.
        var token = Str(FieldServerToken);
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        return new PlexLink(
            Subject: doc["_id"].AsString,
            AccountId: Str(FieldAccountId) ?? "",
            Username: Str(FieldUsername) ?? "",
            Email: Str(FieldEmail),
            ServerToken: token,
            LinkedAt: doc.TryGetValue(FieldLinkedAt, out var at) && at.IsValidDateTime
                ? new DateTimeOffset(at.ToUniversalTime(), TimeSpan.Zero)
                : default);
    }

    public Task Upsert(PlexLink link) =>
        Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", link.Subject),
            Builders<BsonDocument>.Update
                .Set(FieldAccountId, link.AccountId)
                .Set(FieldUsername, link.Username)
                .Set(FieldEmail, (BsonValue?)link.Email ?? BsonNull.Value)
                .Set(FieldServerToken, link.ServerToken)
                .Set(FieldLinkedAt, link.LinkedAt.UtcDateTime),
            new UpdateOptions { IsUpsert = true });

    public Task Delete(string subject) =>
        Collection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", subject));
}
