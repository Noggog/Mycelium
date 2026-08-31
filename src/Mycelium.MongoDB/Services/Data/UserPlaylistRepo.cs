using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed mirror of each user's Plex playlists (see <see cref="IUserPlaylistRepo"/>). One doc
/// per (user, playlist) in the "userPlaylists" collection, keyed by the playlist's title within the
/// user — Plex's rating key is a local handle a rebuild reissues, and the title is what a person would
/// recognise the playlist by.
/// </summary>
public class UserPlaylistRepo : IUserPlaylistRepo
{
    private const string CollectionName = "userPlaylists";
    private const string FieldUserId = "userId";
    private const string FieldTitle = "title";
    private const string FieldSmart = "smart";
    private const string FieldRules = "rules";
    private const string FieldTracks = "tracks";

    private const string FieldPosition = "position";
    private const string FieldArtist = "artist";
    private const string FieldAlbum = "album";
    private const string FieldTrackTitle = "title";
    private const string FieldFile = "file";

    private readonly IMongoDbProvider _mongoDbProvider;

    public UserPlaylistRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    public async Task<int> ReplaceForUser(string userId, IReadOnlyList<UserPlaylist> playlists)
    {
        await Collection.DeleteManyAsync(Builders<BsonDocument>.Filter.Eq(FieldUserId, userId));

        if (playlists.Count == 0)
        {
            return 0;
        }

        // Plex lets one account hold two playlists with the same name; keyed by title, they'd collide.
        // Keeping the first is arbitrary but stable, and better than failing the whole batch.
        var docs = playlists
            .GroupBy(p => $"{userId}|{p.Title}", StringComparer.Ordinal)
            .Select(g => ToDocument(g.Key, userId, g.First()))
            .ToList();

        await Collection.InsertManyAsync(docs);
        return docs.Count;
    }

    public async Task<UserPlaylist[]> GetForUser(string userId)
    {
        var cursor = await Collection.FindAsync(Builders<BsonDocument>.Filter.Eq(FieldUserId, userId));
        return (await cursor.ToListAsync()).Select(ToPlaylist).ToArray();
    }

    private static BsonDocument ToDocument(string id, string userId, UserPlaylist playlist) => new()
    {
        { "_id", id },
        { FieldUserId, userId },
        { FieldTitle, playlist.Title },
        { FieldSmart, playlist.Smart },
        { FieldRules, playlist.Rules ?? (BsonValue)BsonNull.Value },
        {
            FieldTracks, new BsonArray(playlist.Tracks.Select(t => new BsonDocument
            {
                { FieldPosition, t.Position },
                { FieldArtist, t.Artist },
                { FieldAlbum, t.Album },
                { FieldTrackTitle, t.Title },
                { FieldFile, t.File ?? (BsonValue)BsonNull.Value },
            }))
        },
    };

    private static UserPlaylist ToPlaylist(BsonDocument doc) => new(
        Title: doc.TryGetValue(FieldTitle, out var title) && !title.IsBsonNull ? title.AsString : "",
        Smart: doc.TryGetValue(FieldSmart, out var smart) && !smart.IsBsonNull && smart.ToBoolean(),
        Rules: doc.TryGetValue(FieldRules, out var rules) && !rules.IsBsonNull ? rules.AsString : null,
        Tracks: doc.TryGetValue(FieldTracks, out var tracks) && tracks is BsonArray array
            ? array.OfType<BsonDocument>().Select(ToTrack).ToList()
            : []);

    private static PlaylistTrack ToTrack(BsonDocument doc) => new(
        Position: doc.TryGetValue(FieldPosition, out var pos) && !pos.IsBsonNull ? pos.ToInt32() : 0,
        Artist: doc.TryGetValue(FieldArtist, out var artist) && !artist.IsBsonNull ? artist.AsString : "",
        Album: doc.TryGetValue(FieldAlbum, out var album) && !album.IsBsonNull ? album.AsString : "",
        Title: doc.TryGetValue(FieldTrackTitle, out var t) && !t.IsBsonNull ? t.AsString : "",
        File: doc.TryGetValue(FieldFile, out var file) && !file.IsBsonNull ? file.AsString : null);
}
