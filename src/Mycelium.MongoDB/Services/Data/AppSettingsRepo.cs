using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed operator settings (see <see cref="IAppSettingsRepo"/>). One doc per settings group in
/// the "appSettings" collection — the download switch lives in <c>_id: "downloads"</c>. A missing doc
/// or field means "never set", which the caller reads as "use the environment default". The temporary
/// fast-mode burst lives in the same doc as a deadline (<c>fastUntil</c>), so it survives a redeploy
/// mid-hour and lapses on its own without anything having to switch it back off.
/// </summary>
public class AppSettingsRepo : IAppSettingsRepo
{
    private const string CollectionName = "appSettings";
    private const string DownloadsId = "downloads";
    private const string FieldAutomatic = "automatic";
    private const string FieldFastUntil = "fastUntil";
    private const string FieldUpdatedAt = "updatedAt";

    private readonly IMongoDbProvider _mongoDbProvider;

    public AppSettingsRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    public async Task<bool?> GetDownloadsAutomatic()
    {
        var doc = await Load();
        return doc != null && doc.TryGetValue(FieldAutomatic, out var v) && v.IsBoolean
            ? v.AsBoolean
            : null;
    }

    public Task SetDownloadsAutomatic(bool automatic) =>
        Update(Builders<BsonDocument>.Update.Set(FieldAutomatic, automatic));

    public async Task<DateTimeOffset?> GetDownloadsFastUntil()
    {
        var doc = await Load();
        return doc != null && doc.TryGetValue(FieldFastUntil, out var v) && v.IsValidDateTime
            ? new DateTimeOffset(v.ToUniversalTime(), TimeSpan.Zero)
            : null;
    }

    // Cleared by unsetting rather than writing a past stamp, so a doc that's never been in fast mode
    // and one whose burst was cancelled read back identically.
    public Task SetDownloadsFastUntil(DateTimeOffset? until) =>
        Update(until is null
            ? Builders<BsonDocument>.Update.Unset(FieldFastUntil)
            : Builders<BsonDocument>.Update.Set(FieldFastUntil, until.Value.UtcDateTime));

    private async Task<BsonDocument?> Load()
    {
        var cursor = await Collection.FindAsync(Builders<BsonDocument>.Filter.Eq("_id", DownloadsId));
        return await cursor.FirstOrDefaultAsync();
    }

    private Task Update(UpdateDefinition<BsonDocument> update) =>
        Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", DownloadsId),
            Builders<BsonDocument>.Update.Combine(
                update, Builders<BsonDocument>.Update.Set(FieldUpdatedAt, DateTimeOffset.UtcNow.UtcDateTime)),
            new UpdateOptions { IsUpsert = true });
}
