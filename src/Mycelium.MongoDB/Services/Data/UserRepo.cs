using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed user store. One document per user in the "users" collection, keyed by OIDC subject.
/// </summary>
public class UserRepo : IUserRepo
{
    private const string CollectionName = "users";
    private const string FieldUsername = "username";
    private const string FieldEmail = "email";
    private const string FieldDisplayName = "displayName";
    private const string FieldFirstSeenAt = "firstSeenAt";
    private const string FieldLastLoginAt = "lastLoginAt";
    private const string FieldMaxQuality = "maxQuality";
    private const string FieldHalfStarRatings = "halfStarRatings";

    private readonly IMongoDbProvider _mongoDbProvider;

    public UserRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    public async Task UpsertOnLogin(AppUser user)
    {
        var update = Builders<BsonDocument>.Update
            .Set(FieldUsername, (BsonValue?)user.Username ?? BsonNull.Value)
            .Set(FieldEmail, (BsonValue?)user.Email ?? BsonNull.Value)
            .Set(FieldDisplayName, (BsonValue?)user.DisplayName ?? BsonNull.Value)
            .Set(FieldLastLoginAt, user.LastLoginAt.UtcDateTime)
            // First-seen is written only on the initial insert, never overwritten on later logins.
            .SetOnInsert(FieldFirstSeenAt, user.FirstSeenAt.UtcDateTime);
        // maxQuality is deliberately absent from this update: it is set from the dev panel, not by
        // the IdP, and touching it here would undo an operator's decision on the user's next login.
        // Same for halfStarRatings, which the user themselves sets from the Playlists page.

        await Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", user.Subject),
            update,
            new UpdateOptions { IsUpsert = true });
    }

    public async Task<AppUser?> Get(string subject)
    {
        var cursor = await Collection.FindAsync(Builders<BsonDocument>.Filter.Eq("_id", subject));
        var doc = await cursor.FirstOrDefaultAsync();
        return doc == null ? null : ToAppUser(doc);
    }

    public async Task<AppUser[]> GetAll()
    {
        var cursor = await Collection.FindAsync(Builders<BsonDocument>.Filter.Empty);
        return (await cursor.ToListAsync())
            .Select(ToAppUser)
            .OrderBy(u => u.DisplayName ?? u.Username ?? u.Subject, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task SetMaxQuality(string subject, AudioQuality? quality)
    {
        var update = quality is null
            ? Builders<BsonDocument>.Update.Unset(FieldMaxQuality)
            : Builders<BsonDocument>.Update.Set(FieldMaxQuality, quality.Value.ToString());

        // IsUpsert defaults to false: never conjure a user doc from a subject that isn't real.
        return Collection.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", subject), update);
    }

    public Task SetHalfStarRatings(string subject, bool? halfStars)
    {
        var update = halfStars is null
            ? Builders<BsonDocument>.Update.Unset(FieldHalfStarRatings)
            : Builders<BsonDocument>.Update.Set(FieldHalfStarRatings, halfStars.Value);

        return Collection.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", subject), update);
    }

    public async Task<int> BackfillMissingQuality(AudioQuality quality)
    {
        var result = await Collection.UpdateManyAsync(
            Builders<BsonDocument>.Filter.Exists(FieldMaxQuality, false)
            | Builders<BsonDocument>.Filter.Eq(FieldMaxQuality, BsonNull.Value),
            Builders<BsonDocument>.Update.Set(FieldMaxQuality, quality.ToString()));
        return (int)result.ModifiedCount;
    }

    private static AppUser ToAppUser(BsonDocument doc)
    {
        string? Str(string field) =>
            doc.TryGetValue(field, out var v) && !v.IsBsonNull ? v.AsString : null;

        DateTimeOffset Date(string field) =>
            doc.TryGetValue(field, out var v) && v.IsValidDateTime
                ? new DateTimeOffset(v.ToUniversalTime(), TimeSpan.Zero)
                : default;

        bool? Bool(string field) =>
            doc.TryGetValue(field, out var v) && v.IsBoolean ? v.AsBoolean : null;

        return new AppUser(
            Subject: doc["_id"].AsString,
            Username: Str(FieldUsername),
            Email: Str(FieldEmail),
            DisplayName: Str(FieldDisplayName),
            FirstSeenAt: Date(FieldFirstSeenAt),
            LastLoginAt: Date(FieldLastLoginAt),
            // Absent (every doc written before tiers existed) parses to null — "never set" — so the
            // deployment default applies rather than an invented entitlement.
            MaxQuality: AudioQualityTier.Parse(Str(FieldMaxQuality)),
            // Absent means the user has never answered, so the catalog's default scale applies.
            HalfStarRatings: Bool(FieldHalfStarRatings));
    }
}
