using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed store of globally blocked albums (see <see cref="IAlbumBlockRepo"/>). One doc per
/// block in the "blockedAlbums" collection, keyed by a lower-cased (artist, album) so blocking the
/// same release twice just refreshes it. The canonical lookup key (which folds typography via the
/// title normalizer) is rebuilt on the Backend side; this _id only dedupes storage — the same split
/// <see cref="AlbumMatchOverrideRepo"/> uses.
/// </summary>
public class AlbumBlockRepo : IAlbumBlockRepo
{
    private const string CollectionName = "blockedAlbums";
    private const string FieldArtist = "artist";
    private const string FieldAlbum = "album";
    private const string FieldBlockedBy = "blockedBy";
    private const string FieldCreatedAt = "createdAt";
    private const string FieldScope = "scope";
    private const string FieldRetryAfter = "retryAfter";

    private readonly IMongoDbProvider _mongoDbProvider;

    public AlbumBlockRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    public async Task<AlbumBlock[]> GetAll()
    {
        var cursor = await Collection.FindAsync(Builders<BsonDocument>.Filter.Empty);
        return (await cursor.ToListAsync()).Select(ToBlock).ToArray();
    }

    public Task Add(AlbumBlock block)
    {
        var update = Builders<BsonDocument>.Update
            .SetOnInsert(FieldCreatedAt, DateTimeOffset.UtcNow.UtcDateTime)
            .Set(FieldArtist, block.Artist)
            .Set(FieldAlbum, block.Album)
            .Set(FieldBlockedBy, block.BlockedBy)
            .Set(FieldScope, block.Scope.ToString())
            // Always written, so a user's permanent skip replacing an earlier timed one clears the
            // stamp rather than inheriting a deadline they didn't ask for.
            .Set(FieldRetryAfter, block.RetryAfter is { } at
                ? (BsonValue)at.UtcDateTime
                : BsonNull.Value);

        return Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", Id(block.Artist, block.Album, block.Scope)),
            update,
            new UpdateOptions { IsUpsert = true });
    }

    public Task Remove(string artist, string album, AlbumBlockScope scope = AlbumBlockScope.Release) =>
        Collection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", Id(artist, album, scope)));

    // The scope is part of the key, so "don't carry this record" and "keep the copy we have" can both
    // stand for the same album without one overwriting the other. Release blocks keep their original
    // id so every row written before scopes existed still resolves.
    private static string Id(string artist, string album, AlbumBlockScope scope) =>
        scope == AlbumBlockScope.Release
            ? $"{artist.ToLowerInvariant()}|{album.ToLowerInvariant()}"
            : $"{artist.ToLowerInvariant()}|{album.ToLowerInvariant()}|{scope.ToString().ToLowerInvariant()}";

    private static AlbumBlock ToBlock(BsonDocument doc)
    {
        string? Str(string f) => doc.TryGetValue(f, out var v) && !v.IsBsonNull ? v.AsString : null;
        // Absent on every row written before scopes existed — all of which were "don't carry this
        // release", the only kind there was.
        var scope = Enum.TryParse<AlbumBlockScope>(Str(FieldScope), out var parsed)
            ? parsed
            : AlbumBlockScope.Release;
        DateTimeOffset? retryAfter = doc.TryGetValue(FieldRetryAfter, out var ra) && ra.IsValidDateTime
            ? new DateTimeOffset(ra.ToUniversalTime(), TimeSpan.Zero)
            : null;
        return new AlbumBlock(
            Str(FieldArtist) ?? "", Str(FieldAlbum) ?? "", Str(FieldBlockedBy), scope, retryAfter);
    }
}
