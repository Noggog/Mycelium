namespace Mycelium.Interfaces;

/// <summary>A queue row whose artist name encodes multiple artists joined by ';' — cleanup input.</summary>
public record CombinedArtistVerdict(string UserId, string Artist, DiscoveryStatus Status, string? ImageUrl);

/// <summary>
/// Per-user discovery queue: the precomputed swipe candidates the tree-search engine grows from a
/// user's seeds. One document per (user, artist); a candidate is Pending until swiped, then Liked
/// (kept as the "to buy" wishlist) or Disliked (pruned). Distinct from the global similarity graph
/// (<see cref="IRelatedArtistRepo"/>) — this is the user's personal walk through it.
/// </summary>
public interface IUserQueueRepo
{
    /// <summary>
    /// Adds new pending candidates or, for ones already pending, bumps their score and merges in
    /// the new provenance sources — atomically. Candidates already Liked/Disliked must be excluded
    /// by the caller; this never resurrects a decided artist.
    /// </summary>
    Task UpsertCandidates(string userId, IReadOnlyList<DiscoveryCandidate> candidates);

    /// <summary>
    /// The artist names this user has already decided — the expansion exclusion set. Liked/Disliked
    /// always count; a Snoozed artist counts only while unexpired (an expired snooze drops out so the
    /// frontier may re-touch it).
    /// </summary>
    Task<HashSet<string>> GetDecidedArtists(string userId);

    /// <summary>The artist names this user has Liked — the taste anchors the frontier grows from.</summary>
    Task<string[]> GetLikedArtistNames(string userId);

    /// <summary>A score-ranked page of this user's pending candidates, plus the total pending count.</summary>
    Task<DiscoveryPage> GetPending(string userId, int page, int pageSize);

    /// <summary>Count of pending candidates — used to decide whether the queue needs an initial build.</summary>
    Task<long> CountPending(string userId);

    /// <summary>
    /// Records a verdict (Liked/Disliked) on an artist, upserting the row if it doesn't exist yet
    /// (so an owned artist with no prior candidate row can be rated directly). Returns the affected
    /// row — the engine reads its depth when growing the frontier. Only sets the image when one is
    /// supplied, never clobbering an existing one with null. Any sweep flag on the row is dropped: the
    /// evidence was weighed against the <em>old</em> verdict, and the next sweep re-decides.
    /// </summary>
    Task<DiscoveryCandidate?> Rate(string userId, string artistName, DiscoveryStatus status, string? imageUrl);

    /// <summary>
    /// Hides an artist until <paramref name="until"/>, upserting the row if needed (mirrors
    /// <see cref="Rate"/>). The row stays <see cref="DiscoveryStatus.Snoozed"/> until re-rated;
    /// expiry is transparent — <see cref="GetPending"/> resurfaces it once <paramref name="until"/>
    /// has passed. Only sets the image when supplied, never clobbering an existing one with null.
    /// Drops any sweep flag with the old verdict, same as <see cref="Rate"/>.
    /// </summary>
    Task Snooze(string userId, string artistName, DateTimeOffset until, string? imageUrl);

    /// <summary>Removes an artist's verdict, returning it to the feed (recommended or library).</summary>
    Task ClearVerdict(string userId, string artistName);

    /// <summary>The user's Liked candidates — the artist side of the "to buy" wishlist, newest first.</summary>
    Task<DiscoveryCandidate[]> GetLiked(string userId);

    /// <summary>
    /// Every user's Liked candidates — the artist side of the unified "to buy" list the library
    /// maintainer acts on. Not scoped to a user; the caller dedups across users.
    /// </summary>
    Task<DiscoveryCandidate[]> GetAllLiked();

    /// <summary>Every Liked/Disliked artist rating (verdict + image), for the Ratings review page.</summary>
    Task<ArtistRating[]> GetRated(string userId);

    /// <summary>
    /// The artists this user gave <paramref name="status"/> (Liked or Disliked) whose verdict hasn't
    /// been confirmed by a repeat of the same thumb, each with the reconsider flag it currently
    /// carries — the working set for the periodic sweep. Once <see cref="TryConfirmVerdict"/> has stuck
    /// on a row it drops out of here permanently, so a re-affirmed verdict is never weighed (or
    /// questioned) again.
    /// </summary>
    Task<SweptArtist[]> GetUnconfirmedVerdicts(string userId, DiscoveryStatus status);

    /// <summary>
    /// Records (or, with a null <paramref name="signal"/>, clears) the sweep's verdict that the user's
    /// song ratings contradict how they thumbed an artist. Only touches rows still sitting at
    /// <paramref name="status"/> — the verdict the sweep weighed — so one the user changed mid-sweep
    /// can't be flagged from stale evidence. Fills <paramref name="imageUrl"/> when supplied — an
    /// artist rated straight from the library has no art on its row, and stamping it here keeps serving
    /// the feed to a single query.
    /// </summary>
    Task SetReconsider(
        string userId, string artistName, DiscoveryStatus status, ReconsiderSignal? signal, string? imageUrl);

    /// <summary>
    /// The flagged artists to serve as second-guessing cards for <paramref name="status"/>: thumbed
    /// that way, not re-affirmed, and carrying a sweep verdict. Disliked yields the "second chance"
    /// cards, Liked the "second thoughts" ones. One indexed read — all the judgement already happened
    /// in the sweep.
    /// </summary>
    Task<ReconsiderCandidate[]> GetReconsiderable(string userId, DiscoveryStatus status);

    /// <summary>
    /// Marks a thumb as final, but <em>only</em> when the artist already sat at
    /// <paramref name="status"/> — i.e. this is the user giving the same verdict a second time, after
    /// the sweep offered it back for a rethink. Returns true when the flag was set (the verdict is now
    /// remembered forever), false for a first-time verdict, which stays eligible to be questioned. Must
    /// be called <em>before</em> <see cref="Rate"/> records the new verdict, while the row still holds
    /// the previous one. Clearing the verdict (<see cref="ClearVerdict"/>) drops the flag with the row
    /// — a full reset means a clean slate. Only Liked/Disliked are meaningful.
    /// </summary>
    Task<bool> TryConfirmVerdict(string userId, string artistName, DiscoveryStatus status);

    /// <summary>
    /// Drops the "this verdict is final" flag from this user's rows — all three kinds when
    /// <paramref name="status"/> is null, otherwise just that verdict's — returning how many rows
    /// actually carried one. The rows themselves are untouched: the verdict stays, it simply becomes
    /// eligible for the sweep to question again.
    ///
    /// <para>This exists because a confirmation is otherwise a one-way door. It is set silently, has no
    /// UI that shows it, and permanently removes the artist from
    /// <see cref="GetUnconfirmedVerdicts"/> — so a caller that confirmed a verdict it shouldn't have
    /// had no way back short of clearing the rating outright, which also throws away the verdict, the
    /// Plex mood tag and the frontier expansion behind it.</para>
    /// </summary>
    Task<long> ClearConfirmations(string userId, DiscoveryStatus? status);

    /// <summary>Clears pending candidates (keeps Liked/Disliked) so the queue can be rebuilt from likes.</summary>
    Task DeletePending(string userId);

    /// <summary>
    /// Removes <paramref name="sourceArtist"/> from the provenance of this user's <em>pending</em>
    /// candidates: a candidate it solely recommended is deleted; one other liked artists also
    /// recommend keeps its row but drops this source and has its score decayed proportionally. Called
    /// when an artist leaves the frontier (disliked or un-liked) so the queue stops surfacing the
    /// recommendations only that artist seeded — no manual rebuild needed. A no-op for an artist that
    /// never seeded anything (it appears in no <c>sources</c> array).
    /// </summary>
    Task PruneBySource(string userId, string sourceArtist);

    /// <summary>
    /// Every user id that has at least one queue row — the population the periodic replenisher tops
    /// up. Sourced here (not from a user repo) so it covers exactly the users who've engaged discovery.
    /// </summary>
    Task<string[]> GetAllUserIds();

    /// <summary>
    /// Every row across all users whose artist name encodes multiple artists joined by ';' — the
    /// artist side of the combined-name cleanup. Includes Pending rows (cleanup just drops those).
    /// </summary>
    Task<CombinedArtistVerdict[]> FindCombinedRatings();
}
