using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed shared acquisition list. One doc per item in the "purchases" collection, keyed by
/// <see cref="PurchaseKey"/>. Global (not per-user) — the unified maintainer queue. Display fields
/// are refreshed on every upsert; status/requestedAt are insert-only so a reconcile never demotes a
/// Sent/InLibrary row.
/// </summary>
public class PurchaseRepo : IPurchaseRepo
{
    private const string CollectionName = "purchases";
    private const string FieldKind = "kind";
    private const string FieldArtist = "artist";
    private const string FieldAlbum = "album";
    private const string FieldImageUrl = "imageUrl";
    private const string FieldScore = "score";
    private const string FieldSources = "sources";
    private const string FieldStatus = "status";
    private const string FieldRequestedAt = "requestedAt";
    private const string FieldSentAt = "sentAt";
    private const string FieldInLibraryAt = "inLibraryAt";
    private const string FieldDeezerAlbumId = "deezerAlbumId";
    private const string FieldAlbumArtist = "albumArtist";
    private const string FieldFailure = "failure";
    private const string FieldManual = "manual";
    private const string FieldTargetQuality = "targetQuality";
    private const string FieldAcquiredQuality = "acquiredQuality";
    private const string FieldOwnedQuality = "ownedQuality";
    private const string FieldAddedBy = "addedBy";

    private readonly IMongoDbProvider _mongoDbProvider;

    public PurchaseRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    public async Task<PurchaseItem[]> GetAll()
    {
        var cursor = await Collection.FindAsync(
            Builders<BsonDocument>.Filter.Empty,
            new FindOptions<BsonDocument> { Sort = Builders<BsonDocument>.Sort.Descending(FieldRequestedAt) });
        return (await cursor.ToListAsync()).Select(ToItem).ToArray();
    }

    public Task Upsert(PurchaseItem item)
    {
        var update = Builders<BsonDocument>.Update
            .SetOnInsert(FieldStatus, PurchaseStatus.Pending.ToString())
            .SetOnInsert(FieldRequestedAt, DateTimeOffset.UtcNow.UtcDateTime)
            .Set(FieldKind, item.Kind.ToString())
            .Set(FieldArtist, item.Artist.ArtistName)
            .Set(FieldAlbum, (BsonValue)(item.Album ?? (BsonValue)BsonNull.Value))
            .Set(FieldImageUrl, (BsonValue)(item.ImageUrl ?? (BsonValue)BsonNull.Value))
            .Set(FieldScore, item.Score)
            .Set(FieldSources, new BsonArray(item.Sources))
            .SetOnInsert(FieldManual, item.Manual);

        // The Deezer id and the album-artist are immutable facts we may only learn once (while the
        // album is still in the missing set). Set them when we have them, but never overwrite a known
        // value back to null on a later reconcile where the missing set no longer supplies it — a row
        // that loses its id becomes permanently un-downloadable. That is the normal case for a manual
        // row, whose id came from a pasted link and was never in the missing set at all.
        if (item.DeezerAlbumId is not null)
        {
            update = update.Set(FieldDeezerAlbumId, new BsonInt64(item.DeezerAlbumId.Value));
        }

        if (item.AlbumArtist != null)
        {
            update = update.Set(FieldAlbumArtist, item.AlbumArtist);
        }

        // Unlike the two above, this is re-Set on every reconcile rather than learned once: it is
        // derived from who currently wants the album, so a lossless user liking something a lossy
        // user had already queued must raise the row's target before it downloads.
        if (item.TargetQuality is not null)
        {
            update = update.Set(FieldTargetQuality, item.TargetQuality.Value.ToString());
        }

        // Re-Set like the target, not learned-once like the Deezer id: the copy on disk changes when
        // an upgrade lands, and a stale value would make the next swap compare against the old one.
        if (item.OwnedQuality is not null)
        {
            update = update.Set(FieldOwnedQuality, item.OwnedQuality.Value.ToString());
        }

        // Insert-only, like the status: who asked for a record is a fact about the moment it was
        // requested, and a reconcile refreshing display fields has nothing to say about it. The
        // download button claims it on an existing row through SetAddedBy instead.
        if (item.AddedBy != null)
        {
            update = update.SetOnInsert(FieldAddedBy, item.AddedBy);
        }

        return Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", item.Id),
            update,
            new UpdateOptions { IsUpsert = true });
    }

    /// <summary>
    /// Claims the "added by" credit for <paramref name="username"/> only if it's unclaimed. The
    /// unclaimed test is part of the filter rather than a read-then-write, so two people pressing
    /// Download at once can't both win. A Mongo equality against null matches a missing field too,
    /// which is what covers every row written before this field existed.
    /// </summary>
    public async Task<bool> SetAddedBy(string id, string username)
    {
        var result = await Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", id),
                Builders<BsonDocument>.Filter.Eq(FieldAddedBy, BsonNull.Value)),
            Builders<BsonDocument>.Update.Set(FieldAddedBy, username));
        return result.ModifiedCount > 0;
    }

    public async Task<bool> SetStatus(
        string id,
        PurchaseStatus status,
        DownloadFailure failure = DownloadFailure.None,
        AudioQuality? acquired = null)
    {
        // Written on every transition, not just failures: a row moving back to Queued/Pending for a
        // retry must lose the previous reason, or the page would keep explaining a failure that no
        // longer applies.
        var update = Builders<BsonDocument>.Update
            .Set(FieldStatus, status.ToString())
            .Set(FieldFailure, (status == PurchaseStatus.Failed ? failure : DownloadFailure.None).ToString());
        if (status == PurchaseStatus.Sent)
        {
            update = update.Set(FieldSentAt, DateTimeOffset.UtcNow.UtcDateTime);
        }

        // The terminal stamp, written the same way SentAt is: on the transition, by whoever makes it.
        // Set rather than SetOnInsert-style first-wins because a row can genuinely arrive twice — an
        // upgrade sends a closed-out album back to Pending to be re-fetched — and the stamp should then
        // name the arrival of the copy that is actually on the shelf, not a copy that was replaced.
        if (status == PurchaseStatus.InLibrary)
        {
            update = update.Set(FieldInLibraryAt, DateTimeOffset.UtcNow.UtcDateTime);
        }

        // Only ever written, never cleared: a backend that couldn't report what it got shouldn't
        // erase what an earlier attempt did report.
        if (acquired is not null)
        {
            update = update.Set(FieldAcquiredQuality, acquired.Value.ToString());
        }

        var result = await Collection.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", id), update);
        return result.MatchedCount > 0;
    }

    public Task Remove(string id) =>
        Collection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", id));

    private static PurchaseItem ToItem(BsonDocument doc)
    {
        string Str(string f) => doc.TryGetValue(f, out var v) && !v.IsBsonNull ? v.AsString : "";
        string? StrN(string f) => doc.TryGetValue(f, out var v) && !v.IsBsonNull ? v.AsString : null;

        var kind = Enum.TryParse<FeedKind>(Str(FieldKind), out var k) ? k : FeedKind.RecommendedArtist;
        var status = Enum.TryParse<PurchaseStatus>(Str(FieldStatus), out var s) ? s : PurchaseStatus.Pending;
        var sources = doc.TryGetValue(FieldSources, out var src) && src.IsBsonArray
            ? src.AsBsonArray.Select(x => x.AsString).ToArray()
            : Array.Empty<string>();
        var score = doc.TryGetValue(FieldScore, out var sc) && sc.IsNumeric ? sc.ToDouble() : 0;
        var requestedAt = doc.TryGetValue(FieldRequestedAt, out var ra) && ra.IsValidDateTime
            ? (DateTimeOffset)ra.ToUniversalTime()
            : DateTimeOffset.MinValue;
        DateTimeOffset? sentAt = doc.TryGetValue(FieldSentAt, out var sa) && sa.IsValidDateTime
            ? (DateTimeOffset)sa.ToUniversalTime()
            : null;
        DateTimeOffset? inLibraryAt = doc.TryGetValue(FieldInLibraryAt, out var la) && la.IsValidDateTime
            ? (DateTimeOffset)la.ToUniversalTime()
            : null;
        long? deezerAlbumId = doc.TryGetValue(FieldDeezerAlbumId, out var da) && da.IsNumeric
            ? da.ToInt64()
            : null;

        // Rows written before failure tracking have no field, and an unparseable value is treated the
        // same way: an unexplained failure, which is what the page showed before this existed.
        var failure = Enum.TryParse<DownloadFailure>(Str(FieldFailure), out var f) ? f : DownloadFailure.None;

        // Absent on every row written before hand-added purchases existed — all of which came from a
        // rating, so false is the correct reading.
        var manual = doc.TryGetValue(FieldManual, out var mn) && mn.IsBoolean && mn.AsBoolean;

        return new PurchaseItem(
            doc["_id"].AsString, kind, new ArtistKey(Str(FieldArtist)), StrN(FieldAlbum),
            StrN(FieldImageUrl), score, sources, status, requestedAt, sentAt, deezerAlbumId,
            StrN(FieldAlbumArtist), failure, manual,
            // Absent on rows written before tiers existed; the downloader then uses its configured
            // quality, which is exactly what those rows would have downloaded at anyway.
            AudioQualityTier.Parse(StrN(FieldTargetQuality)),
            AudioQualityTier.Parse(StrN(FieldAcquiredQuality)),
            AudioQualityTier.Parse(StrN(FieldOwnedQuality)),
            // Absent on every row written before the "added by" credit existed, and on anything that
            // downloaded without a person pressing for it.
            StrN(FieldAddedBy),
            // Absent until the row closes out, and on every InLibrary row that closed out before this
            // field existed — which reads as "finished, arrival time unknown" rather than "unfinished",
            // since Status is still what says the row is done.
            inLibraryAt);
    }
}
