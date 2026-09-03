using Mycelium.Backend.Services.Background;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// One thumb on the discovery feed, as the API records it: which store the verdict lands in, and
/// which Plex mood the follow-up worker is asked to stamp.
///
/// <para><b>Why this is a service and not a route body.</b> A verdict looks like one write and is
/// actually four decisions — artist or album, upgrade or acquisition, which tag to add, which to
/// strip — and they are not independent: the mood tag a like writes is the one a later dislike has to
/// remove, so the two halves have to agree on how a tag is spelled. Once the batch endpoint existed
/// there were two callers making those decisions, and a second copy that got the *stripping* subtly
/// wrong would not fail anything. It would leave two of <c>&lt;user&gt;_liked</c> /
/// <c>&lt;user&gt;_disliked</c> / <c>&lt;user&gt;_indifferent</c> on an artist, and the only symptom
/// would be a smart playlist quietly matching music the user had moved on from — noticed, if ever,
/// months later. So there is exactly one implementation (<see cref="RateOne"/>) and the batch is a
/// loop over it. The same reasoning is why the strip set is computed by
/// <see cref="ArtistTag.OtherVerdictTags"/> rather than spelled out per call site.</para>
/// </summary>
public class DiscoveryRatingService
{
    private readonly DiscoveryEngine _engine;
    private readonly ArtistFollowUpService _followUps;
    private readonly CollectionService _collections;

    public DiscoveryRatingService(
        DiscoveryEngine engine,
        ArtistFollowUpService followUps,
        CollectionService collections)
    {
        _engine = engine;
        _followUps = followUps;
        _collections = collections;
    }

    /// <summary>
    /// Records one verdict. Extracted verbatim from the single-item route, which now calls it — see the
    /// type summary for why the batch path must not have its own copy.
    ///
    /// <para>Nothing here waits on a third party. The verdict is a Mongo write, and the graph
    /// expansion and Plex tag are handed to the follow-up worker, which is what keeps a thumb from
    /// blocking on the rate-limited source APIs. That is also why a batch of forty is not the load it
    /// looks like: it is forty small writes and forty queued items, not forty round trips.</para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// An "indifferent" verdict on an album — see <see cref="DiscoveryVerdict"/>. The batch reports it
    /// per item; the single route answers 400.
    /// </exception>
    public async Task RateOne(string userId, string? username, DiscoveryRateItem item)
    {
        // Artists take all three verdicts; albums take two and reject the third rather than folding it
        // into a dislike. Splitting the parse is what keeps that from being every caller's job.
        var isArtist = string.IsNullOrEmpty(item.Album);
        var status = isArtist
            ? DiscoveryVerdict.ForArtist(item.Verdict)
            : DiscoveryVerdict.ForAlbum(item.Verdict);

        if (isArtist)
        {
            // Record the verdict — that's what the UI is waiting on — and leave the frontier expansion
            // and the Plex write to the follow-up worker, so a thumb never blocks on the source APIs.
            var depth = await _engine.RecordArtistVerdict(
                userId, item.Artist, status, confirm: item.Confirm == true);
            // The queued Plex write mirrors the verdict as a per-user mood tag ("<username>_liked"/
            // "_disliked"/"_indifferent"), which a music smart playlist can filter on via "Artist Mood".
            // Stamp the new verdict and strip every *other* one, so the latest rating is the only tag
            // left. Not "the opposite" — with three verdicts a flip has two tags to clear, and a
            // ternary that cleared only one would leave a stale verdict behind without failing anything.
            var tag = ArtistTag.For(username, status);
            var staleTags = ArtistTag.OtherVerdictTags(username, status);
            // A thumb also retires the "<username>_recommended" marker, if the artist was carrying one:
            // it means "your likes point here and you haven't decided yet", and this is the deciding —
            // a shrug decides it as much as a thumb does. The nightly sweep (RecommendedArtistTagger)
            // would drop it anyway, but a rated band should not sit in the user's recommended playlist
            // until 6am — and the marker is only ever on artists already in Plex, so this costs nothing
            // beyond the write we're doing regardless.
            var recommendedTag = tag != null ? ArtistTag.Recommended(username) : null;
            _followUps.QueueVerdictFollowUp(
                userId, item.Artist, status, depth,
                addTag: tag,
                removeTags: staleTags.Append(recommendedTag).OfType<string>().ToArray());
        }
        else if (item.Upgrade == true)
        {
            // A thumbs-down on an upgrade card means "keep the copy we have", not "I dislike this
            // album" — the user owns it and presumably likes it. Routed to its own verdict store so
            // it never lands on their Ratings page as a rejection. See DiscoveryEngine.RateUpgrade.
            await _engine.RateUpgrade(userId, username, item.Artist, item.Album, item.AlbumArt, status);
        }
        else
        {
            await _engine.RateAlbum(userId, item.Artist, item.Album, item.AlbumArt, status);
            // A collection has no act that could carry the verdict — "Various Artists" liked would
            // claim every compilation in the library — so an umbrella-credited album is stamped on the
            // album itself, reachable from a smart playlist as "Album Mood". A no-op for every other
            // album, whose artist already carries it. Queued, like the artist write.
            //
            // Note this does *not* move the artist's mood, though CollectionService.Rate does: this
            // path backs a UI that rates artists directly, so a thumbs-down on one record must not
            // strip the "<user>_liked" the user put on the band themselves.
            _collections.QueueTagWrite(username, item.Artist, item.Album, status);
        }
    }

    /// <summary>
    /// Records a whole set of verdicts and reports on each one separately.
    ///
    /// <para><b>Why per-item results.</b> The caller is a migration script working through a playlist:
    /// it thumbs a batch and then waits for those albums to land. A single pass/fail would tell it
    /// nothing it can act on — it would have to re-read the ratings to discover which of the forty
    /// went in — and a batch that aborted on the first failure would leave the set half-applied with
    /// no record of where it stopped. So every item is attempted, each carries its own verdict, and a
    /// failure is data rather than an exception.</para>
    ///
    /// <para><b>Order is kept</b> and the work is sequential. Verdicts on the same artist can appear
    /// twice in one batch (a script correcting itself), and the follow-up worker is a single consumer
    /// in submission order — so running these concurrently would let a like and a later clear land
    /// backwards.</para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// More than <see cref="BatchLimits.MaxItems"/> items. Rejecting the whole batch is the point: see
    /// <see cref="BatchLimits"/> for why this is not a truncation.
    /// </exception>
    public async Task<RateBatchResponse<DiscoveryRateResult>> RateMany(
        string userId, string? username, IReadOnlyList<DiscoveryRateItem> items)
    {
        BatchLimits.Guard(items.Count);

        var results = new List<DiscoveryRateResult>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            try
            {
                await RateOne(userId, username, item);
                results.Add(new DiscoveryRateResult(i, item.Artist, item.Album, Ok: true, Error: null));
            }
            catch (Exception ex)
            {
                // One item's write failing (a Mongo blip, a malformed row) must not cost the other
                // thirty-nine. The reason is reported against the item so the caller can retry that one
                // rather than the whole playlist.
                results.Add(new DiscoveryRateResult(i, item.Artist, item.Album, Ok: false, Error: ex.Message));
            }
        }

        var failed = results.Count(r => !r.Ok);
        return new RateBatchResponse<DiscoveryRateResult>(
            results.Count, results.Count - failed, failed, results);
    }
}

/// <summary>
/// One item of a rating batch — the same five fields the single-item route takes as query parameters.
/// A batch cannot go in a query string, but the shape stays identical so a client can move from one
/// endpoint to the other without re-deriving what a verdict is.
/// </summary>
/// <param name="Verdict">"up", "down" or — on an artist only — "indifferent"; anything else reads as
/// down, exactly as the single-item route has always treated it. See <see cref="DiscoveryVerdict"/>
/// for why an album rejects "indifferent" instead of folding it.</param>
/// <param name="Upgrade">True for a verdict on an <em>upgrade</em> card — see
/// <see cref="DiscoveryEngine.RateUpgrade"/>.</param>
/// <param name="Confirm">
/// True only when this verdict is the user <em>re-affirming</em> one they were just shown — the thumb
/// on a reconsider card. It is what lets the same verdict landing twice retire the artist from the
/// sweep for good (<see cref="IUserQueueRepo.TryConfirmVerdict"/>).
///
/// <para><b>Why this is opt-in rather than inferred.</b> Confirmation used to be derived from the row
/// alone: any like landing on an already-liked artist counted. That reads a repeat as a decision, and
/// most repeats are not decisions — a migration script that likes every artist on a buy list re-likes
/// the same band each time it appears on another playlist, and a library where 44% of artists span two
/// or more playlists would silently confirm nearly half of them. A confirmed verdict leaves
/// <c>GetUnconfirmedVerdicts</c> permanently, so the sweep can never second-guess it again and no UI
/// affordance undoes it: the damage is invisible and unbounded. Whether a thumb is a re-affirmation is
/// something only the caller knows, so the caller says so.</para>
/// </param>
public record DiscoveryRateItem(
    string Artist,
    string? Album,
    string? AlbumArt,
    string Verdict,
    bool? Upgrade = null,
    bool? Confirm = null);

/// <summary>
/// What became of one item of a discovery batch. Carries the artist/album back rather than leaving the
/// caller to line results up against what it sent: <paramref name="Index"/> alone is correct but
/// unreadable in a log, which is where a failed migration run gets diagnosed.
/// </summary>
public record DiscoveryRateResult(int Index, string Artist, string? Album, bool Ok, string? Error);

/// <summary>
/// The envelope every batch endpoint answers with. The counts are redundant — they can be derived from
/// <paramref name="Results"/> — and are there anyway because the caller's first question is always
/// "did all of them go in?", and making it fold the array to find out invites it to get that wrong.
/// </summary>
public record RateBatchResponse<T>(int Total, int Succeeded, int Failed, IReadOnlyList<T> Results);
