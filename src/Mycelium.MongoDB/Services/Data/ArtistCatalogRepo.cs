using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

public class ArtistCatalogRepo : IArtistCatalogRepo
{
    private const string CollectionName = "artists";
    private const string FieldName = "name";
    private const string FieldImageUrl = "imageUrl";
    private const string FieldLastSeenAt = "lastSeenAt";
    private const string FieldPresent = "present";
    private const string FieldAlbums = "albums";
    // The same albums with the Plex item each lives in: [{ title, key }, ...]. Kept beside the plain
    // title array rather than replacing it — the missing-album diff only ever wants the titles, and
    // docs synced before keys were captured stay readable.
    private const string FieldAlbumKeys = "albumKeys";
    private const string FieldAlbumKeyTitle = "title";
    private const string FieldAlbumKeyRatingKey = "key";
    private const string FieldGenres = "genres";
    private const string FieldPlexRatingKeys = "plexRatingKeys";
    private const string FieldDeezerId = "deezerId";
    private const string FieldDeezerName = "deezerName";
    private const string FieldDeezerFans = "deezerFans";
    private const string FieldDeezerLink = "deezerLink";
    private const string FieldDeezerOverride = "deezerOverride";
    private const string FieldDeezerUnlinked = "deezerUnlinked";
    private const string FieldMusicBrainzMbid = "musicBrainzMbid";
    private const string FieldMusicBrainzName = "musicBrainzName";
    private const string FieldMusicBrainzDisambiguation = "musicBrainzDisambiguation";
    private const string FieldMusicBrainzOverride = "musicBrainzOverride";
    private const string FieldMusicBrainzUnlinked = "musicBrainzUnlinked";

    private readonly IMongoDbProvider _mongoDbProvider;

    public ArtistCatalogRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    public async Task<CatalogSyncResult> SyncFromLibrary(IReadOnlyList<ArtistMetadata> artists, DateTimeOffset syncedAt)
    {
        var collection = Collection;
        var syncedAtUtc = syncedAt.UtcDateTime;

        var writes = new List<WriteModel<BsonDocument>>(artists.Count);
        foreach (var artist in artists)
        {
            var name = artist.ArtistKey.ArtistName;
            var update = Builders<BsonDocument>.Update
                .Set(FieldName, name)
                .Set(FieldLastSeenAt, syncedAtUtc)
                .Set(FieldPresent, true)
                // Plex is the source of truth for genres, so write them every sync (an empty array
                // clears stale tags). Absent on the Deezer backfill path, which never touches this.
                .Set(FieldGenres, new BsonArray(artist.Genres ?? Array.Empty<string>()))
                // Same story for the Plex rating key(s): recaptured every sync so a library rebuild
                // that shifts keys self-heals, and an empty array clears stale keys. Lets the tagger
                // target the exact Plex item(s) instead of scanning the whole library.
                .Set(FieldPlexRatingKeys, new BsonArray(artist.PlexRatingKeys ?? Array.Empty<int>()));

            // Only set the image when we actually have one, so a Plex sync (which
            // currently supplies no image) never clobbers one backfilled elsewhere
            // (e.g. Deezer in a later phase).
            if (artist.ArtistImageUrl != null)
            {
                update = update.Set(FieldImageUrl, artist.ArtistImageUrl);
            }

            writes.Add(new UpdateOneModel<BsonDocument>(
                Builders<BsonDocument>.Filter.Eq("_id", name),
                update)
            {
                IsUpsert = true,
            });
        }

        if (writes.Count > 0)
        {
            await collection.BulkWriteAsync(writes);
        }

        // Anything not touched by this sync is no longer in the Plex library.
        var absent = await collection.UpdateManyAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Lt(FieldLastSeenAt, syncedAtUtc),
                Builders<BsonDocument>.Filter.Eq(FieldPresent, true)),
            Builders<BsonDocument>.Update.Set(FieldPresent, false));

        var totalPresent = await collection.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq(FieldPresent, true));

        return new CatalogSyncResult(
            Upserted: writes.Count,
            MarkedAbsent: (int)absent.ModifiedCount,
            TotalPresent: (int)totalPresent);
    }

    public async Task<int> BackfillImages(IReadOnlyList<ArtistMetadata> artists)
    {
        var writes = new List<WriteModel<BsonDocument>>();
        foreach (var artist in artists)
        {
            if (artist.ArtistImageUrl == null) continue;

            writes.Add(new UpdateOneModel<BsonDocument>(
                Builders<BsonDocument>.Filter.Eq("_id", artist.ArtistKey.ArtistName),
                Builders<BsonDocument>.Update.Set(FieldImageUrl, artist.ArtistImageUrl))
            {
                // Never create entries for artists outside the library — only fill images on
                // artists already cataloged by a Plex sync.
                IsUpsert = false,
            });
        }

        if (writes.Count == 0) return 0;

        var result = await Collection.BulkWriteAsync(writes);
        return (int)result.ModifiedCount;
    }

    public async Task SetGenres(ArtistKey artist, IReadOnlyList<string> genres)
    {
        await Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", artist.ArtistName),
            Builders<BsonDocument>.Update.Set(FieldGenres, new BsonArray(genres)),
            // Never create entries for artists outside the library — a tag edit only mirrors what a
            // Plex sync already put here.
            new UpdateOptions { IsUpsert = false });
    }

    public async Task SyncAlbums(IReadOnlyList<ArtistAlbums> artistAlbums)
    {
        var writes = new List<WriteModel<BsonDocument>>();
        foreach (var entry in artistAlbums)
        {
            writes.Add(new UpdateOneModel<BsonDocument>(
                Builders<BsonDocument>.Filter.Eq("_id", entry.Artist.ArtistName),
                Builders<BsonDocument>.Update
                    .Set(FieldAlbums, new BsonArray(entry.Albums.Select(a => a.Title)))
                    .Set(FieldAlbumKeys, new BsonArray(entry.Albums.Select(ToAlbumKeyDoc))))
            {
                // Albums come from the same Plex pull as the artist list, so the doc already exists;
                // never create phantom entries for an artist not in the catalog.
                IsUpsert = false,
            });
        }

        if (writes.Count == 0) return;
        await Collection.BulkWriteAsync(writes);
    }

    public async Task<Dictionary<string, HashSet<string>>> GetOwnedAlbums()
    {
        var cursor = await Collection.FindAsync(
            Builders<BsonDocument>.Filter.Eq(FieldPresent, true),
            new FindOptions<BsonDocument>
            {
                Projection = Builders<BsonDocument>.Projection.Include(FieldName).Include(FieldAlbums),
            });

        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in await cursor.ToListAsync())
        {
            var name = doc.TryGetValue(FieldName, out var n) && !n.IsBsonNull ? n.AsString : doc["_id"].AsString;
            var albums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (doc.TryGetValue(FieldAlbums, out var a) && a.IsBsonArray)
            {
                foreach (var item in a.AsBsonArray.Where(x => !x.IsBsonNull))
                {
                    albums.Add(item.AsString);
                }
            }
            result[name] = albums;
        }

        return result;
    }

    public async Task<Dictionary<string, Dictionary<string, int>>> GetAlbumPlexRatingKeys(
        IReadOnlyCollection<string> artists)
    {
        var result = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        if (artists.Count == 0) return result;

        var cursor = await Collection.FindAsync(
            Builders<BsonDocument>.Filter.In("_id", artists),
            new FindOptions<BsonDocument>
            {
                Projection = Builders<BsonDocument>.Projection.Include(FieldName).Include(FieldAlbumKeys),
            });

        foreach (var doc in await cursor.ToListAsync())
        {
            var name = doc.TryGetValue(FieldName, out var n) && !n.IsBsonNull ? n.AsString : doc["_id"].AsString;
            var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (doc.TryGetValue(FieldAlbumKeys, out var a) && a.IsBsonArray)
            {
                foreach (var entry in a.AsBsonArray.OfType<BsonDocument>())
                {
                    if (entry.TryGetValue(FieldAlbumKeyTitle, out var title) && title.IsString
                        && entry.TryGetValue(FieldAlbumKeyRatingKey, out var key) && key.IsNumeric)
                    {
                        keys[title.AsString] = key.ToInt32();
                    }
                }
            }
            result[name] = keys;
        }

        return result;
    }

    private static BsonDocument ToAlbumKeyDoc(OwnedAlbum album) => new()
    {
        { FieldAlbumKeyTitle, album.Title },
        { FieldAlbumKeyRatingKey, album.PlexRatingKey },
    };

    public async Task<string[]> FindCombinedArtistNames()
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq(FieldPresent, true),
            Builders<BsonDocument>.Filter.Regex(FieldName, new BsonRegularExpression(";")));
        var cursor = await Collection.FindAsync(filter, new FindOptions<BsonDocument>
        {
            Projection = Builders<BsonDocument>.Projection.Include(FieldName),
        });

        return (await cursor.ToListAsync())
            .Select(d => d.TryGetValue(FieldName, out var n) && !n.IsBsonNull ? n.AsString : d["_id"].AsString)
            .ToArray();
    }

    public async Task SplitCombinedArtist(string combinedName, IReadOnlyList<string> parts, DateTimeOffset syncedAt)
    {
        var syncedAtUtc = syncedAt.UtcDateTime;

        // Carry the combined doc's albums onto each split artist (same fan-out a Plex sync would do).
        var combined = await (await Collection.FindAsync(
            Builders<BsonDocument>.Filter.Eq("_id", combinedName))).FirstOrDefaultAsync();
        var albums = combined != null && combined.TryGetValue(FieldAlbums, out var a) && a.IsBsonArray
            ? a.AsBsonArray.Where(x => !x.IsBsonNull).Select(x => x.AsString).ToArray()
            : Array.Empty<string>();
        // The album -> Plex item pairs travel with the titles, so a split artist keeps its deep links.
        var albumKeys = combined != null && combined.TryGetValue(FieldAlbumKeys, out var k) && k.IsBsonArray
            ? k.AsBsonArray.OfType<BsonDocument>().ToArray()
            : Array.Empty<BsonDocument>();

        var writes = new List<WriteModel<BsonDocument>>(parts.Count + 1);
        foreach (var part in parts)
        {
            var update = Builders<BsonDocument>.Update
                .Set(FieldName, part)
                .Set(FieldLastSeenAt, syncedAtUtc)
                .Set(FieldPresent, true);
            if (albums.Length > 0)
            {
                update = update.AddToSetEach(FieldAlbums, albums);
            }
            if (albumKeys.Length > 0)
            {
                update = update.AddToSetEach(FieldAlbumKeys, albumKeys);
            }

            writes.Add(new UpdateOneModel<BsonDocument>(
                Builders<BsonDocument>.Filter.Eq("_id", part), update) { IsUpsert = true });
        }

        writes.Add(new DeleteOneModel<BsonDocument>(Builders<BsonDocument>.Filter.Eq("_id", combinedName)));
        await Collection.BulkWriteAsync(writes);
    }

    public async Task<CatalogArtist[]> GetAllPresent()
    {
        var collection = Collection;
        var cursor = await collection.FindAsync(
            Builders<BsonDocument>.Filter.Eq(FieldPresent, true),
            new FindOptions<BsonDocument>
            {
                Sort = Builders<BsonDocument>.Sort.Ascending(FieldName),
            });

        return (await cursor.ToListAsync())
            .Select(ToCatalogArtist)
            .ToArray();
    }

    public async Task<IReadOnlyList<int>> GetPlexRatingKeys(ArtistKey artist)
    {
        var doc = await (await Collection.FindAsync(
            Builders<BsonDocument>.Filter.Eq("_id", artist.ArtistName))).FirstOrDefaultAsync();
        if (doc == null || !doc.TryGetValue(FieldPlexRatingKeys, out var v) || !v.IsBsonArray)
        {
            return Array.Empty<int>();
        }

        return v.AsBsonArray.Where(e => e.IsNumeric).Select(e => e.ToInt32()).ToArray();
    }

    public async Task<(DeezerIdentity Identity, bool IsOverride)?> GetDeezer(ArtistKey artist)
    {
        var doc = await (await Collection.FindAsync(
            Builders<BsonDocument>.Filter.Eq("_id", artist.ArtistName))).FirstOrDefaultAsync();
        if (doc == null) return null;

        var identity = ToDeezerIdentity(doc);
        if (identity == null) return null;

        var isOverride = doc.TryGetValue(FieldDeezerOverride, out var o) && o.IsBoolean && o.AsBoolean;
        return (identity, isOverride);
    }

    public async Task SetDeezerIdentity(ArtistKey artist, DeezerIdentity identity, bool isOverride)
    {
        BsonValue name = identity.Name != null ? new BsonString(identity.Name) : BsonNull.Value;
        BsonValue fans = identity.Fans.HasValue ? new BsonInt32(identity.Fans.Value) : BsonNull.Value;
        BsonValue link = identity.Link != null ? new BsonString(identity.Link) : BsonNull.Value;

        var update = Builders<BsonDocument>.Update
            .Set(FieldDeezerId, new BsonInt64(identity.Id))
            .Set(FieldDeezerName, name)
            .Set(FieldDeezerFans, fans)
            .Set(FieldDeezerLink, link)
            .Set(FieldDeezerOverride, isOverride)
            // Storing a resolved id means the artist is linked — drop any sticky "detached" flag.
            .Unset(FieldDeezerUnlinked);

        // The photo doubles as the displayed artist image; only set it when Deezer supplied one
        // so a missing image never blanks an existing photo.
        if (identity.ImageUrl != null)
        {
            update = update.Set(FieldImageUrl, identity.ImageUrl);
        }

        var filter = Builders<BsonDocument>.Filter.Eq("_id", artist.ArtistName);
        if (!isOverride)
        {
            // Opportunistic writes must never overturn a user's pin.
            filter = Builders<BsonDocument>.Filter.And(
                filter,
                Builders<BsonDocument>.Filter.Ne(FieldDeezerOverride, true));
        }

        // Never create entries for artists outside the catalog.
        await Collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = false });
    }

    public async Task ClearDeezerOverride(ArtistKey artist)
    {
        var update = Builders<BsonDocument>.Update
            .Unset(FieldDeezerId)
            .Unset(FieldDeezerName)
            .Unset(FieldDeezerFans)
            .Unset(FieldDeezerLink)
            .Unset(FieldDeezerOverride)
            .Unset(FieldDeezerUnlinked);

        await Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", artist.ArtistName), update);
    }

    public async Task<bool> IsDeezerUnlinked(ArtistKey artist)
    {
        var doc = await (await Collection.FindAsync(
            Builders<BsonDocument>.Filter.Eq("_id", artist.ArtistName))).FirstOrDefaultAsync();
        return doc != null && doc.TryGetValue(FieldDeezerUnlinked, out var u) && u.IsBoolean && u.AsBoolean;
    }

    public async Task SetDeezerUnlinked(ArtistKey artist)
    {
        // Clear any resolved id and pin the sticky "unlinked" flag — the artist has no Deezer match,
        // so resolution must return null instead of re-guessing by name. Leave the artist photo intact.
        var update = Builders<BsonDocument>.Update
            .Unset(FieldDeezerId)
            .Unset(FieldDeezerName)
            .Unset(FieldDeezerFans)
            .Unset(FieldDeezerLink)
            .Unset(FieldDeezerOverride)
            .Set(FieldDeezerUnlinked, true);

        // Never create entries for artists outside the catalog.
        await Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", artist.ArtistName), update,
            new UpdateOptions { IsUpsert = false });
    }

    private static DeezerIdentity? ToDeezerIdentity(BsonDocument doc)
    {
        if (!doc.TryGetValue(FieldDeezerId, out var id) || !id.IsNumeric) return null;

        var name = doc.TryGetValue(FieldDeezerName, out var n) && !n.IsBsonNull ? n.AsString : null;
        var fans = doc.TryGetValue(FieldDeezerFans, out var f) && f.IsNumeric ? f.ToInt32() : (int?)null;
        var link = doc.TryGetValue(FieldDeezerLink, out var l) && !l.IsBsonNull ? l.AsString : null;
        var image = doc.TryGetValue(FieldImageUrl, out var img) && !img.IsBsonNull ? img.AsString : null;

        return new DeezerIdentity(id.ToInt64(), name, fans, link, image);
    }

    public async Task<(MusicBrainzIdentity Identity, bool IsOverride)?> GetMusicBrainz(ArtistKey artist)
    {
        var doc = await (await Collection.FindAsync(
            Builders<BsonDocument>.Filter.Eq("_id", artist.ArtistName))).FirstOrDefaultAsync();
        if (doc == null) return null;

        var identity = ToMusicBrainzIdentity(doc);
        if (identity == null) return null;

        var isOverride = doc.TryGetValue(FieldMusicBrainzOverride, out var o) && o.IsBoolean && o.AsBoolean;
        return (identity, isOverride);
    }

    public async Task SetMusicBrainzIdentity(ArtistKey artist, MusicBrainzIdentity identity, bool isOverride)
    {
        BsonValue name = identity.Name != null ? new BsonString(identity.Name) : BsonNull.Value;
        BsonValue disambiguation = identity.Disambiguation != null
            ? new BsonString(identity.Disambiguation)
            : BsonNull.Value;

        var update = Builders<BsonDocument>.Update
            .Set(FieldMusicBrainzMbid, new BsonString(identity.Mbid))
            .Set(FieldMusicBrainzName, name)
            .Set(FieldMusicBrainzDisambiguation, disambiguation)
            .Set(FieldMusicBrainzOverride, isOverride)
            // Storing a resolved MBID means the artist is linked — drop any sticky "detached" flag.
            .Unset(FieldMusicBrainzUnlinked);

        var filter = Builders<BsonDocument>.Filter.Eq("_id", artist.ArtistName);
        if (!isOverride)
        {
            // Opportunistic writes must never overturn a user's pin.
            filter = Builders<BsonDocument>.Filter.And(
                filter,
                Builders<BsonDocument>.Filter.Ne(FieldMusicBrainzOverride, true));
        }

        // Never create entries for artists outside the catalog.
        await Collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = false });
    }

    public async Task ClearMusicBrainzOverride(ArtistKey artist)
    {
        var update = Builders<BsonDocument>.Update
            .Unset(FieldMusicBrainzMbid)
            .Unset(FieldMusicBrainzName)
            .Unset(FieldMusicBrainzDisambiguation)
            .Unset(FieldMusicBrainzOverride)
            .Unset(FieldMusicBrainzUnlinked);

        await Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", artist.ArtistName), update);
    }

    public async Task<bool> IsMusicBrainzUnlinked(ArtistKey artist)
    {
        var doc = await (await Collection.FindAsync(
            Builders<BsonDocument>.Filter.Eq("_id", artist.ArtistName))).FirstOrDefaultAsync();
        return doc != null && doc.TryGetValue(FieldMusicBrainzUnlinked, out var u) && u.IsBoolean && u.AsBoolean;
    }

    public async Task SetMusicBrainzUnlinked(ArtistKey artist)
    {
        // Clear any resolved MBID and pin the sticky "unlinked" flag — the artist has no MusicBrainz
        // match, so resolution must return null instead of re-guessing by name.
        var update = Builders<BsonDocument>.Update
            .Unset(FieldMusicBrainzMbid)
            .Unset(FieldMusicBrainzName)
            .Unset(FieldMusicBrainzDisambiguation)
            .Unset(FieldMusicBrainzOverride)
            .Set(FieldMusicBrainzUnlinked, true);

        // Never create entries for artists outside the catalog.
        await Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", artist.ArtistName), update,
            new UpdateOptions { IsUpsert = false });
    }

    private static MusicBrainzIdentity? ToMusicBrainzIdentity(BsonDocument doc)
    {
        if (!doc.TryGetValue(FieldMusicBrainzMbid, out var mbid) || mbid.IsBsonNull
            || mbid is not BsonString { Value.Length: > 0 }) return null;

        var name = doc.TryGetValue(FieldMusicBrainzName, out var n) && !n.IsBsonNull ? n.AsString : null;
        var disambiguation = doc.TryGetValue(FieldMusicBrainzDisambiguation, out var d) && !d.IsBsonNull
            ? d.AsString
            : null;

        return new MusicBrainzIdentity(mbid.AsString, name, disambiguation);
    }

    private static CatalogArtist ToCatalogArtist(BsonDocument doc)
    {
        var name = doc.TryGetValue(FieldName, out var n) && !n.IsBsonNull
            ? n.AsString
            : doc["_id"].AsString;

        var imageUrl = doc.TryGetValue(FieldImageUrl, out var img) && !img.IsBsonNull
            ? img.AsString
            : null;

        var lastSeenAt = doc.TryGetValue(FieldLastSeenAt, out var ls) && ls.IsValidDateTime
            ? new DateTimeOffset(ls.ToUniversalTime(), TimeSpan.Zero)
            : default;

        var deezer = ToDeezerIdentity(doc);
        var deezerOverride = doc.TryGetValue(FieldDeezerOverride, out var o) && o.IsBoolean && o.AsBoolean;

        var musicBrainz = ToMusicBrainzIdentity(doc);
        var musicBrainzOverride = doc.TryGetValue(FieldMusicBrainzOverride, out var mo) && mo.IsBoolean && mo.AsBoolean;

        var genres = doc.TryGetValue(FieldGenres, out var g) && g.IsBsonArray
            ? g.AsBsonArray.Where(x => !x.IsBsonNull).Select(x => x.AsString).ToArray()
            : Array.Empty<string>();

        return new CatalogArtist(
            new ArtistKey(name), imageUrl, lastSeenAt, deezer, deezerOverride, genres,
            musicBrainz, musicBrainzOverride);
    }
}
