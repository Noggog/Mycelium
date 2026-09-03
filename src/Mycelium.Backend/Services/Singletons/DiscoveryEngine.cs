using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// The seam the periodic <c>QueueReplenishService</c> drives — a per-user, additive top-up of the
/// recommendation queue. Implemented by <see cref="DiscoveryEngine"/>; extracted so the background
/// service can be unit-tested without constructing the whole engine.
/// </summary>
public interface IQueueReplenisher
{
    /// <summary>Gently grows (and refreshes stale edges for) the user's queue without clearing pending.</summary>
    Task TopUp(string userId);
}

/// <summary>
/// The seam the <c>ArtistFollowUpService</c> worker drives — the deferred half of a decision the user
/// has already been told landed: what a recorded verdict implies for the frontier, and the rebuild an
/// identity correction implies. Implemented by <see cref="DiscoveryEngine"/>; extracted for the same
/// reason as <see cref="IQueueReplenisher"/>, so the background worker is unit-testable on its own.
/// </summary>
public interface IVerdictFollowUp
{
    /// <inheritdoc cref="DiscoveryEngine.ApplyVerdictFollowUp"/>
    Task ApplyVerdictFollowUp(string userId, string artistName, DiscoveryStatus? status, int depth);

    /// <inheritdoc cref="DiscoveryEngine.Rebuild"/>
    Task Rebuild(string userId);
}

/// <summary>
/// The seam <see cref="RecommendedArtistTagger"/> reads through — "which artists that the library
/// already has does this user's frontier point at, and haven't they thumbed yet". Implemented by
/// <see cref="DiscoveryEngine"/>; extracted for the same reason as <see cref="IQueueReplenisher"/>, so
/// the Plex-facing sweep is unit-testable without standing up the whole engine and its five repos.
/// </summary>
public interface IRecommendedLibraryArtists
{
    /// <inheritdoc cref="DiscoveryEngine.RecommendedLibraryArtistNames"/>
    Task<IReadOnlyList<string>> RecommendedLibraryArtistNames(string userId);
}

/// <summary>
/// The discovery loop. Surfaces three kinds of things to react to — new recommended artists, owned
/// artists not yet rated, and albums missing from <em>liked</em> artists — and steers a per-user walk
/// through the similarity graph by the user's verdicts.
///
/// There is no separate "seed" concept: the frontier is simply the user's <em>Liked</em> artists
/// (owned taste anchors and approved recommendations alike). A thumbs-up on an artist grows the
/// frontier from it (and, if it isn't owned, queues it to buy); a thumbs-down prunes. Albums are
/// rated independently — a liked missing album joins the buy list and drops off once acquired.
/// Recommendations never re-add an artist that's owned, already-decided, or the frontier itself, so
/// the frontier only moves outward.
/// </summary>
public class DiscoveryEngine : IQueueReplenisher, IVerdictFollowUp, IRecommendedLibraryArtists
{
    private readonly IUserQueueRepo _queue;
    private readonly IRelatedArtistReader _related;
    private readonly ILibraryProvider _library;
    private readonly IArtistCatalogRepo _catalog;
    private readonly IMissingAlbumRepo _missing;
    private readonly IUserAlbumRatingRepo _albumRatings;
    private readonly IAlbumBlockRepo _blocks;
    private readonly MissingAlbumRefresher _albumRefresher;
    private readonly UserQualityService _qualities;
    // Only for reading back which way a flagged Indifferent row cuts (ArguesForLike). The sweep does
    // the judging; this is the one verdict whose *direction* isn't recoverable from the row's status
    // alone, so serving its two feed sections needs the same threshold the sweep flagged it against.
    private readonly ReconsiderPolicy _reconsider;
    private readonly ILogger<DiscoveryEngine> _logger;

    public DiscoveryEngine(
        IUserQueueRepo queue,
        IRelatedArtistReader related,
        ILibraryProvider library,
        IArtistCatalogRepo catalog,
        IMissingAlbumRepo missing,
        IUserAlbumRatingRepo albumRatings,
        IAlbumBlockRepo blocks,
        MissingAlbumRefresher albumRefresher,
        UserQualityService qualities,
        ReconsiderPolicy reconsider,
        ILogger<DiscoveryEngine> logger)
    {
        _queue = queue;
        _related = related;
        _library = library;
        _catalog = catalog;
        _missing = missing;
        _albumRatings = albumRatings;
        _blocks = blocks;
        _albumRefresher = albumRefresher;
        _qualities = qualities;
        _reconsider = reconsider;
        _logger = logger;
    }

    // ---- Feed ----

    /// <summary>A paged feed of one category alone.</summary>
    public async Task<DiscoveryFeedPage> GetFeed(string userId, FeedKind kind, int page, int pageSize)
    {
        var all = await ItemsForKind(userId, kind);
        var items = all.Skip(page * pageSize).Take(pageSize).ToArray();
        return new DiscoveryFeedPage(kind, items, page, pageSize, all.Count);
    }

    /// <summary>
    /// A single mixed feed across the selected categories. Each category's items are shuffled (a
    /// stable, <paramref name="seed"/>-driven shuffle so paging is consistent) and then round-robin
    /// interleaved, so the user sees a balanced, varied mix — a recommendation, then a missing album,
    /// then an owned artist to rate — rather than 70 of one kind before any of another.
    /// </summary>
    public async Task<DiscoveryFeedPage> GetMixedFeed(
        string userId, IReadOnlyList<FeedKind> kinds, int page, int pageSize, int seed)
    {
        var lists = new List<List<FeedItem>>();
        foreach (var kind in kinds.Distinct())
        {
            var items = await ItemsForKind(userId, kind);
            // Offset the seed per kind so different categories don't shuffle into lockstep.
            Shuffle(items, seed ^ ((int)kind * 2654435761u).GetHashCode());
            lists.Add(items);
        }

        var mixed = RoundRobin(lists);
        var pageItems = mixed.Skip(page * pageSize).Take(pageSize).ToArray();
        // Page.Kind is meaningless for a mix; each item carries its own kind. Use the first requested.
        var headerKind = kinds.Count > 0 ? kinds[0] : FeedKind.RecommendedArtist;
        return new DiscoveryFeedPage(headerKind, pageItems, page, pageSize, mixed.Count);
    }

    /// <summary>
    /// Just the names behind the <see cref="FeedKind.RecommendedLibraryArtist"/> section — owned,
    /// unrated artists at least one <em>liked</em> artist recommends. The same computation the feed
    /// serves, without the paging, art or provenance a card needs: this exists for the Plex marker
    /// sweep, whose only question is which names should carry "&lt;user&gt;_recommended".
    /// </summary>
    public async Task<IReadOnlyList<string>> RecommendedLibraryArtistNames(string userId) =>
        (await ItemsForKind(userId, FeedKind.RecommendedLibraryArtist))
        .Select(i => i.Artist.ArtistName)
        .ToArray();

    /// <summary>
    /// The full (unpaged) list of feed items for one category, with umbrella credits ("Various
    /// Artists", "Original Soundtrack", cast recordings) dropped. Filtering here rather than
    /// per-category catches every way one can reach a card — a stale queue row written before this rule
    /// existed, an owned Plex "Various Artists" bucket, a soundtrack's missing albums, a collection
    /// someone added by hand — so no card ever asks the user to have an opinion about a non-act.
    ///
    /// <para>Collections are excluded here <em>by design</em>, not incidentally: they are found on
    /// purpose in Browse (see <see cref="CollectionService"/>). The feed grows from a taste graph, and
    /// a compilation has no place in one.</para>
    /// </summary>
    private async Task<List<FeedItem>> ItemsForKind(string userId, FeedKind kind)
    {
        var items = await KindItems(userId, kind);
        items.RemoveAll(i => UmbrellaArtist.Is(i.Artist.ArtistName));
        return items;
    }

    private Task<List<FeedItem>> KindItems(string userId, FeedKind kind) => kind switch
    {
        FeedKind.RecommendedArtist => RecommendedItems(userId),
        FeedKind.LibraryArtist => LibraryItems(userId),
        FeedKind.RecommendedLibraryArtist => LibraryItemsBySection(userId, recommended: true),
        FeedKind.SeedLibraryArtist => LibraryItemsBySection(userId, recommended: false),
        FeedKind.ReconsiderArtist => ReconsiderItems(userId),
        FeedKind.SecondThoughtsArtist => SecondThoughtsItems(userId),
        FeedKind.IndifferentLikeArtist => IndifferentItems(userId, arguesForLike: true),
        FeedKind.IndifferentDislikeArtist => IndifferentItems(userId, arguesForLike: false),
        FeedKind.MissingAlbum => MissingAlbumItems(userId),
        FeedKind.UpgradeAlbum => UpgradeAlbumItems(userId),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown feed kind"),
    };

    private async Task<List<FeedItem>> RecommendedItems(string userId)
    {
        await EnsureQueue(userId);
        // Pull the whole pending set (modest in practice); paging/mixing happens above.
        var pageData = await _queue.GetPending(userId, 0, int.MaxValue);
        return pageData.Items
            .Select(c => new FeedItem(FeedKind.RecommendedArtist, c.Artist, null, c.ImageUrl, c.Score, c.Sources, null))
            .ToList();
    }

    private async Task<List<FeedItem>> LibraryItems(string userId)
    {
        // Owned artists the user hasn't thumbed yet — computed as catalog minus already-decided, so
        // there's nothing to precompute or keep in sync.
        var decided = await _queue.GetDecidedArtists(userId);
        return (await _library.GetAllArtistMetadata())
            .Where(a => !decided.Contains(a.ArtistKey.ArtistName))
            .OrderBy(a => a.ArtistKey.ArtistName, StringComparer.OrdinalIgnoreCase)
            .Select(a => new FeedItem(FeedKind.LibraryArtist, a.ArtistKey, null, a.ArtistImageUrl, 0, Array.Empty<string>(), null))
            .ToList();
    }

    /// <summary>
    /// One section of the owned-unrated artists, split by whether a <em>liked</em> artist recommends
    /// them. <paramref name="recommended"/>=true yields artists the frontier already vouches for
    /// (sorted by how many liked artists point at them, with that provenance attached);
    /// =false yields the rest — fresh artists to rate that would seed new recommendations.
    /// </summary>
    private async Task<List<FeedItem>> LibraryItemsBySection(string userId, bool recommended)
    {
        var decided = await _queue.GetDecidedArtists(userId);
        var owned = (await _library.GetAllArtistMetadata())
            .Where(a => !decided.Contains(a.ArtistKey.ArtistName))
            .ToList();
        var byLiked = await OwnedRecommendedByLiked(userId, owned);

        if (recommended)
        {
            return owned
                .Where(a => byLiked.ContainsKey(a.ArtistKey.ArtistName))
                .OrderByDescending(a => byLiked[a.ArtistKey.ArtistName].Count)
                .ThenBy(a => a.ArtistKey.ArtistName, StringComparer.OrdinalIgnoreCase)
                .Select(a => new FeedItem(
                    FeedKind.RecommendedLibraryArtist, a.ArtistKey, null, a.ArtistImageUrl,
                    byLiked[a.ArtistKey.ArtistName].Count, byLiked[a.ArtistKey.ArtistName].ToArray(), null))
                .ToList();
        }

        return owned
            .Where(a => !byLiked.ContainsKey(a.ArtistKey.ArtistName))
            .OrderBy(a => a.ArtistKey.ArtistName, StringComparer.OrdinalIgnoreCase)
            .Select(a => new FeedItem(
                FeedKind.SeedLibraryArtist, a.ArtistKey, null, a.ArtistImageUrl, 0, Array.Empty<string>(), null))
            .ToList();
    }

    /// <summary>
    /// Of the given owned artists, those at least one <em>liked</em> artist recommends — mapped to the
    /// liked artists that point at them (provenance). Computed live from the similarity graph (the
    /// same edges the frontier expansion walks), so there's nothing to precompute or keep in sync.
    /// Reads are readOnly (see the GetRelated call below), so this never fetches — the background
    /// replenisher/warmer keeps the edges fresh out of band.
    /// </summary>
    private async Task<Dictionary<string, List<string>>> OwnedRecommendedByLiked(
        string userId, IReadOnlyList<ArtistMetadata> owned)
    {
        var ownedSet = owned.Select(a => a.ArtistKey.ArtistName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var likedArtist in await _queue.GetLikedArtistNames(userId))
        {
            // Serving the feed: read stored edges only. The background replenisher keeps liked
            // artists' edges fresh, so this never blocks the request on a source fetch.
            var unified = await _related.GetRelated(new ArtistKey(likedArtist), readOnly: true);
            foreach (var rel in unified.Related)
            {
                var name = rel.ArtistKey.ArtistName;
                if (!ownedSet.Contains(name))
                {
                    continue;
                }

                if (!sources.TryGetValue(name, out var srcs))
                {
                    sources[name] = srcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                srcs.Add(likedArtist);
            }
        }

        return sources.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The thumbed-down artists the user's own Plex song ratings argue with — a thumbs-down is a snap
    /// judgement, while the star ratings are what they thought after actually listening. Which artists
    /// those are is decided out of band by the weekly <c>ReconsiderSweepService</c> and stored on the
    /// queue row, so serving this category is one read with no Plex traffic and nothing to recompute.
    /// A second thumbs-down confirms the rejection for good (see
    /// <see cref="IUserQueueRepo.TryConfirmVerdict"/>), which drops the row out of the sweep too.
    ///
    /// Snoozing one of these cards is a plain snooze, not a soft rejection: it stops being Disliked, so
    /// when the window lapses it comes back through the ordinary unrated-owned-artist sections rather
    /// than here. Only a second thumbs-down settles it permanently.
    /// </summary>
    private async Task<List<FeedItem>> ReconsiderItems(string userId) =>
        // Strongest contradiction first — the higher you rated it, the more the thumbs-down looks like
        // the mistake. Ties break by name so the order is stable across loads.
        (await _queue.GetReconsiderable(userId, DiscoveryStatus.Disliked))
        .OrderByDescending(r => r.Signal.Average)
        .ThenBy(r => r.Artist.ArtistName, StringComparer.OrdinalIgnoreCase)
        .Select(r => new FeedItem(
            FeedKind.ReconsiderArtist, r.Artist, null, r.ImageUrl,
            0, Array.Empty<string>(), null, Reconsider: r.Signal))
        .ToList();

    /// <summary>
    /// The mirror of <see cref="ReconsiderItems"/>: liked artists whose own Plex song ratings are poor
    /// enough to argue the thumbs-up was wrong. Same sweep, same stored evidence, opposite direction —
    /// and it matters more than a stale dislike, because a like still feeds the frontier, so every
    /// recommendation grown off a band the user actually rates 2★ is wasted queue.
    ///
    /// Thumbing one down here does what any dislike does (prunes what it seeded); thumbing it up again
    /// confirms the like for good and it never comes back.
    /// </summary>
    private async Task<List<FeedItem>> SecondThoughtsItems(string userId) =>
        // Worst-rated first — the lower the average, the more the thumbs-up looks like the mistake.
        (await _queue.GetReconsiderable(userId, DiscoveryStatus.Liked))
        .OrderBy(r => r.Signal.Average)
        .ThenBy(r => r.Artist.ArtistName, StringComparer.OrdinalIgnoreCase)
        .Select(r => new FeedItem(
            FeedKind.SecondThoughtsArtist, r.Artist, null, r.ImageUrl,
            0, Array.Empty<string>(), null, Reconsider: r.Signal))
        .ToList();

    /// <summary>
    /// One side of the indifferent second-guessing: artists the user shrugged at whose own song ratings
    /// say otherwise, split by which way they say it.
    ///
    /// <para><b>Why the split is a single boolean.</b> The sweep flags an indifferent row when the
    /// ratings clear <em>either</em> threshold, and the row itself records only the average — not which
    /// threshold it cleared. Asking "is it above MinAverage?" partitions the flagged set; asking two
    /// independent questions ("above min?" and "below max?") would not, because a row flagged under one
    /// set of thresholds and read after they were retuned can satisfy neither. Such a row would then
    /// appear in no feed section at all while still sitting flagged in Mongo — invisible to the user and
    /// unclearable from the UI. See <see cref="ReconsiderPolicy.ArguesForLike"/>.</para>
    ///
    /// <para>Each side is ordered most-contradicted-first, which is opposite ends of the same scale:
    /// the higher the average the more it looks like a like, the lower the more like a dislike.</para>
    /// </summary>
    private async Task<List<FeedItem>> IndifferentItems(string userId, bool arguesForLike)
    {
        var flagged = (await _queue.GetReconsiderable(userId, DiscoveryStatus.Indifferent))
            .Where(r => _reconsider.ArguesForLike(r.Signal) == arguesForLike);

        var ordered = arguesForLike
            ? flagged.OrderByDescending(r => r.Signal.Average)
            : flagged.OrderBy(r => r.Signal.Average);

        return ordered
            .ThenBy(r => r.Artist.ArtistName, StringComparer.OrdinalIgnoreCase)
            .Select(r => new FeedItem(
                arguesForLike ? FeedKind.IndifferentLikeArtist : FeedKind.IndifferentDislikeArtist,
                r.Artist, null, r.ImageUrl,
                0, Array.Empty<string>(), null, Reconsider: r.Signal))
            .ToList();
    }

    private Task<List<FeedItem>> MissingAlbumItems(string userId) =>
        AlbumItems(userId, upgrades: false);

    /// <summary>
    /// Albums the library already holds below what this user is entitled to. Same rows as the missing
    /// feed — the sync emits one superset, diffed against the best tier anyone here could ask for —
    /// narrowed to the ones this particular user out-ranks. A user capped at 320 is never shown a 320
    /// album they already have, even though a lossless user would be.
    /// </summary>
    private Task<List<FeedItem>> UpgradeAlbumItems(string userId) =>
        AlbumItems(userId, upgrades: true);

    /// <summary>
    /// The shared body of the two album feeds. They differ only in which half of the missing-album
    /// rows they take and — for upgrades — the per-user quality test; everything else (frontier,
    /// record type, prior verdicts, blocks) applies identically, and splitting the method would let
    /// those rules drift apart.
    /// </summary>
    private async Task<List<FeedItem>> AlbumItems(string userId, bool upgrades)
    {
        // Gap-fill only for artists the user has thumbed up (the frontier) — not every owned artist.
        // A fresh user with no likes sees no missing albums until they like a band.
        var liked = (await _queue.GetLikedArtistNames(userId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (liked.Count == 0)
        {
            return new List<FeedItem>();
        }

        var entitlement = upgrades ? await _qualities.For(userId) : (AudioQuality?)null;
        var decided = await _albumRatings.GetDecidedKeys(userId);
        var blocked = await BlockedKeys();
        var skipped = upgrades ? await UpgradeSkippedKeys() : new HashSet<string>();

        return (await _missing.GetAll())
            .Where(m => m.IsUpgrade == upgrades)
            // For an upgrade, the copy on disk has to actually be worse than what this user can have.
            // The sync diffed against the ceiling, so a row can exist for a tier this user doesn't
            // reach — that row belongs to someone else's feed, not theirs.
            .Where(m => !upgrades || m.OwnedQuality < entitlement)
            .Where(m => liked.Contains(m.Artist.ArtistName))
            // Singles and compilations are synced (so they're queueable from an artist's discography and
            // carry a Deezer id) but never pushed here — the feed would fill with radio edits.
            .Where(m => AlbumRecordType.IsFeedEligible(m.RecordType))
            // Same deal for the second pressing of a record already listed: browsable in the discography,
            // never a card of its own, so the deluxe edition and the remaster aren't two asks.
            .Where(m => !m.AlternatePressing)
            .Where(m => !decided.Contains(AlbumRatingKey.For(m.Artist.ArtistName, m.Album.AlbumName)))
            .Where(m => !IsBlocked(blocked, m))
            // Upgrades carry their own verdicts, kept apart from album ratings: declining to replace a
            // record is not disliking it.
            .Where(m => !skipped.Contains(AlbumOverrideKey.For(m.Artist.ArtistName, m.Album.AlbumName)))
            .Select(m => new FeedItem(
                upgrades ? FeedKind.UpgradeAlbum : FeedKind.MissingAlbum,
                m.Artist, m.Album.AlbumName, m.AlbumArt, 0, Array.Empty<string>(),
                m.DeezerAlbumId, m.Year, OwnedQuality: m.OwnedQuality))
            .ToList();
    }

    /// <summary>
    /// The canonical keys of every globally blocked album. Keyed like a match override (act + the
    /// title in canonical form), so typography differences between the stored block and the diffed
    /// Deezer title can't let a blocked release slip back into a feed.
    /// </summary>
    private async Task<HashSet<string>> BlockedKeys() =>
        (await _blocks.GetAll())
        .Where(b => b.Scope == AlbumBlockScope.Release)
        .Select(b => AlbumOverrideKey.For(b.Artist, b.Album))
        .ToHashSet();

    /// <summary>
    /// Albums whose upgrade has been declined — either by a user ("this copy is fine") or by the
    /// downloader having already established Deezer has nothing better. An expired
    /// <see cref="AlbumBlock.RetryAfter"/> drops out, so a "no lossless available" verdict lapses back
    /// into candidacy rather than foreclosing on a catalogue that can change.
    ///
    /// <para>Kept apart from <see cref="BlockedKeys"/> deliberately: declining to replace a record is
    /// not deciding the library shouldn't carry it, and blurring the two would hide an album the user
    /// owns and likes from every other surface in the app.</para>
    /// </summary>
    private async Task<HashSet<string>> UpgradeSkippedKeys()
    {
        var now = DateTimeOffset.UtcNow;
        return (await _blocks.GetAll())
            .Where(b => b.Scope == AlbumBlockScope.Upgrade && b.AppliesAt(now))
            .Select(b => AlbumOverrideKey.For(b.Artist, b.Album))
            .ToHashSet();
    }

    /// <summary>
    /// Whether a missing album is blocked, checked under both acts it can be filed as — the artist
    /// whose discography surfaced it and the act Deezer credits it to. A collaboration reachable
    /// through either member is blocked once, from whichever side it was blocked.
    /// </summary>
    private static bool IsBlocked(HashSet<string> blocked, MissingAlbum m) =>
        blocked.Contains(AlbumOverrideKey.For(m.Artist.ArtistName, m.Album.AlbumName))
        || blocked.Contains(AlbumOverrideKey.For(m.MatchArtist.ArtistName, m.Album.AlbumName));

    /// <summary>
    /// On-demand: a brand-new (non-owned) liked artist's albums, so the discover→acquire loop can act
    /// on a fresh discovery rather than just wishlisting the artist. Pulls the artist's Deezer
    /// discography, persists it into the global missing-album store (so a liked album carries its
    /// <see cref="MissingAlbum.DeezerAlbumId"/> through reconcile to the downloader — without that row
    /// the album would be un-downloadable), and returns the not-yet-decided, feed-eligible ones as
    /// missing-album feed items to surface inline under the just-rated card. Singles, compilations and
    /// second pressings of a record already listed are persisted but withheld from the cards; they stay
    /// reachable in the artist's discography. Whatever Plex already owns for the artist is diffed out,
    /// so a partly-owned artist only surfaces its gaps.
    /// </summary>
    public async Task<IReadOnlyList<FeedItem>> ArtistAlbums(string userId, string artistName)
    {
        var ownedAlbums = await _catalog.GetOwnedAlbums();
        var rows = await _albumRefresher.RefreshOne(new ArtistKey(artistName), ownedAlbums);
        var decided = await _albumRatings.GetDecidedKeys(userId);
        var blocked = await BlockedKeys();
        return rows
            // Same feed rules as the main missing-album section: LPs and EPs only, so a newly-liked
            // artist offers their records rather than a wall of singles, and one pressing per record.
            .Where(m => AlbumRecordType.IsFeedEligible(m.RecordType))
            .Where(m => !m.AlternatePressing)
            .Where(m => !decided.Contains(AlbumRatingKey.For(m.Artist.ArtistName, m.Album.AlbumName)))
            .Where(m => !IsBlocked(blocked, m))
            .Select(m => new FeedItem(
                FeedKind.MissingAlbum, m.Artist, m.Album.AlbumName, m.AlbumArt, 0, Array.Empty<string>(),
                m.DeezerAlbumId, m.Year))
            .ToList();
    }

    /// <summary>
    /// An owned artist's full Deezer discography for the Artists-page drill-down: every release Deezer
    /// lists — LPs, EPs, singles and compilations, each badged with its type, and every pressing of a
    /// record rather than one row standing in for all of them — flagged with whether the library owns
    /// it, missing ones overlaid with the user's verdict (queued/dismissed/snoozed) so the listing
    /// matches the to-buy list. Singles, compilations and second pressings appear here but never reach
    /// the feed, so a row can be browsable and thumbable without the refresher having pushed it at
    /// anyone. One
    /// Deezer call per expand; owned albums sort first. Pulling the discography also refreshes the
    /// persisted missing-album rows for the artist.
    ///
    /// Globally blocked albums are <em>marked</em> here rather than dropped — this drill-down is the
    /// one place a block can be reviewed and lifted, so hiding them would make it a one-way door.
    /// </summary>
    public async Task<IReadOnlyList<ArtistAlbumItem>> ArtistDiscography(string userId, string artistName)
    {
        var ownedAlbums = await _catalog.GetOwnedAlbums();
        var albums = await _albumRefresher.Discography(new ArtistKey(artistName), ownedAlbums);

        // The user's verdicts on this artist's albums, keyed like the album-rating store, so a missing
        // album already queued/dismissed/snoozed shows that state instead of fresh action buttons.
        var verdicts = (await _albumRatings.GetRated(userId))
            .Where(r => string.Equals(r.Artist.ArtistName, artistName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(r => AlbumRatingKey.For(r.Artist.ArtistName, r.Album.AlbumName), r => r.Status);
        var blocked = await BlockedKeys();

        var artist = new ArtistKey(artistName);
        return albums
            .OrderByDescending(a => a.Owned)
            .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .Select(a =>
            {
                DiscoveryStatus? verdict = !a.Owned
                    && verdicts.TryGetValue(AlbumRatingKey.For(artistName, a.Title), out var v)
                    ? v
                    : null;
                var isBlocked = !a.Owned && blocked.Contains(AlbumOverrideKey.For(artistName, a.Title));
                return new ArtistAlbumItem(
                    artist, a.Title, a.CoverUrl, a.DeezerAlbumId, a.Owned, verdict, a.Year, isBlocked,
                    a.RecordType);
            })
            .ToList();
    }

    /// <summary>In-place Fisher–Yates shuffle with a fixed seed (so the order is stable across pages).</summary>
    private static void Shuffle(List<FeedItem> items, int seed)
    {
        var rng = new Random(seed);
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>Interleaves the lists one element at a time (list0[0], list1[0], …, list0[1], …).</summary>
    private static List<FeedItem> RoundRobin(List<List<FeedItem>> lists)
    {
        var result = new List<FeedItem>(lists.Sum(l => l.Count));
        var max = lists.Count == 0 ? 0 : lists.Max(l => l.Count);
        for (var i = 0; i < max; i++)
        {
            foreach (var list in lists)
            {
                if (i < list.Count)
                {
                    result.Add(list[i]);
                }
            }
        }
        return result;
    }

    // ---- Rating ----

    /// <summary>
    /// Thumbs an artist. A like records the verdict and grows the frontier from it (queuing it to buy
    /// too, if it isn't owned); a dislike records the verdict and prunes the recommendations that
    /// artist alone had seeded, so the queue tracks current taste without a manual rebuild.
    ///
    /// A thumb landing on an artist that <em>already</em> holds that same verdict is the user standing
    /// by something the sweep offered back for a rethink — a re-rejection of a "second chance" card, or
    /// a re-affirmed like on a "second thoughts" one. Either confirms the verdict permanently, so that
    /// artist is never second-guessed again.
    /// </summary>
    public async Task RateArtist(string userId, string artistName, DiscoveryStatus status)
    {
        var depth = await RecordArtistVerdict(userId, artistName, status);
        await ApplyVerdictFollowUp(userId, artistName, status, depth);
    }

    /// <summary>
    /// The fast half of <see cref="RateArtist"/>: persists the verdict (and the "stood by it twice"
    /// confirmation) and nothing else. Returns the depth the frontier should grow from — one hop past
    /// the rated artist's own. Split out so a request can record the decision, answer the user, and
    /// leave <see cref="ApplyVerdictFollowUp"/> to a background worker; the rate-limited source APIs it
    /// would otherwise wait on turned a click into a multi-second stall.
    /// </summary>
    public async Task<int> RecordArtistVerdict(string userId, string artistName, DiscoveryStatus status)
    {
        // Before Rate overwrites the previous verdict, while the row still carries it. Indifferent
        // confirms like the other two: it is the only thing that stops the sweep offering a shrug back,
        // and a shrug is second-guessed from both sides, so without it a band with polarised song
        // ratings would return every week for good.
        if (status is DiscoveryStatus.Disliked or DiscoveryStatus.Liked or DiscoveryStatus.Indifferent
            && await _queue.TryConfirmVerdict(userId, artistName, status))
        {
            // The status itself, not a hand-rolled "up"/"down" — that ternary read every third verdict
            // as a rejection, which is a debugging trap in the log of all places.
            _logger.LogInformation(
                "{User} rated {Artist} {Verdict} a second time — the verdict is now permanent",
                userId, artistName, status);
        }

        var rated = await _queue.Rate(userId, artistName, status, imageUrl: null);
        return (rated?.Depth ?? 0) + 1;
    }

    /// <summary>
    /// The slow half: what a recorded verdict implies for the queue. A like grows the frontier from the
    /// artist; every other decided verdict — a dislike, a shrug, or a cleared one
    /// (<paramref name="status"/> null), all of which take the artist <em>out</em> of the frontier —
    /// drops the pending candidates it alone had seeded (or just its provenance + score share, where
    /// others also recommend them), so the queue tracks current taste without a manual rebuild.
    ///
    /// <para><b>Indifferent prunes, and the interesting case is Liked→Indifferent.</b> The like seeded
    /// pending rows that name this artist in their <c>sources</c>; withdrawing it to a shrug leaves
    /// those rows standing as recommendations grown from taste the user no longer claims. That is
    /// exactly the situation a clear describes, so it gets the same cleanup. For an artist that was
    /// never liked the call is a no-op — only <see cref="ExpandFrom"/> writes a name into
    /// <c>sources</c> — so this costs one indexed find returning nothing.</para>
    ///
    /// <para>Note the chain is exhaustive by construction rather than by luck: an unhandled status here
    /// silently does nothing, which is precisely how a Liked→Indifferent flip would have left orphaned
    /// recommendations behind while looking entirely correct.</para>
    /// </summary>
    public async Task ApplyVerdictFollowUp(
        string userId, string artistName, DiscoveryStatus? status, int depth)
    {
        if (status == DiscoveryStatus.Liked)
        {
            await ExpandFrom(userId, new[] { artistName }, depth);
        }
        else if (status is DiscoveryStatus.Disliked or DiscoveryStatus.Indifferent or null)
        {
            await _queue.PruneBySource(userId, artistName);
        }
    }

    /// <summary>
    /// Snoozes an artist for <paramref name="duration"/> — hides it from the feed and resurfaces it
    /// when the window lapses. Unlike a like, it never grows the frontier (a snooze is "not now", not
    /// "yes"); unlike a dislike, it isn't permanent.
    /// </summary>
    public Task SnoozeArtist(string userId, string artistName, TimeSpan duration) =>
        _queue.Snooze(userId, artistName, DateTimeOffset.UtcNow + duration, imageUrl: null);

    /// <summary>
    /// Snoozes a missing album for <paramref name="duration"/> — hides it from the missing-albums feed
    /// and resurfaces it when the window lapses. The album analogue of <see cref="SnoozeArtist"/>.
    /// </summary>
    public Task SnoozeAlbum(string userId, string artistName, string albumName, string? albumArt, TimeSpan duration) =>
        _albumRatings.Snooze(userId, artistName, albumName, albumArt, DateTimeOffset.UtcNow + duration);

    /// <summary>
    /// Thumbs a missing album: like = queue to buy, <see cref="DiscoveryStatus.Disliked"/> = "meh" —
    /// a purely personal pass. It hides the album from this user's feed for good and touches nothing
    /// anyone else sees; the album stays offerable to every other user. To take a release off
    /// everyone's feeds, see <see cref="BlockAlbum"/>.
    /// </summary>
    public Task RateAlbum(string userId, string artistName, string albumName, string? albumArt, DiscoveryStatus status) =>
        _albumRatings.Rate(userId, artistName, albumName, albumArt, status);

    /// <summary>
    /// A verdict on an <em>upgrade</em> — replacing a copy the library already has with a better one.
    /// A thumbs-up rates the album like any other acquisition (that is what puts it on the to-buy
    /// list); a thumbs-down records a skip instead.
    ///
    /// <para>The thumbs-down deliberately does <b>not</b> go through <see cref="RateAlbum"/>. The user
    /// owns this album and presumably likes it — storing a dislike would put a record they enjoy on
    /// their Ratings page as rejected, and drop it out of the liked set that drives the frontier. The
    /// gesture means "keep the copy we have", which is a fact about the upgrade, not about the
    /// music.</para>
    /// </summary>
    /// <param name="userId">The OIDC subject, which keys the per-user rating store.</param>
    /// <param name="blockedBy">
    /// The same person's <em>username</em>, for the skip's audit field. Two spellings of one identity
    /// because they are read by different audiences: the rating store is matched on and must use the
    /// stable internal key, while the skip's attribution is only ever read by a person or exported,
    /// where an identity-provider id would mean nothing.
    /// </param>
    public async Task RateUpgrade(
        string userId, string? blockedBy, string artistName, string albumName, string? albumArt,
        DiscoveryStatus status)
    {
        if (status == DiscoveryStatus.Liked)
        {
            await _albumRatings.Rate(userId, artistName, albumName, albumArt, status);
            return;
        }

        await SkipUpgrade(blockedBy, artistName, albumName, retryAfter: null);
    }

    /// <summary>
    /// Stops an album being offered for upgrade. <paramref name="retryAfter"/> null is a standing
    /// decision (a user saying the copy they have is fine); a stamp is a snooze, used when Deezer
    /// turned out to have nothing better — a catalogue can gain a lossless master later, so that
    /// verdict lapses rather than foreclosing forever.
    ///
    /// <para>Recorded under every act the album could be filed as, for the same reason a block is:
    /// a collaboration reachable through either member must not come back through the other.</para>
    /// </summary>
    /// <param name="blockedBy">Username of whoever skipped it, for audit. Null when the downloader
    /// itself decided there was nothing better to fetch.</param>
    public async Task SkipUpgrade(
        string? blockedBy, string artistName, string albumName, DateTimeOffset? retryAfter)
    {
        foreach (var act in await BlockActsFor(artistName, albumName))
        {
            await _blocks.Add(new AlbumBlock(
                act, albumName, blockedBy, AlbumBlockScope.Upgrade, retryAfter));
        }

        _logger.LogInformation(
            "Upgrade skipped for \"{Album}\" ({Artist}){Until}",
            albumName, artistName,
            retryAfter is { } at ? $" until {at:u}" : " (standing)");
    }

    /// <summary>
    /// Blocks an album for everyone — the escalation from a personal "meh". It stops being offered in
    /// any user's missing-album feed and in the inline albums of a freshly-liked artist, and survives
    /// the nightly Deezer re-diff (the block is stored separately from the missing set, so the sweep
    /// can't resurrect it).
    ///
    /// Deliberately does not touch anyone's existing verdicts or the shared to-buy list: someone who
    /// already queued this album keeps it (and the row keeps the Deezer id the downloader needs). A
    /// block stops the album being <em>offered</em>; it doesn't retract choices already made.
    ///
    /// Scoped to the record, not the pressing (see <see cref="AlbumOverrideKey"/>): blocking "Both
    /// Sides (Deluxe Edition)" also takes plain "Both Sides" off the feed. Saying no to an album is
    /// saying no to the album — being re-offered the same record next week under a different edition
    /// name is the answer nobody wants.
    /// </summary>
    /// <param name="blockedBy">Username of whoever placed the block, for audit — anyone may lift it.</param>
    public async Task BlockAlbum(string? blockedBy, string artistName, string albumName)
    {
        foreach (var act in await BlockActsFor(artistName, albumName))
        {
            await _blocks.Add(new AlbumBlock(act, albumName, blockedBy));
        }
        _logger.LogInformation(
            "{User} blocked album \"{Album}\" ({Artist}) for everyone",
            blockedBy ?? "(unattributed)", albumName, artistName);
    }

    /// <summary>Lifts a global block, returning the album to everyone's feeds.</summary>
    public async Task UnblockAlbum(string artistName, string albumName)
    {
        foreach (var act in await BlockActsFor(artistName, albumName))
        {
            await _blocks.Remove(act, albumName);
        }
    }

    /// <summary>
    /// The acts a block has to be recorded under to actually stick: the artist whose discography
    /// surfaced the album, plus the act Deezer credits it to when they differ (a collaboration reached
    /// through one member). Mirrors the same problem merges solve — without the second act the album
    /// resurfaces through the other member's discography.
    /// </summary>
    private async Task<string[]> BlockActsFor(string artistName, string albumName)
    {
        var key = AlbumRatingKey.For(artistName, albumName);
        var row = (await _missing.GetAll())
            .FirstOrDefault(m => AlbumRatingKey.For(m.Artist.ArtistName, m.Album.AlbumName) == key);
        var acts = new List<string> { artistName };
        if (row is not null)
        {
            acts.Add(row.MatchArtist.ArtistName);
        }
        return acts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Clears an artist's verdict, returning it to the feed (recommended or library).</summary>
    public async Task ClearArtistRating(string userId, string artistName)
    {
        await ClearArtistVerdict(userId, artistName);
        // Un-liking drops the artist from the frontier, so prune the recommendations it seeded — same
        // as a dislike. A no-op when the cleared verdict wasn't a like (it seeded nothing).
        await ApplyVerdictFollowUp(userId, artistName, status: null, depth: 0);
    }

    /// <summary>
    /// The fast half of <see cref="ClearArtistRating"/> — drops the verdict row and leaves the pruning
    /// to <see cref="ApplyVerdictFollowUp"/>, mirroring the <see cref="RecordArtistVerdict"/> split.
    /// </summary>
    public Task ClearArtistVerdict(string userId, string artistName) =>
        _queue.ClearVerdict(userId, artistName);

    /// <summary>Clears an album's verdict, returning it to the missing-albums feed.</summary>
    public Task ClearAlbumRating(string userId, string artistName, string albumName) =>
        _albumRatings.Clear(userId, artistName, albumName);

    /// <summary>Discards the pending recommendations and rebuilds them from the current liked artists.</summary>
    public async Task Rebuild(string userId)
    {
        await _queue.DeletePending(userId);
        await ExpandFrom(userId, await _queue.GetLikedArtistNames(userId), depth: 1);
    }

    /// <summary>
    /// Rebuilds every user's queue — the dev-panel "secret" operation. Per-user failures are logged and
    /// skipped so one bad user doesn't abort the sweep. Returns the number of users rebuilt.
    /// </summary>
    public async Task<int> RebuildAll()
    {
        var userIds = await _queue.GetAllUserIds();
        var rebuilt = 0;
        foreach (var userId in userIds)
        {
            try
            {
                await Rebuild(userId);
                rebuilt++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rebuild failed for {User}; skipping to the next user", userId);
            }
        }
        return rebuilt;
    }

    /// <summary>
    /// A gentle, additive top-up of the queue for the periodic replenisher — re-expands from the
    /// liked artists <em>without</em> clearing pending (so it never reshuffles a user mid-swipe). The
    /// upsert is idempotent, and the expansion naturally refetches similarity edges that have gone
    /// stale, so one pass both grows the frontier and refreshes the graph.
    /// </summary>
    public async Task TopUp(string userId) =>
        await ExpandFrom(userId, await _queue.GetLikedArtistNames(userId), depth: 1);

    // ---- Review ----

    /// <summary>
    /// Every rating the user has made, for the review page. Album ratings whose album has since been
    /// acquired are dropped — once it exists, it's no longer interesting.
    /// </summary>
    public async Task<RatedItem[]> GetRatings(string userId)
    {
        var owned = (await _library.GetAllArtistMetadata())
            .Select(a => a.ArtistKey.ArtistName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownedAlbums = await _catalog.GetOwnedAlbums();

        var artistItems = (await _queue.GetRated(userId))
            .Select(r => new RatedItem(
                owned.Contains(r.Artist.ArtistName) ? FeedKind.LibraryArtist : FeedKind.RecommendedArtist,
                r.Artist, null, r.ImageUrl, r.Status, r.SnoozeUntil));

        var albumItems = (await _albumRatings.GetRated(userId))
            .Where(r => !AlbumIsOwned(ownedAlbums, r.Artist.ArtistName, r.Album.AlbumName))
            .Select(r => new RatedItem(
                FeedKind.MissingAlbum, r.Artist, r.Album.AlbumName, r.AlbumArt, r.Status, r.SnoozeUntil));

        return artistItems.Concat(albumItems).ToArray();
    }

    private static bool AlbumIsOwned(
        Dictionary<string, Dictionary<string, AudioQuality?>> ownedAlbums, string artist, string album) =>
        ownedAlbums.TryGetValue(artist, out var set) && set.ContainsKey(album);

    // ---- Frontier expansion ----

    /// <summary>Builds the initial recommendation queue from the liked artists, only when empty.</summary>
    private async Task EnsureQueue(string userId)
    {
        if (await _queue.CountPending(userId) > 0)
        {
            return;
        }

        var liked = await _queue.GetLikedArtistNames(userId);
        if (liked.Length == 0)
        {
            return;
        }

        // Cold-start safety net driven by the feed request itself: read stored edges only so the
        // discover request never blocks on ingestion. If the graph has nothing yet, the queue stays
        // empty until the background replenisher fills it — and shows up on the next load.
        await ExpandFrom(userId, liked, depth: 1, readOnly: true);
    }

    /// <summary>
    /// Walks one step out from <paramref name="frontier"/>: pulls each frontier artist's related
    /// artists from the similarity graph (ingesting from the source on a miss, unless
    /// <paramref name="readOnly"/>), aggregates them so a candidate several frontier artists agree on
    /// accrues score and provenance, drops anything owned/already-decided, and upserts the survivors as
    /// pending candidates.
    /// </summary>
    private async Task ExpandFrom(string userId, IReadOnlyList<string> frontier, int depth, bool readOnly = false)
    {
        if (frontier.Count == 0)
        {
            return;
        }

        var library = (await _library.GetAllArtistMetadata())
            .Select(a => a.ArtistKey.ArtistName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var decided = await _queue.GetDecidedArtists(userId);

        var aggregated = new Dictionary<string, Aggregate>(StringComparer.OrdinalIgnoreCase);

        foreach (var frontierArtist in frontier)
        {
            var unified = await _related.GetRelated(new ArtistKey(frontierArtist), readOnly: readOnly);
            foreach (var candidate in unified.Related)
            {
                var name = candidate.ArtistKey.ArtistName;
                if (string.IsNullOrWhiteSpace(name)
                    || UmbrellaArtist.Is(name)
                    || name.Equals(frontierArtist, StringComparison.OrdinalIgnoreCase)
                    || library.Contains(name)
                    || decided.Contains(name))
                {
                    continue;
                }

                if (!aggregated.TryGetValue(name, out var agg))
                {
                    agg = new Aggregate(name, candidate.ImageUrl, depth);
                    aggregated[name] = agg;
                }

                // One point per frontier artist that points here, plus a small bump for candidates
                // multiple sources (Deezer, …) independently recommend.
                agg.Score += 1.0 + 0.25 * candidate.Sources.Count;
                agg.Sources.Add(frontierArtist);
                agg.ImageUrl ??= candidate.ImageUrl;
            }
        }

        if (aggregated.Count == 0)
        {
            _logger.LogInformation(
                "Discovery expansion for {User} from {FrontierCount} artist(s) yielded no new candidates",
                userId, frontier.Count);
            return;
        }

        var candidates = aggregated.Values
            .Select(a => new DiscoveryCandidate(new ArtistKey(a.Name), a.ImageUrl, a.Score, a.Sources.ToArray(), a.Depth))
            .ToArray();

        await _queue.UpsertCandidates(userId, candidates);
        _logger.LogInformation(
            "Discovery expansion for {User} queued/bumped {Count} candidate(s) at depth {Depth}",
            userId, candidates.Length, depth);
    }

    private sealed class Aggregate
    {
        public Aggregate(string name, string? imageUrl, int depth)
        {
            Name = name;
            ImageUrl = imageUrl;
            Depth = depth;
        }

        public string Name { get; }
        public string? ImageUrl { get; set; }
        public int Depth { get; }
        public double Score { get; set; }
        public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
