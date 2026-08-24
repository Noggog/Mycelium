using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed per-user album verdicts. One doc per (user, artist, album) in the
/// "userAlbumRatings" collection, keyed "{userId}:{artist} {album}". The album analogue of the
/// artist ratings in <see cref="UserQueueRepo"/>.
/// </summary>
public class UserAlbumRatingRepo : IUserAlbumRatingRepo
{
    private const string CollectionName = "userAlbumRatings";
    private const string FieldUserId = "userId";
    private const string FieldArtist = "artist";
    private const string FieldAlbum = "album";
    private const string FieldAlbumArt = "albumArt";
    private const string FieldStatus = "status";
    private const string FieldDecidedAt = "decidedAt";
    private const string FieldSnoozeUntil = "snoozeUntil";

    private static readonly string StatusLiked = DiscoveryStatus.Liked.ToString();
    private static readonly string StatusSnoozed = DiscoveryStatus.Snoozed.ToString();

    private readonly IMongoDbProvider _mongoDbProvider;

    public UserAlbumRatingRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    private static string DocId(string userId, string artist, string album) => $"{userId}:{artist} {album}";

    public async Task Rate(string userId, string artistName, string albumName, string? albumArt, DiscoveryStatus status)
    {
        var updates = new List<UpdateDefinition<BsonDocument>>
        {
            Builders<BsonDocument>.Update.SetOnInsert(FieldUserId, userId),
            Builders<BsonDocument>.Update.SetOnInsert(FieldArtist, artistName),
            Builders<BsonDocument>.Update.SetOnInsert(FieldAlbum, albumName),
            Builders<BsonDocument>.Update.Set(FieldStatus, status.ToString()),
            Builders<BsonDocument>.Update.Set(FieldDecidedAt, DateTimeOffset.UtcNow.UtcDateTime),
        };
        if (albumArt != null)
        {
            updates.Add(Builders<BsonDocument>.Update.Set(FieldAlbumArt, albumArt));
        }

        await Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", DocId(userId, artistName, albumName)),
            Builders<BsonDocument>.Update.Combine(updates),
            new UpdateOptions { IsUpsert = true });
    }

    public async Task Snooze(string userId, string artistName, string albumName, string? albumArt, DateTimeOffset until)
    {
        var updates = new List<UpdateDefinition<BsonDocument>>
        {
            Builders<BsonDocument>.Update.SetOnInsert(FieldUserId, userId),
            Builders<BsonDocument>.Update.SetOnInsert(FieldArtist, artistName),
            Builders<BsonDocument>.Update.SetOnInsert(FieldAlbum, albumName),
            Builders<BsonDocument>.Update.Set(FieldStatus, StatusSnoozed),
            Builders<BsonDocument>.Update.Set(FieldSnoozeUntil, until.UtcDateTime),
            Builders<BsonDocument>.Update.Set(FieldDecidedAt, DateTimeOffset.UtcNow.UtcDateTime),
        };
        if (albumArt != null)
        {
            updates.Add(Builders<BsonDocument>.Update.Set(FieldAlbumArt, albumArt));
        }

        await Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", DocId(userId, artistName, albumName)),
            Builders<BsonDocument>.Update.Combine(updates),
            new UpdateOptions { IsUpsert = true });
    }

    public Task Clear(string userId, string artistName, string albumName) =>
        Collection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", DocId(userId, artistName, albumName)));

    public async Task<HashSet<string>> GetDecidedKeys(string userId)
    {
        var f = Builders<BsonDocument>.Filter;
        // Decided = anything except a Snoozed row whose snooze has expired (those resurface).
        var filter = f.Eq(FieldUserId, userId)
                     & (f.Ne(FieldStatus, StatusSnoozed) | f.Gt(FieldSnoozeUntil, DateTimeOffset.UtcNow.UtcDateTime));
        var cursor = await Collection.FindAsync(
            filter,
            new FindOptions<BsonDocument>
            {
                Projection = Builders<BsonDocument>.Projection.Include(FieldArtist).Include(FieldAlbum),
            });

        var keys = new HashSet<string>();
        foreach (var doc in await cursor.ToListAsync())
        {
            var artist = doc.TryGetValue(FieldArtist, out var a) && !a.IsBsonNull ? a.AsString : null;
            var album = doc.TryGetValue(FieldAlbum, out var al) && !al.IsBsonNull ? al.AsString : null;
            if (artist != null && album != null)
            {
                keys.Add(AlbumRatingKey.For(artist, album));
            }
        }
        return keys;
    }

    public Task<AlbumRating[]> GetRated(string userId) =>
        Query(Builders<BsonDocument>.Filter.Eq(FieldUserId, userId));

    public Task<AlbumRating[]> GetLiked(string userId) =>
        Query(Builders<BsonDocument>.Filter.Eq(FieldUserId, userId)
              & Builders<BsonDocument>.Filter.Eq(FieldStatus, StatusLiked));

    public Task<AlbumRating[]> GetAllLiked() =>
        Query(Builders<BsonDocument>.Filter.Eq(FieldStatus, StatusLiked));

    public async Task<LikedAlbum[]> GetAllLikedByUser()
    {
        var cursor = await Collection.FindAsync(
            Builders<BsonDocument>.Filter.Eq(FieldStatus, StatusLiked),
            new FindOptions<BsonDocument> { Sort = Builders<BsonDocument>.Sort.Descending(FieldDecidedAt) });

        return (await cursor.ToListAsync())
            .Select(doc => (Doc: doc, UserId: doc.TryGetValue(FieldUserId, out var u) && !u.IsBsonNull
                ? u.AsString
                : null))
            // A row with no userId can't have its entitlement resolved, and guessing one would either
            // over- or under-serve somebody. Dropping it only loses it from the quality calculation:
            // GetAllLiked still surfaces the same album, so it is still queued, just at the default.
            .Where(x => x.UserId != null)
            .Select(x => new LikedAlbum(x.UserId!, ToRating(x.Doc)))
            .ToArray();
    }

    public async Task<CombinedAlbumVerdict[]> FindCombinedRatings()
    {
        var filter = Builders<BsonDocument>.Filter.Regex(FieldArtist, new BsonRegularExpression(";"));
        var cursor = await Collection.FindAsync(filter);

        var result = new List<CombinedAlbumVerdict>();
        foreach (var doc in await cursor.ToListAsync())
        {
            var userId = doc.TryGetValue(FieldUserId, out var u) && !u.IsBsonNull ? u.AsString : null;
            var artist = doc.TryGetValue(FieldArtist, out var a) && !a.IsBsonNull ? a.AsString : null;
            var album = doc.TryGetValue(FieldAlbum, out var al) && !al.IsBsonNull ? al.AsString : null;
            if (userId == null || artist == null || album == null)
            {
                continue;
            }

            var art = doc.TryGetValue(FieldAlbumArt, out var art2) && !art2.IsBsonNull ? art2.AsString : null;
            var status = doc.TryGetValue(FieldStatus, out var s) && !s.IsBsonNull
                         && Enum.TryParse<DiscoveryStatus>(s.AsString, out var parsed)
                ? parsed
                : DiscoveryStatus.Pending;

            result.Add(new CombinedAlbumVerdict(userId, artist, album, art, status));
        }

        return result.ToArray();
    }

    private async Task<AlbumRating[]> Query(FilterDefinition<BsonDocument> filter)
    {
        var cursor = await Collection.FindAsync(filter, new FindOptions<BsonDocument>
        {
            Sort = Builders<BsonDocument>.Sort.Descending(FieldDecidedAt),
        });
        return (await cursor.ToListAsync()).Select(ToRating).ToArray();
    }

    private static AlbumRating ToRating(BsonDocument doc)
    {
        var artist = doc.TryGetValue(FieldArtist, out var a) && !a.IsBsonNull ? a.AsString : "";
        var album = doc.TryGetValue(FieldAlbum, out var al) && !al.IsBsonNull ? al.AsString : "";
        var art = doc.TryGetValue(FieldAlbumArt, out var art2) && !art2.IsBsonNull ? art2.AsString : null;
        var status = doc.TryGetValue(FieldStatus, out var s) && !s.IsBsonNull
            && Enum.TryParse<DiscoveryStatus>(s.AsString, out var parsed)
            ? parsed
            : DiscoveryStatus.Pending;
        DateTimeOffset? snoozeUntil = doc.TryGetValue(FieldSnoozeUntil, out var su) && su.IsValidDateTime
            ? new DateTimeOffset(su.ToUniversalTime(), TimeSpan.Zero)
            : null;
        return new AlbumRating(new ArtistKey(artist), new AlbumKey(album), art, status, snoozeUntil);
    }
}
