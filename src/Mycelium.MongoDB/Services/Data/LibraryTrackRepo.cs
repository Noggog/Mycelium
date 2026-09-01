using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed copy of the library's track listing (see <see cref="ILibraryTrackRepo"/>). One doc per
/// track in the "libraryTracks" collection, keyed by the file path — the only track identity that
/// survives the server being rebuilt.
///
/// <para>This is the largest collection in the system by row count (a library of a few thousand albums
/// runs to tens of thousands of tracks), so the replace is done as one unordered bulk write rather
/// than a document at a time.</para>
/// </summary>
public class LibraryTrackRepo : ILibraryTrackRepo
{
    private const string CollectionName = "libraryTracks";
    private const string FieldArtist = "artist";
    private const string FieldAlbum = "album";
    private const string FieldTitle = "title";
    private const string FieldTrackNumber = "trackNumber";
    private const string FieldFile = "file";

    private readonly IMongoDbProvider _mongoDbProvider;

    public LibraryTrackRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    public async Task<int> ReplaceAll(IReadOnlyList<LibraryTrack> tracks)
    {
        await Collection.DeleteManyAsync(Builders<BsonDocument>.Filter.Empty);

        if (tracks.Count == 0)
        {
            return 0;
        }

        // Deduped on the way in: a library can hold the same path twice across sections, and one
        // duplicate would otherwise fail the whole batch.
        var docs = tracks
            .GroupBy(Id, StringComparer.Ordinal)
            .Select(g => ToDocument(g.Key, g.First()))
            .ToList();

        // Unordered so one bad row can't abort the rest, and batched because this is the biggest write
        // the app makes.
        await Collection.InsertManyAsync(docs, new InsertManyOptions { IsOrdered = false });
        return docs.Count;
    }

    public async Task<LibraryTrack[]> GetAll()
    {
        var cursor = await Collection.FindAsync(Builders<BsonDocument>.Filter.Empty);
        return (await cursor.ToListAsync()).Select(ToTrack).ToArray();
    }

    private static string Id(LibraryTrack track) =>
        string.IsNullOrWhiteSpace(track.File)
            ? $"{track.Artist}|{track.Album}|{track.Title}"
            : track.File;

    private static BsonDocument ToDocument(string id, LibraryTrack track) => new()
    {
        { "_id", id },
        { FieldArtist, track.Artist },
        { FieldAlbum, track.Album },
        { FieldTitle, track.Title },
        { FieldTrackNumber, track.TrackNumber is { } n ? n : BsonNull.Value },
        { FieldFile, track.File ?? (BsonValue)BsonNull.Value },
    };

    private static LibraryTrack ToTrack(BsonDocument doc) => new(
        Artist: doc.TryGetValue(FieldArtist, out var artist) && !artist.IsBsonNull ? artist.AsString : "",
        Album: doc.TryGetValue(FieldAlbum, out var album) && !album.IsBsonNull ? album.AsString : "",
        Title: doc.TryGetValue(FieldTitle, out var title) && !title.IsBsonNull ? title.AsString : "",
        TrackNumber: doc.TryGetValue(FieldTrackNumber, out var n) && !n.IsBsonNull ? n.ToInt32() : null,
        File: doc.TryGetValue(FieldFile, out var file) && !file.IsBsonNull ? file.AsString : null);
}
