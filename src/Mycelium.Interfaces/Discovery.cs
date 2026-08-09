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
    ReconsiderSignal? Reconsider = null);

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
/// </summary>
public record ArtistAlbumItem(
    ArtistKey Artist,
    string Album,
    string? ImageUrl,
    long? DeezerAlbumId,
    bool Owned,
    DiscoveryStatus? Verdict,
    int? Year = null,
    bool Blocked = false);
