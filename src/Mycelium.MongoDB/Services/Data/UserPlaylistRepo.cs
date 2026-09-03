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
    private const string FieldMatch = "match";
    private const string FieldRuleList = "rules";
    private const string FieldField = "field";
    private const string FieldOp = "op";
    private const string FieldValue = "value";
    private const string FieldSort = "sort";
    private const string FieldLimit = "limit";
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
        { FieldRules, playlist.Rules is { } rules ? ToDocument(rules) : (BsonValue)BsonNull.Value },
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
        Rules: doc.TryGetValue(FieldRules, out var rules) && rules is BsonDocument ruleDoc
            ? ToRules(ruleDoc)
            : null,
        Tracks: doc.TryGetValue(FieldTracks, out var tracks) && tracks is BsonArray array
            ? array.OfType<BsonDocument>().Select(ToTrack).ToList()
            : []);

    /// <summary>
    /// A rule tree as a nested document rather than a serialised string, so the archive's dump — which
    /// walks BSON into JSON structurally — carries the shape through without having to know this
    /// format, and so a query could one day filter on a rule without parsing anything.
    /// </summary>
    private static BsonDocument ToDocument(PlaylistRules rules)
    {
        var doc = new BsonDocument
        {
            { FieldMatch, rules.Match },
            { FieldRuleList, new BsonArray(rules.Rules.Select(ToDocument)) },
        };

        // Omitted when absent rather than written as null: these are optional parts of a definition,
        // and a null would read as "sorted by nothing" rather than "not sorted".
        if (rules.Sort is { Length: > 0 } sort)
        {
            doc[FieldSort] = sort;
        }

        if (rules.Limit is { } limit)
        {
            doc[FieldLimit] = limit;
        }

        return doc;
    }

    private static BsonDocument ToDocument(PlaylistRule rule) => rule switch
    {
        PlaylistRuleGroup group => new BsonDocument
        {
            { FieldMatch, group.Match },
            { FieldRuleList, new BsonArray(group.Rules.Select(ToDocument)) },
        },
        PlaylistCondition condition => new BsonDocument
        {
            { FieldField, condition.Field },
            { FieldOp, condition.Op },
            { FieldValue, condition.Value },
        },
        _ => new BsonDocument(),
    };

    private static PlaylistRules ToRules(BsonDocument doc) => new(
        Match: Str(doc, FieldMatch) ?? "all",
        Rules: Children(doc),
        Sort: Str(doc, FieldSort),
        Limit: doc.TryGetValue(FieldLimit, out var limit) && limit.IsNumeric ? limit.ToInt32() : null);

    /// <summary>
    /// One node back. A document carrying <c>match</c> is a group and anything else a condition, which
    /// is the same test the writer's shapes imply — and a defensive one: an unrecognised shape becomes
    /// an empty condition rather than throwing and costing the whole playlist.
    /// </summary>
    private static PlaylistRule ToRule(BsonDocument doc) =>
        doc.Contains(FieldMatch)
            ? new PlaylistRuleGroup(Str(doc, FieldMatch) ?? "all", Children(doc))
            : new PlaylistCondition(
                Str(doc, FieldField) ?? "", Str(doc, FieldOp) ?? "", Str(doc, FieldValue) ?? "");

    private static List<PlaylistRule> Children(BsonDocument doc) =>
        doc.TryGetValue(FieldRuleList, out var rules) && rules is BsonArray array
            ? array.OfType<BsonDocument>().Select(ToRule).ToList()
            : [];

    private static string? Str(BsonDocument doc, string field) =>
        doc.TryGetValue(field, out var value) && value.IsString ? value.AsString : null;

    private static PlaylistTrack ToTrack(BsonDocument doc) => new(
        Position: doc.TryGetValue(FieldPosition, out var pos) && !pos.IsBsonNull ? pos.ToInt32() : 0,
        Artist: doc.TryGetValue(FieldArtist, out var artist) && !artist.IsBsonNull ? artist.AsString : "",
        Album: doc.TryGetValue(FieldAlbum, out var album) && !album.IsBsonNull ? album.AsString : "",
        Title: doc.TryGetValue(FieldTrackTitle, out var t) && !t.IsBsonNull ? t.AsString : "",
        File: doc.TryGetValue(FieldFile, out var file) && !file.IsBsonNull ? file.AsString : null);
}
