using MongoDB.Bson;
using MongoDB.Driver;
using Mycelium.Interfaces;

namespace Mycelium.MongoDB.Services.Data;

/// <summary>
/// Mongo-backed per-user discovery queue. One document per (user, artist) in the "userQueue"
/// collection, keyed "{userId}:{artist}", in the clean BsonDocument-mapping style of
/// <see cref="RelatedArtistRepo"/>. Status drives the swipe loop; score ranks pending candidates.
/// </summary>
public class UserQueueRepo : IUserQueueRepo
{
    private const string CollectionName = "userQueue";
    private const string FieldUserId = "userId";
    private const string FieldArtist = "artist";
    private const string FieldImageUrl = "imageUrl";
    private const string FieldStatus = "status";
    private const string FieldScore = "score";
    private const string FieldSources = "sources";
    private const string FieldDepth = "depth";
    private const string FieldAddedAt = "addedAt";
    private const string FieldDecidedAt = "decidedAt";
    private const string FieldSnoozeUntil = "snoozeUntil";
    // Sticky "this thumbs-down is final" flag, set when a dislike lands on an already-disliked row.
    // Absent on every legacy doc, which reads as false — so old dislikes stay eligible to resurface.
    private const string FieldDislikeConfirmed = "dislikeConfirmed";
    // The mirror: "this thumbs-up is final", set when a like lands on an already-liked row. Same
    // absent-reads-as-false rule, so every pre-existing like is still open to being second-guessed.
    private const string FieldLikeConfirmed = "likeConfirmed";
    // The third of the set: "this shrug is final", set when Indifferent lands on an already-indifferent
    // row. It matters more here than for the other two, because indifference is contradicted in *both*
    // directions — without a terminal state, a band whose song ratings are polarised would be offered
    // back every single week forever. Same absent-reads-as-false rule.
    private const string FieldIndifferentConfirmed = "indifferentConfirmed";
    // The periodic sweep's verdict that this artist's song ratings contradict how it was thumbed, as a
    // subdocument holding the rating snapshot behind it. Present = flagged; the sweep $unsets it to
    // withdraw the verdict, so "is it flagged" and "why" can never disagree. Which way it cuts follows
    // from the row's status for a like or a dislike; for an Indifferent row — the one verdict that can
    // be contradicted either way — it follows from the stored average against the policy threshold.
    // Either way one field serves every side.
    private const string FieldReconsider = "reconsider";
    private const string FieldReconsiderAverage = "average";
    private const string FieldReconsiderRatedCount = "ratedCount";
    private const string FieldReconsiderTrackCount = "trackCount";

    private static readonly string StatusPending = DiscoveryStatus.Pending.ToString();
    private static readonly string StatusLiked = DiscoveryStatus.Liked.ToString();
    private static readonly string StatusDisliked = DiscoveryStatus.Disliked.ToString();
    private static readonly string StatusSnoozed = DiscoveryStatus.Snoozed.ToString();
    private static readonly string StatusIndifferent = DiscoveryStatus.Indifferent.ToString();

    private readonly IMongoDbProvider _mongoDbProvider;

    public UserQueueRepo(IMongoDbProvider mongoDbProvider)
    {
        _mongoDbProvider = mongoDbProvider;
    }

    private IMongoCollection<BsonDocument> Collection =>
        _mongoDbProvider.database.GetCollection<BsonDocument>(CollectionName);

    private static string DocId(string userId, string artistName) => $"{userId}:{artistName}";

    public async Task UpsertCandidates(string userId, IReadOnlyList<DiscoveryCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.UtcDateTime;
        var models = new List<WriteModel<BsonDocument>>(candidates.Count);

        foreach (var c in candidates)
        {
            var name = c.Artist.ArtistName;

            // $setOnInsert seeds immutable fields on first sight; $inc/$addToSet/$min merge a repeat
            // sighting into the existing pending doc (bump score, accrue provenance, shorten depth).
            // The caller filters out decided artists, so the only doc this _id can match is a pending
            // one — never a Liked/Disliked one, so a thumbs-down stays pruned.
            var updates = new List<UpdateDefinition<BsonDocument>>
            {
                Builders<BsonDocument>.Update.SetOnInsert(FieldUserId, userId),
                Builders<BsonDocument>.Update.SetOnInsert(FieldArtist, name),
                Builders<BsonDocument>.Update.SetOnInsert(FieldStatus, StatusPending),
                Builders<BsonDocument>.Update.SetOnInsert(FieldAddedAt, now),
                Builders<BsonDocument>.Update.Inc(FieldScore, c.Score),
                Builders<BsonDocument>.Update.Min(FieldDepth, c.Depth),
                Builders<BsonDocument>.Update.AddToSetEach(FieldSources, c.Sources),
            };

            // Fill the image when this sighting has one; don't clobber an existing image with null.
            if (c.ImageUrl != null)
            {
                updates.Add(Builders<BsonDocument>.Update.Set(FieldImageUrl, c.ImageUrl));
            }

            models.Add(new UpdateOneModel<BsonDocument>(
                Builders<BsonDocument>.Filter.Eq("_id", DocId(userId, name)),
                Builders<BsonDocument>.Update.Combine(updates))
            {
                IsUpsert = true,
            });
        }

        await Collection.BulkWriteAsync(models, new BulkWriteOptions { IsOrdered = false });
    }

    public async Task<HashSet<string>> GetDecidedArtists(string userId)
    {
        var f = Builders<BsonDocument>.Filter;
        // Liked/Disliked/Indifferent are decided forever; a Snoozed row counts as decided only while
        // unexpired, so an expired snooze drops out of this exclusion set and expansion may re-touch it.
        //
        // Indifferent belongs here for the same reason the other two do: it is an answer. It is also the
        // single filter that makes a shrug behave like one — it is what takes the card out of the feed,
        // what stops ExpandFrom re-adding the artist, and (through RecommendedLibraryArtistNames) what
        // makes the nightly sweep strip the artist's "<user>_recommended" marker.
        var filter = f.Eq(FieldUserId, userId)
                     & (f.Eq(FieldStatus, StatusLiked)
                        | f.Eq(FieldStatus, StatusDisliked)
                        | f.Eq(FieldStatus, StatusIndifferent)
                        | (f.Eq(FieldStatus, StatusSnoozed) & f.Gt(FieldSnoozeUntil, DateTimeOffset.UtcNow.UtcDateTime)));
        var cursor = await Collection.FindAsync(
            filter,
            new FindOptions<BsonDocument> { Projection = Builders<BsonDocument>.Projection.Include(FieldArtist) });
        var docs = await cursor.ToListAsync();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in docs)
        {
            if (doc.TryGetValue(FieldArtist, out var a) && !a.IsBsonNull)
            {
                names.Add(a.AsString);
            }
        }

        return names;
    }

    /// <summary>
    /// Rows eligible to be shown right now: still-Pending, plus Snoozed rows whose snooze has expired
    /// (they resurface lazily here — their status stays Snoozed until the user re-rates). This OR-filter
    /// is the single source of truth for resurfacing.
    /// </summary>
    private static FilterDefinition<BsonDocument> EligiblePending(string userId)
    {
        var f = Builders<BsonDocument>.Filter;
        return f.Eq(FieldUserId, userId)
               & (f.Eq(FieldStatus, StatusPending)
                  | (f.Eq(FieldStatus, StatusSnoozed) & f.Lte(FieldSnoozeUntil, DateTimeOffset.UtcNow.UtcDateTime)));
    }

    public async Task<DiscoveryPage> GetPending(string userId, int page, int pageSize)
    {
        var filter = EligiblePending(userId);

        var total = await Collection.CountDocumentsAsync(filter);

        // Highest score first; ties broken by oldest-added so the order is stable across pages.
        var sort = Builders<BsonDocument>.Sort.Descending(FieldScore).Ascending(FieldAddedAt);
        var cursor = await Collection.FindAsync(filter, new FindOptions<BsonDocument>
        {
            Sort = sort,
            Skip = page * pageSize,
            Limit = pageSize,
        });

        var items = (await cursor.ToListAsync()).Select(ToCandidate).ToArray();
        return new DiscoveryPage(items, page, pageSize, total);
    }

    public async Task<long> CountPending(string userId)
    {
        // Same OR-filter as GetPending, so an expired snooze counts as pending (not a spurious rebuild).
        return await Collection.CountDocumentsAsync(EligiblePending(userId));
    }

    public async Task<DiscoveryCandidate?> Rate(string userId, string artistName, DiscoveryStatus status, string? imageUrl)
    {
        var now = DateTimeOffset.UtcNow.UtcDateTime;
        var updates = new List<UpdateDefinition<BsonDocument>>
        {
            // Seed immutable fields on first sight so an owned artist with no prior candidate row
            // (rated straight from the library/Artists page) gets a valid doc; depth 0 means its
            // neighbours expand to depth 1, exactly like an old seed.
            Builders<BsonDocument>.Update.SetOnInsert(FieldUserId, userId),
            Builders<BsonDocument>.Update.SetOnInsert(FieldArtist, artistName),
            Builders<BsonDocument>.Update.SetOnInsert(FieldAddedAt, now),
            Builders<BsonDocument>.Update.SetOnInsert(FieldScore, 0.0),
            Builders<BsonDocument>.Update.SetOnInsert(FieldDepth, 0),
            Builders<BsonDocument>.Update.Set(FieldStatus, status.ToString()),
            Builders<BsonDocument>.Update.Set(FieldDecidedAt, now),
            // The flag was the sweep's argument against the verdict this call is replacing, so it dies
            // with it — otherwise thumbing a "second thoughts" card down would carry its low-rating
            // evidence onto the new dislike and serve it straight back as a "second chance".
            Builders<BsonDocument>.Update.Unset(FieldReconsider),
        };
        if (imageUrl != null)
        {
            updates.Add(Builders<BsonDocument>.Update.Set(FieldImageUrl, imageUrl));
        }

        var doc = await Collection.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", DocId(userId, artistName)),
            Builders<BsonDocument>.Update.Combine(updates),
            new FindOneAndUpdateOptions<BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After,
            });

        return doc == null ? null : ToCandidate(doc);
    }

    public async Task Snooze(string userId, string artistName, DateTimeOffset until, string? imageUrl)
    {
        var now = DateTimeOffset.UtcNow.UtcDateTime;
        var updates = new List<UpdateDefinition<BsonDocument>>
        {
            // Mirror Rate's immutable seeding so an artist with no prior candidate row can be snoozed.
            Builders<BsonDocument>.Update.SetOnInsert(FieldUserId, userId),
            Builders<BsonDocument>.Update.SetOnInsert(FieldArtist, artistName),
            Builders<BsonDocument>.Update.SetOnInsert(FieldAddedAt, now),
            Builders<BsonDocument>.Update.SetOnInsert(FieldScore, 0.0),
            Builders<BsonDocument>.Update.SetOnInsert(FieldDepth, 0),
            Builders<BsonDocument>.Update.Set(FieldStatus, StatusSnoozed),
            Builders<BsonDocument>.Update.Set(FieldSnoozeUntil, until.UtcDateTime),
            Builders<BsonDocument>.Update.Set(FieldDecidedAt, now),
            // Same as Rate: the sweep's flag argued against the verdict this snooze replaces.
            Builders<BsonDocument>.Update.Unset(FieldReconsider),
        };
        if (imageUrl != null)
        {
            updates.Add(Builders<BsonDocument>.Update.Set(FieldImageUrl, imageUrl));
        }

        await Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", DocId(userId, artistName)),
            Builders<BsonDocument>.Update.Combine(updates),
            new UpdateOptions { IsUpsert = true });
    }

    public Task ClearVerdict(string userId, string artistName) =>
        Collection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", DocId(userId, artistName)));

    public async Task<string[]> GetLikedArtistNames(string userId)
    {
        var filter = Builders<BsonDocument>.Filter.Eq(FieldUserId, userId)
                     & Builders<BsonDocument>.Filter.Eq(FieldStatus, StatusLiked);
        var cursor = await Collection.FindAsync(filter, new FindOptions<BsonDocument>
        {
            Projection = Builders<BsonDocument>.Projection.Include(FieldArtist),
        });

        var names = new List<string>();
        foreach (var doc in await cursor.ToListAsync())
        {
            if (doc.TryGetValue(FieldArtist, out var a) && !a.IsBsonNull)
            {
                names.Add(a.AsString);
            }
        }
        return names.ToArray();
    }

    public async Task<ArtistRating[]> GetRated(string userId)
    {
        var filter = Builders<BsonDocument>.Filter.Eq(FieldUserId, userId)
                     & Builders<BsonDocument>.Filter.Ne(FieldStatus, StatusPending);
        var cursor = await Collection.FindAsync(filter, new FindOptions<BsonDocument>
        {
            Sort = Builders<BsonDocument>.Sort.Descending(FieldDecidedAt),
        });

        return (await cursor.ToListAsync()).Select(doc =>
        {
            var c = ToCandidate(doc);
            var status = doc.TryGetValue(FieldStatus, out var s) && !s.IsBsonNull
                && Enum.TryParse<DiscoveryStatus>(s.AsString, out var parsed)
                ? parsed
                : DiscoveryStatus.Pending;
            DateTimeOffset? snoozeUntil = doc.TryGetValue(FieldSnoozeUntil, out var su) && su.IsValidDateTime
                ? new DateTimeOffset(su.ToUniversalTime(), TimeSpan.Zero)
                : null;
            return new ArtistRating(c.Artist, c.ImageUrl, status, snoozeUntil);
        }).ToArray();
    }

    /// <summary>
    /// The stored status string and its sticky "final" flag for a swept verdict. Anything but
    /// Liked/Disliked/Indifferent is a caller bug — the sweep only ever second-guesses a decision.
    /// </summary>
    private static (string Status, string ConfirmField) Verdict(DiscoveryStatus status) => status switch
    {
        DiscoveryStatus.Disliked => (StatusDisliked, FieldDislikeConfirmed),
        DiscoveryStatus.Liked => (StatusLiked, FieldLikeConfirmed),
        DiscoveryStatus.Indifferent => (StatusIndifferent, FieldIndifferentConfirmed),
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status,
            "Only Liked/Disliked/Indifferent verdicts can be confirmed or reconsidered"),
    };

    /// <summary>Verdicts of one kind that haven't been re-affirmed: the sweep's working set.</summary>
    private static FilterDefinition<BsonDocument> UnconfirmedVerdicts(string userId, DiscoveryStatus status)
    {
        var (stored, confirmField) = Verdict(status);
        var f = Builders<BsonDocument>.Filter;
        // $ne also matches docs where the field is absent, so every pre-existing verdict counts as
        // unconfirmed — exactly right: none of them has been given the same thumb twice yet.
        return f.Eq(FieldUserId, userId)
               & f.Eq(FieldStatus, stored)
               & f.Ne(confirmField, true);
    }

    public async Task<SweptArtist[]> GetUnconfirmedVerdicts(string userId, DiscoveryStatus status)
    {
        var cursor = await Collection.FindAsync(
            UnconfirmedVerdicts(userId, status),
            new FindOptions<BsonDocument> { Sort = Builders<BsonDocument>.Sort.Descending(FieldDecidedAt) });

        return (await cursor.ToListAsync())
            .Select(doc =>
            {
                var c = ToCandidate(doc);
                return new SweptArtist(c.Artist, c.ImageUrl, ToSignal(doc));
            })
            .ToArray();
    }

    public async Task SetReconsider(
        string userId, string artistName, DiscoveryStatus status, ReconsiderSignal? signal, string? imageUrl)
    {
        var updates = new List<UpdateDefinition<BsonDocument>>
        {
            signal is null
                ? Builders<BsonDocument>.Update.Unset(FieldReconsider)
                : Builders<BsonDocument>.Update.Set(FieldReconsider, new BsonDocument
                {
                    { FieldReconsiderAverage, signal.Average },
                    { FieldReconsiderRatedCount, signal.RatedCount },
                    { FieldReconsiderTrackCount, signal.TrackCount },
                }),
        };
        // Same "fill, never clobber" rule as Rate/Snooze.
        if (imageUrl != null)
        {
            updates.Add(Builders<BsonDocument>.Update.Set(FieldImageUrl, imageUrl));
        }

        // Status-scoped (never an upsert): if the user re-thumbed or cleared the artist while the sweep
        // was running, this matches nothing rather than stamping evidence onto the new verdict.
        await Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", DocId(userId, artistName))
            & Builders<BsonDocument>.Filter.Eq(FieldStatus, Verdict(status).Status),
            Builders<BsonDocument>.Update.Combine(updates));
    }

    public async Task<ReconsiderCandidate[]> GetReconsiderable(string userId, DiscoveryStatus status)
    {
        var filter = UnconfirmedVerdicts(userId, status)
                     & Builders<BsonDocument>.Filter.Exists(FieldReconsider);
        var cursor = await Collection.FindAsync(filter);

        return (await cursor.ToListAsync())
            .Select(doc => (Candidate: ToCandidate(doc), Signal: ToSignal(doc)))
            // Defensive: Exists() already guarantees a signal, but a hand-edited/partial subdoc would
            // otherwise NRE here rather than just being skipped.
            .Where(x => x.Signal != null)
            .Select(x => new ReconsiderCandidate(x.Candidate.Artist, x.Candidate.ImageUrl, x.Signal!))
            .ToArray();
    }

    /// <summary>The stored sweep verdict on a queue doc, or null when it carries none.</summary>
    private static ReconsiderSignal? ToSignal(BsonDocument doc)
    {
        if (!doc.TryGetValue(FieldReconsider, out var value) || !value.IsBsonDocument)
        {
            return null;
        }

        var sub = value.AsBsonDocument;
        if (!sub.TryGetValue(FieldReconsiderAverage, out var avg) || !avg.IsNumeric)
        {
            return null;
        }

        var rated = sub.TryGetValue(FieldReconsiderRatedCount, out var r) && r.IsNumeric ? r.ToInt32() : 0;
        var tracks = sub.TryGetValue(FieldReconsiderTrackCount, out var t) && t.IsNumeric ? t.ToInt32() : 0;
        return new ReconsiderSignal(avg.ToDouble(), rated, tracks);
    }

    public async Task<bool> TryConfirmVerdict(string userId, string artistName, DiscoveryStatus status)
    {
        var (stored, confirmField) = Verdict(status);
        var f = Builders<BsonDocument>.Filter;
        // The status predicate is the whole point: it only matches when the row *already* holds this
        // same verdict, making this a no-op on a first-time thumb. One atomic update — no
        // read-then-write race.
        var result = await Collection.UpdateOneAsync(
            f.Eq("_id", DocId(userId, artistName)) & f.Eq(FieldStatus, stored),
            Builders<BsonDocument>.Update.Set(confirmField, true));

        return result.MatchedCount > 0;
    }

    public async Task<long> ClearConfirmations(string userId, DiscoveryStatus? status)
    {
        var f = Builders<BsonDocument>.Filter;
        // Unset rather than set-false so a cleared row is indistinguishable from one that was never
        // confirmed: UnconfirmedVerdicts filters on Ne(field, true), which treats absent and false
        // alike, and leaving `false` behind would be a second spelling of the same state.
        var fields = status is null
            ? new[] { FieldLikeConfirmed, FieldDislikeConfirmed, FieldIndifferentConfirmed }
            : new[] { Verdict(status.Value).ConfirmField };

        // Filtered to rows that actually carry one, so the reported count is confirmations removed
        // rather than documents visited — the difference matters when this is run to undo a bulk
        // mistake and the number is the only evidence of what it did.
        var carries = fields.Select(x => f.Eq(x, true)).ToArray();
        var filter = f.Eq(FieldUserId, userId) & f.Or(carries);
        if (status is not null)
        {
            filter &= f.Eq(FieldStatus, Verdict(status.Value).Status);
        }

        var update = Builders<BsonDocument>.Update.Combine(
            fields.Select(x => Builders<BsonDocument>.Update.Unset(x)));
        var result = await Collection.UpdateManyAsync(filter, update);
        return result.ModifiedCount;
    }

    public async Task<DiscoveryCandidate[]> GetLiked(string userId)
    {
        var filter = Builders<BsonDocument>.Filter.Eq(FieldUserId, userId)
                     & Builders<BsonDocument>.Filter.Eq(FieldStatus, StatusLiked);
        var cursor = await Collection.FindAsync(filter, new FindOptions<BsonDocument>
        {
            Sort = Builders<BsonDocument>.Sort.Descending(FieldDecidedAt),
        });
        return (await cursor.ToListAsync()).Select(ToCandidate).ToArray();
    }

    public async Task<DiscoveryCandidate[]> GetAllLiked()
    {
        var filter = Builders<BsonDocument>.Filter.Eq(FieldStatus, StatusLiked);
        var cursor = await Collection.FindAsync(filter, new FindOptions<BsonDocument>
        {
            Sort = Builders<BsonDocument>.Sort.Descending(FieldDecidedAt),
        });
        return (await cursor.ToListAsync()).Select(ToCandidate).ToArray();
    }

    public Task DeletePending(string userId) =>
        Collection.DeleteManyAsync(
            Builders<BsonDocument>.Filter.Eq(FieldUserId, userId)
            & Builders<BsonDocument>.Filter.Eq(FieldStatus, StatusPending));

    public async Task PruneBySource(string userId, string sourceArtist)
    {
        var f = Builders<BsonDocument>.Filter;
        // Only pending rows carry expansion provenance worth pruning — a Liked/Disliked/Snoozed row is
        // the user's own verdict. An equality match on the array field hits docs whose sources contain it.
        var filter = f.Eq(FieldUserId, userId)
                     & f.Eq(FieldStatus, StatusPending)
                     & f.Eq(FieldSources, sourceArtist);
        var docs = await (await Collection.FindAsync(filter)).ToListAsync();
        if (docs.Count == 0)
        {
            return;
        }

        var models = new List<WriteModel<BsonDocument>>(docs.Count);
        foreach (var doc in docs)
        {
            var id = doc["_id"];
            var sources = doc.TryGetValue(FieldSources, out var src) && src.IsBsonArray
                ? src.AsBsonArray.Where(x => !x.IsBsonNull).Select(x => x.AsString).ToList()
                : new List<string>();
            var remaining = sources
                .Where(s => !string.Equals(s, sourceArtist, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (remaining.Count == 0)
            {
                // The departing artist was this candidate's only recommender — it has no reason to stay.
                models.Add(new DeleteOneModel<BsonDocument>(f.Eq("_id", id)));
                continue;
            }

            // Still recommended by other liked artists: strip this provenance and decay the score by the
            // share of sources lost. Exact per-source contributions aren't stored, so scale proportionally
            // — enough to keep the score-ranked order sane without resurrecting the per-source math.
            var score = doc.TryGetValue(FieldScore, out var s) && s.IsNumeric ? s.ToDouble() : 0;
            var decayed = score * remaining.Count / sources.Count;
            models.Add(new UpdateOneModel<BsonDocument>(
                f.Eq("_id", id),
                Builders<BsonDocument>.Update.Set(FieldSources, remaining).Set(FieldScore, decayed)));
        }

        await Collection.BulkWriteAsync(models, new BulkWriteOptions { IsOrdered = false });
    }

    public async Task<string[]> GetAllUserIds()
    {
        var ids = await Collection.DistinctAsync<string>(FieldUserId, Builders<BsonDocument>.Filter.Empty);
        return (await ids.ToListAsync()).ToArray();
    }

    public async Task<CombinedArtistVerdict[]> FindCombinedRatings()
    {
        var filter = Builders<BsonDocument>.Filter.Regex(FieldArtist, new BsonRegularExpression(";"));
        var cursor = await Collection.FindAsync(filter);

        var result = new List<CombinedArtistVerdict>();
        foreach (var doc in await cursor.ToListAsync())
        {
            var userId = doc.TryGetValue(FieldUserId, out var u) && !u.IsBsonNull ? u.AsString : null;
            var artist = doc.TryGetValue(FieldArtist, out var a) && !a.IsBsonNull ? a.AsString : null;
            if (userId == null || artist == null)
            {
                continue;
            }

            var status = doc.TryGetValue(FieldStatus, out var s) && !s.IsBsonNull
                         && Enum.TryParse<DiscoveryStatus>(s.AsString, out var parsed)
                ? parsed
                : DiscoveryStatus.Pending;
            var imageUrl = doc.TryGetValue(FieldImageUrl, out var img) && !img.IsBsonNull ? img.AsString : null;

            result.Add(new CombinedArtistVerdict(userId, artist, status, imageUrl));
        }

        return result.ToArray();
    }

    private static DiscoveryCandidate ToCandidate(BsonDocument doc)
    {
        var artist = doc.TryGetValue(FieldArtist, out var a) && !a.IsBsonNull ? a.AsString : "";
        var imageUrl = doc.TryGetValue(FieldImageUrl, out var img) && !img.IsBsonNull ? img.AsString : null;
        var score = doc.TryGetValue(FieldScore, out var s) && s.IsNumeric ? s.ToDouble() : 0;
        var depth = doc.TryGetValue(FieldDepth, out var d) && d.IsNumeric ? d.ToInt32() : 0;

        var sources = new List<string>();
        if (doc.TryGetValue(FieldSources, out var src) && src.IsBsonArray)
        {
            sources.AddRange(src.AsBsonArray.Where(x => !x.IsBsonNull).Select(x => x.AsString));
        }

        return new DiscoveryCandidate(new ArtistKey(artist), imageUrl, score, sources, depth);
    }
}
