using System.Text.Json.Serialization;

namespace Mycelium.Interfaces;

/// <summary>Where a candidate sits in the per-user swipe loop.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiscoveryStatus
{
    /// <summary>Awaiting a swipe — eligible to be shown.</summary>
    Pending,

    /// <summary>Thumbs-up: grows the frontier and lands on the "to buy" wishlist.</summary>
    Liked,

    /// <summary>Thumbs-down: pruned — never shown or expanded again.</summary>
    Disliked,

    /// <summary>
    /// Temporarily hidden until <c>snoozeUntil</c> passes, then it resurfaces as pending (lazily, on
    /// the next read). Counts as decided while unexpired (not re-added by expansion); once expired it
    /// drops back out of the decided set so the frontier may re-touch it.
    /// </summary>
    Snoozed,

    /// <summary>
    /// A shrug: the user has heard enough to decide they have no opinion. The third verdict, and the
    /// only one that decides <em>without</em> taking a side.
    ///
    /// <para><b>What it does.</b> Counts as decided — the card leaves the feed for good and expansion
    /// never re-adds it — but grows nothing: no frontier, no wishlist, no "&lt;user&gt;_recommended".
    /// <b>What it deliberately doesn't do</b> is block playback. A dislike is subtracted from the Deep
    /// Frontier playlist; indifference is not, so the band stays in rotation. That is the whole point:
    /// the shrug used to have to be spent as a rejection, which took music out of rotation the user
    /// never objected to.</para>
    ///
    /// <para>It is not a permanent silence either. The reconsider sweep second-guesses it in
    /// <em>both</em> directions — an indifferent band whose own song ratings are high comes back
    /// offering the thumbs-up, a poorly-rated one the thumbs-down — so an unopinionated verdict is
    /// expected to resolve into a real one eventually, once the user has actually heard the band. A
    /// second shrug confirms it and retires it from the sweep for good.</para>
    ///
    /// <para>Artists only. An album thumbs-down already <em>means</em> "meh, hide this from my feed"
    /// and an upgrade thumbs-down means "keep the copy I have", so both already occupy this slot;
    /// see the album verdict parse in <c>DiscoveryRatingService</c>, which rejects it outright.</para>
    /// </summary>
    Indifferent,
}

/// <summary>
/// The wire spelling of a verdict — what a client puts in <c>?verdict=</c> or a batch item — and the
/// one place it is turned into a <see cref="DiscoveryStatus"/>.
///
/// <para><b>Why artists and albums parse differently.</b> The long-standing contract is "anything that
/// isn't <c>up</c> reads as down", and clients rely on it, so it is kept. But admitting a
/// <em>new</em> token to that fold is a different thing entirely: <c>verdict=indifferent&amp;album=X</c>
/// would silently record a dislike on the album, writing a row no label map can render and that
/// <c>GetDecidedKeys</c> swallows whole — the album vanishes from the missing-albums feed with no
/// affordance anywhere to undo it. Indifference is an artist verdict (see
/// <see cref="DiscoveryStatus.Indifferent"/>), so on an album it is rejected at the door rather than
/// quietly reinterpreted.</para>
/// </summary>
public static class DiscoveryVerdict
{
    public const string Up = "up";
    public const string Down = "down";
    public const string Indifferent = "indifferent";

    /// <summary>
    /// An artist verdict. <c>up</c> is a like, <c>indifferent</c> a shrug, and anything else a dislike
    /// — the historical fold, kept for every client that has ever sent something other than "down".
    /// </summary>
    public static DiscoveryStatus ForArtist(string verdict) =>
        verdict.Equals(Up, StringComparison.OrdinalIgnoreCase) ? DiscoveryStatus.Liked
        : verdict.Equals(Indifferent, StringComparison.OrdinalIgnoreCase) ? DiscoveryStatus.Indifferent
        : DiscoveryStatus.Disliked;

    /// <summary>
    /// An album (or collection) verdict: <c>up</c> is a like, anything else a dislike — except
    /// <c>indifferent</c>, which is not a thing an album can be.
    /// </summary>
    /// <exception cref="ArgumentException"><c>indifferent</c> on an album.</exception>
    public static DiscoveryStatus ForAlbum(string verdict)
    {
        if (verdict.Equals(Indifferent, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "'indifferent' is an artist verdict. An album thumbs-down already means \"meh, hide "
                + "this from my feed\", and an upgrade thumbs-down means \"keep the copy I have\".",
                nameof(verdict));
        }

        return verdict.Equals(Up, StringComparison.OrdinalIgnoreCase)
            ? DiscoveryStatus.Liked
            : DiscoveryStatus.Disliked;
    }
}

/// <summary>
/// One artist in a user's discovery queue. <paramref name="Score"/> ranks the queue (higher =
/// shown sooner); it accrues each time a frontier artist points here, so candidates several of
/// your tastes agree on float to the top. <paramref name="Sources"/> is the provenance shown in
/// the UI ("via boygenius, Snail Mail") — the frontier artists that recommended this one.
/// <paramref name="Depth"/> is the graph distance from a seed (seeds' neighbours = 1).
/// </summary>
public record DiscoveryCandidate(
    ArtistKey Artist,
    string? ImageUrl,
    double Score,
    IReadOnlyList<string> Sources,
    int Depth);

/// <summary>A page of pending candidates plus the total pending count (for paging controls).</summary>
public record DiscoveryPage(
    IReadOnlyList<DiscoveryCandidate> Items,
    int Page,
    int PageSize,
    long TotalPending);

/// <summary>
/// The category a discovery-feed item belongs to. The feed is split into these so the UI can show
/// each as its own checkbox-toggleable, independently-paged section.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FeedKind
{
    /// <summary>A new artist not in the library, grown from the user's liked artists.</summary>
    RecommendedArtist,

    /// <summary>An album on Deezer for an owned artist that isn't in the library yet.</summary>
    MissingAlbum,

    /// <summary>
    /// An album the library already has, but at a lower quality than the user is entitled to — a
    /// 320kbps copy where they could have lossless.
    ///
    /// <para>Its own kind rather than a flavour of <see cref="MissingAlbum"/> because the two are
    /// different propositions in every way that matters: one grows the library and the other replaces
    /// a record already in it. They are separately toggleable in the feed (someone may want gaps
    /// filled but not care about re-fetching what they own), a thumbs-down means something different
    /// on each (skip *this upgrade*, not dislike an album they own and like), and — carried onto the
    /// purchase row — it is what tells the downloader an existing copy has to be moved aside rather
    /// than merged with.</para>
    /// </summary>
    UpgradeAlbum,

    /// <summary>
    /// An owned library artist the user hasn't thumbed yet (either section, used for the Ratings
    /// classification and the legacy single-kind endpoint).
    /// </summary>
    LibraryArtist,

    /// <summary>
    /// An owned, unrated artist that a <em>liked</em> artist recommends — worth rating because the
    /// frontier already vouches for it. Carries the liked artists that point at it as provenance.
    /// </summary>
    RecommendedLibraryArtist,

    /// <summary>
    /// An owned, unrated artist nothing in the frontier recommends yet — rating it seeds the graph
    /// (a fresh taste anchor to grow recommendations from).
    /// </summary>
    SeedLibraryArtist,

    /// <summary>
    /// An owned artist the user thumbed <em>down</em> even though their own Plex song ratings say
    /// otherwise — a high average across a decent share of the artist's tracks. The thumbs-down was
    /// probably a misfire (a snap judgement, or a rating made before they'd heard much), so the card
    /// offers it back once. Thumbing it down a second time confirms the verdict and it never returns.
    /// </summary>
    ReconsiderArtist,

    /// <summary>
    /// The mirror of <see cref="ReconsiderArtist"/>: an owned artist the user thumbed <em>up</em> whose
    /// own Plex song ratings argue against it — a low average across a decent share of the tracks. The
    /// like is still growing the frontier off a band they apparently don't rate, so the card offers the
    /// thumbs-down. Thumbing it up a second time confirms the like and it never returns.
    /// </summary>
    SecondThoughtsArtist,

    /// <summary>
    /// An owned artist the user marked <see cref="DiscoveryStatus.Indifferent"/> whose own Plex song
    /// ratings say they do in fact like it — a high average across a decent share of the tracks. The
    /// shrug was made before they'd heard much, so the card offers the thumbs-up.
    ///
    /// <para><b>Why two indifferent kinds and not one.</b> The direction a reconsider card argues in is
    /// read off its kind — that is why <see cref="ReconsiderArtist"/> and
    /// <see cref="SecondThoughtsArtist"/> are two kinds sharing one <see cref="ReconsiderSignal"/>
    /// shape. Indifference is the only verdict that can be contradicted <em>either</em> way, and one
    /// kind covering both would make direction sometimes-readable from the kind and sometimes not,
    /// forcing every consumer (badge, blurb, ordering) to grow a second mechanism. There is also no
    /// single correct ordering for a merged list: each side sorts most-contradicted-first, which is
    /// opposite ends of the same scale.</para>
    /// </summary>
    IndifferentLikeArtist,

    /// <summary>
    /// The mirror of <see cref="IndifferentLikeArtist"/>: an artist the user shrugged at whose song
    /// ratings are poor enough to argue for a rejection, so the card offers the thumbs-down. Lower
    /// stakes than <see cref="SecondThoughtsArtist"/> — an indifferent band feeds nothing, so nothing
    /// is being wasted — but settling it is what stops the sweep offering it back.
    /// </summary>
    IndifferentDislikeArtist,
}

/// <summary>What a <see cref="FeedKind"/> means for acquisition.</summary>
public static class FeedKindExtensions
{
    /// <summary>
    /// Whether a row of this kind is something the downloader can fetch. Artists are wishlist-only
    /// (there is no such thing as downloading an artist); both album kinds are fetchable, and an
    /// upgrade differs only in what has to happen to the copy already on disk before the new one
    /// lands. One definition because five separate call sites gate on this, and a kind admitted by
    /// four of them would queue and never drain.
    /// </summary>
    public static bool IsDownloadableAlbum(this FeedKind kind) =>
        kind is FeedKind.MissingAlbum or FeedKind.UpgradeAlbum;
}

/// <summary>
/// One thing to react to in the discovery feed. <paramref name="Album"/> and
/// <paramref name="DeezerAlbumId"/> are set only for <see cref="FeedKind.MissingAlbum"/> (the id lets
/// the UI sample/link the album on Deezer); <paramref name="Score"/>/<paramref name="Sources"/> rank
/// and explain recommended artists (0/empty for the other kinds). <paramref name="Year"/> is the
/// album's release year (album kinds only, null when Deezer supplied no date).
/// <paramref name="Reconsider"/> is the stored rating evidence behind a
/// <see cref="FeedKind.ReconsiderArtist"/> or <see cref="FeedKind.SecondThoughtsArtist"/> card, so the
/// UI can show why the verdict is being questioned. Null for every other kind.
/// </summary>
public record FeedItem(
    FeedKind Kind,
    ArtistKey Artist,
    string? Album,
    string? ImageUrl,
    double Score,
    IReadOnlyList<string> Sources,
    long? DeezerAlbumId,
    int? Year = null,
    ReconsiderSignal? Reconsider = null,
    // For a FeedKind.UpgradeAlbum card: how good the copy already in the library is, so the card can
    // say what it is offering to replace ("You have this as MP3") rather than reading like a gap.
    // Null on every other kind.
    AudioQuality? OwnedQuality = null);

/// <summary>
/// Why the weekly sweep thinks a verdict was wrong — a keeper thumbed down, or a dud thumbed up: a
/// snapshot of the user's Plex song ratings for the artist, taken when it was flagged. Which way it
/// cuts is read off the row's own verdict, so one shape serves both directions. Persisted on the queue
/// row so serving the feed is one Mongo read — no Plex round-trip, nothing to recompute per request.
/// </summary>
public record ReconsiderSignal(double Average, int RatedCount, int TrackCount);

/// <summary>
/// One already-thumbed artist for the sweep to weigh (a like or a dislike, per the status it was
/// fetched by), carrying the flag it currently holds (<paramref name="Reconsider"/> null = not
/// currently flagged) so the sweep only writes when the verdict actually changes.
/// </summary>
public record SweptArtist(ArtistKey Artist, string? ImageUrl, ReconsiderSignal? Reconsider);

/// <summary>
/// A flagged artist ready to serve as a "second chance" / "second thoughts" card — artist, art, and
/// the evidence. Which card it becomes follows from the status it was fetched by.
/// </summary>
public record ReconsiderCandidate(ArtistKey Artist, string? ImageUrl, ReconsiderSignal Signal);

/// <summary>A paged feed section for a single <see cref="FeedKind"/>.</summary>
public record DiscoveryFeedPage(
    FeedKind Kind,
    IReadOnlyList<FeedItem> Items,
    int Page,
    int PageSize,
    long Total);

/// <summary>
/// A rating the user has made, for the Ratings review page. <paramref name="Album"/> is set for
/// album ratings; <paramref name="Kind"/> is the effective category (an owned rated artist is
/// <see cref="FeedKind.LibraryArtist"/>, a non-owned one <see cref="FeedKind.RecommendedArtist"/>).
/// </summary>
public record RatedItem(
    FeedKind Kind,
    ArtistKey Artist,
    string? Album,
    string? ImageUrl,
    DiscoveryStatus Verdict,
    DateTimeOffset? SnoozeUntil = null);

/// <summary>
/// An artist-level rating row (verdict + image) read back from the per-user queue.
/// <paramref name="SnoozeUntil"/> is set only for <see cref="DiscoveryStatus.Snoozed"/> rows.
/// </summary>
public record ArtistRating(ArtistKey Artist, string? ImageUrl, DiscoveryStatus Status, DateTimeOffset? SnoozeUntil = null);

/// <summary>
/// One album in an artist's full discography, for the Artists-page drill-down. <paramref name="Owned"/>
/// marks albums already in the library; missing ones carry <paramref name="DeezerAlbumId"/> so they can
/// be queued to buy. <paramref name="Verdict"/> reflects any rating the user has placed on a missing
/// album (null = not yet decided, or owned). Owned albums the library has that Deezer doesn't list as an
/// LP carry no Deezer id/art — nor a <paramref name="Year"/>, which comes from Deezer's release date.
/// <paramref name="Blocked"/> marks an album blocked for everyone (see <see cref="IAlbumBlockRepo"/>);
/// it's filtered out of the feeds entirely, and surfaced only here so the block can be lifted.
/// <paramref name="RecordType"/> is Deezer's own classification ("album" / "ep" / "single" /
/// "compilation"), shown as a badge on the row. This listing carries every type while the feed takes
/// only LPs and EPs, so the badge is what tells a single apart from an album here; null for an owned
/// album Deezer doesn't list. <paramref name="PlexUrl"/> deep links an owned album into Plex, so the
/// copy we have can be opened from the row that says we have it; null for a missing album, for one
/// whose Plex rating key isn't captured yet, or when Plex couldn't be reached.
/// </summary>
public record ArtistAlbumItem(
    ArtistKey Artist,
    string Album,
    string? ImageUrl,
    long? DeezerAlbumId,
    bool Owned,
    DiscoveryStatus? Verdict,
    int? Year = null,
    bool Blocked = false,
    string? RecordType = null,
    string? PlexUrl = null);
