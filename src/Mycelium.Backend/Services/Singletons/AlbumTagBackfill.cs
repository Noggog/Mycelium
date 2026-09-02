using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// Stamps the verdict moods a <em>collection</em> rating couldn't write at the time onto albums that
/// have since arrived in Plex — the album twin of <see cref="ArtistTagBackfill"/>.
///
/// <para><b>The gap this closes.</b> Liking a collection writes "&lt;user&gt;_liked" onto the album in
/// Plex (see <see cref="PlexAlbumTagger"/>), but the whole point of the collections view is finding
/// records the library <em>doesn't have</em>. At rating time there is no item to tag, so the verdict
/// lives only in Mongo; once the download lands, nothing would go back and stamp it, and a "My Library"
/// playlist would silently omit exactly the compilations the user went and acquired.</para>
///
/// <para><b>Driven by the ratings, not by arrivals.</b> <see cref="ArtistTagBackfill"/> can key off
/// <see cref="CatalogSyncResult.NewlyPresent"/> because artists arrive; a collection usually doesn't
/// make its umbrella act newly present — "Various Artists" was already in the library, it just gained
/// another record. There is no arrival signal at album granularity, so this walks the ratings instead:
/// every user's umbrella-credited verdicts, filtered to the ones the catalog now says are owned. That
/// is a Mongo-only pass, and a collection is a deliberate escape hatch rather than a feed, so the set
/// stays small — the Plex cost is one read per owned rated collection, and only when it needs a
/// change.</para>
///
/// <para><b>Idempotent.</b> The write goes through the same <see cref="IAlbumTagger"/> the live rating
/// path uses, which diffs against the album's current moods and no-ops when the tag is already there.
/// Failures are logged rather than thrown, like every tagging path.</para>
/// </summary>
public class AlbumTagBackfill
{
    private readonly IAlbumTagger _tagger;
    private readonly IUserAlbumRatingRepo _albumRatings;
    private readonly IUserQueueRepo _queue;
    private readonly IUserRepo _users;
    private readonly IArtistCatalogRepo _catalog;
    private readonly IAlbumMatchOverrideRepo _overrides;
    private readonly ILogger<AlbumTagBackfill> _logger;

    public AlbumTagBackfill(
        IAlbumTagger tagger,
        IUserAlbumRatingRepo albumRatings,
        IUserQueueRepo queue,
        IUserRepo users,
        IArtistCatalogRepo catalog,
        IAlbumMatchOverrideRepo overrides,
        ILogger<AlbumTagBackfill> logger)
    {
        _tagger = tagger;
        _albumRatings = albumRatings;
        _queue = queue;
        _users = users;
        _catalog = catalog;
        _overrides = overrides;
        _logger = logger;
    }

    /// <summary>
    /// Re-stamps every user's collection verdicts onto the albums the library now holds. Returns the
    /// number of (user, album) tag writes issued — 0 when nobody has rated a collection that has
    /// landed.
    /// </summary>
    public async Task<int> Backfill()
    {
        var owned = await OwnedAlbumLookup.Load(_catalog, _overrides);
        var applied = 0;

        foreach (var userId in await _queue.GetAllUserIds())
        {
            var user = await _users.Get(userId);

            foreach (var rating in await _albumRatings.GetRated(userId))
            {
                var artist = rating.Artist.ArtistName;
                var album = rating.Album.AlbumName;

                // Only collections: an ordinary album's verdict is carried by its artist.
                // Only actual thumbs: GetRated includes snoozed rows, which are deferred decisions.
                if (!UmbrellaArtist.Is(artist)
                    || rating.Status is not (DiscoveryStatus.Liked or DiscoveryStatus.Disliked)
                    || !owned.Owns(artist, album))
                {
                    continue;
                }

                var tag = ArtistTag.For(user?.Username, rating.Status);
                if (tag is null)
                {
                    continue; // no usable username to prefix the tag with
                }

                // Strip the opposite verdict as the live path does: a rating flipped while the album was
                // still outside the library would otherwise land both tags on arrival.
                //
                // Still "the opposite" rather than ArtistTag.OtherVerdictTags, unlike the artist path:
                // Indifferent is an artist verdict and the album routes reject the token outright (see
                // DiscoveryVerdict.ForAlbum), so no album row can hold one and there is no third tag in
                // the album mood vocabulary to strip.
                var opposite = rating.Status == DiscoveryStatus.Liked
                    ? DiscoveryStatus.Disliked
                    : DiscoveryStatus.Liked;
                var oppositeTag = ArtistTag.For(user?.Username, opposite);
                var remove = oppositeTag != null ? new[] { oppositeTag } : Array.Empty<string>();

                await _tagger.SetTags(artist, album, tag, remove);
                applied++;
            }
        }

        if (applied > 0)
        {
            _logger.LogInformation("Backfilled {Applied} collection verdict tag(s) onto library albums", applied);
        }

        return applied;
    }
}
