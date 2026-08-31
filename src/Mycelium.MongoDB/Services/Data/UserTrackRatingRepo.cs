using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed mirror of each user's Plex song ratings (see <see cref="IUserTrackRatingRepo"/>). One
/// doc per (user, track) in the "userTrackRatings" collection.
///
/// <para>The <c>_id</c> is the user plus the track's file path, which is the only identity a track has
/// that outlives the server indexing it. Where a track has no file, the artist/album/title triple
/// stands in — less stable, but it keeps a row that would otherwise be dropped on the floor.</para>
/// </summary>
public class UserTrackRatingRepo : IUserTrackRatingRepo
{
    private const string CollectionName = "userTrackRatings";
    private const string FieldUserId = "userId";
    private const string FieldArtist = "artist";
    private const string FieldAlbum = "album";
    private const string FieldTitle = "title";
    private const string FieldTrackNumber = "trackNumber";
    private const string FieldFile = "file";
    private const string FieldStars = "stars";

    private readonly IMongoDbProvider _mongoDbProvider;

    public UserTrackRatingRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    public async Task<int> ReplaceForUser(string userId, IReadOnlyList<TrackRating> ratings)
    {
        var mine = Builders<BsonDocument>.Filter.Eq(FieldUserId, userId);

        // Delete-then-insert, scoped to this one user. The sweep is authoritative for the account it
        // read, so anything it didn't return is a rating that no longer exists.
        await Collection.DeleteManyAsync(mine);

        if (ratings.Count == 0)
        {
            return 0;
        }

        // Deduped on the way in: two tracks can share a path (or an artist/album/title) in a library
        // with duplicate files, and InsertMany would fail the whole batch on the second one.
        var docs = ratings
            .GroupBy(r => Id(userId, r), StringComparer.Ordinal)
            .Select(g => ToDocument(g.Key, userId, g.First()))
            .ToList();

        await Collection.InsertManyAsync(docs);
        return docs.Count;
    }

    public async Task<TrackRating[]> GetForUser(string userId)
    {
        var cursor = await Collection.FindAsync(Builders<BsonDocument>.Filter.Eq(FieldUserId, userId));
        return (await cursor.ToListAsync()).Select(ToRating).ToArray();
    }

    private static string Id(string userId, TrackRating rating) =>
        string.IsNullOrWhiteSpace(rating.File)
            ? $"{userId}|{rating.Artist}|{rating.Album}|{rating.Title}"
            : $"{userId}|{rating.File}";

    private static BsonDocument ToDocument(string id, string userId, TrackRating rating) => new()
    {
        { "_id", id },
        { FieldUserId, userId },
        { FieldArtist, rating.Artist },
        { FieldAlbum, rating.Album },
        { FieldTitle, rating.Title },
        { FieldTrackNumber, rating.TrackNumber is { } n ? n : BsonNull.Value },
        { FieldFile, rating.File ?? (BsonValue)BsonNull.Value },
        { FieldStars, rating.Stars },
    };

    private static TrackRating ToRating(BsonDocument doc) => new(
        Artist: doc.TryGetValue(FieldArtist, out var artist) && !artist.IsBsonNull ? artist.AsString : "",
        Album: doc.TryGetValue(FieldAlbum, out var album) && !album.IsBsonNull ? album.AsString : "",
        Title: doc.TryGetValue(FieldTitle, out var title) && !title.IsBsonNull ? title.AsString : "",
        TrackNumber: doc.TryGetValue(FieldTrackNumber, out var n) && !n.IsBsonNull ? n.ToInt32() : null,
        File: doc.TryGetValue(FieldFile, out var file) && !file.IsBsonNull ? file.AsString : null,
        Stars: doc.TryGetValue(FieldStars, out var stars) && !stars.IsBsonNull ? stars.ToDouble() : 0);
}
