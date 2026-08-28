using Mycelium.Plex.Services.Smart;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// One playlist the app knows how to build. <see cref="Filter"/> is null exactly when
/// <see cref="Unavailable"/> explains why it can't be built for this user right now — currently only
/// "My Library", which has nothing to filter on until the user has thumbed at least one artist.
/// </summary>
public record StockPlaylistDefinition(
    string Id,
    string Title,
    string Description,
    PlexSmartFilter? Filter,
    string? Unavailable = null);

/// <summary>
/// Everything one survey's definitions depend on.
///
/// <param name="LikedArtistMoodTagId">
/// The Plex tag id of this user's "&lt;username&gt;_liked" <em>artist</em> mood, or null when the tag
/// doesn't exist on the server yet (nobody has been thumbed up). Tag rules store ids, not names, so
/// there is no way to write this rule ahead of the tag existing.
/// </param>
/// <param name="LikedAlbumMoodTagId">
/// The same tag in the <em>album</em> vocabulary. Plex keys tags per metadata type, so the identical
/// name has a different id at type 9 than at type 8, and the two must be looked up separately. Null
/// until the user likes their first collection — a compilation or soundtrack, which carries its
/// verdict on the album because its umbrella credit is not an act anyone has taste about.
/// </param>
/// <param name="RecommendedArtistMoodTagId">
/// The tag id of this user's "&lt;username&gt;_recommended" <em>artist</em> mood — the marker the
/// discovery sweep puts on owned artists their liked artists point at — or null when no artist
/// carries it yet. Artist vocabulary only: the marker is never written to an album.
/// </param>
/// <param name="FreshMonths">The play-recency window for the Fresh variants.</param>
/// <param name="HalfStars">Whether this user rates in half stars — see <see cref="SmartPlaylistCatalog.Floor"/>.</param>
/// </summary>
public record StockPlaylistOptions(
    string? LikedArtistMoodTagId = null,
    string? LikedAlbumMoodTagId = null,
    string? RecommendedArtistMoodTagId = null,
    int FreshMonths = 3,
    bool HalfStars = SmartPlaylistCatalog.DefaultHalfStars);

/// <summary>
/// The stock smart playlists offered on the Playlists page — the whole point of which is that a user
/// gets a working set without learning Plex's filter editor, and can go build their own once they see
/// the shape of it.
///
/// <para><b>Plex's rating scale.</b> <c>track.userRating</c> runs 0–10 in half-star steps, so four stars
/// is 8 and <c>-1</c> means unrated. Plex's only comparison is strictly-greater, so "4 stars and up" is
/// <c>&gt;&gt; 7</c> — see <see cref="Above"/>. Everything in this file names ratings in those raw 0–10
/// <em>units</em> rather than in stars, because half-star tiers have no integer star to be named by.</para>
///
/// <para><b>Half stars.</b> Which values a user actually sets depends on the client they rate from —
/// Plexamp does halves, Plex Web only whole stars — and Plex exposes no way to ask, so the user tells
/// us (<see cref="StockPlaylistOptions.HalfStars"/>). Thresholds don't care: "3★ and up" is
/// <c>&gt;&gt; 5</c> either way. Only two things do — where the reject <see cref="Floor"/> sits, and
/// which tiers <see cref="Tiers"/> offers.</para>
/// </summary>
public static class SmartPlaylistCatalog
{
    public const string MyLibraryId = "my-library";
    public const string FrontierId = "frontier";
    public const string DeepFrontierId = "frontier-deep";

    /// <summary>
    /// The rating scale assumed for a user who has never said. Half stars, because that is what these
    /// playlists have always generated: flipping the default would silently rewrite the rules of every
    /// Frontier already created.
    /// </summary>
    public const bool DefaultHalfStars = true;

    /// <summary>The "not played in the last N months" windows the picker offers for Fresh variants.</summary>
    public static readonly int[] FreshWindows = { 1, 3, 6, 12 };

    /// <summary>
    /// The threshold for "<paramref name="ratingUnits"/> and up", expressed for Plex's strictly-greater
    /// operator: 3★ is a rating of 6, so the rule is "greater than 5".
    /// </summary>
    internal static string Above(int ratingUnits) => (ratingUnits - 1).ToString();

    /// <summary>
    /// The user's "never play again" rating — the lowest one they can express. 0.5★ (1 unit) for
    /// someone rating in half stars, 1★ (2 units) for someone whose client only offers whole ones.
    /// This is the <em>only</em> number the scale changes, and Frontier's bottom two bands are built
    /// from it: at or below the floor is rejected, above it is undecided.
    /// </summary>
    internal static int Floor(bool halfStars) => halfStars ? 1 : 2;

    /// <summary>
    /// The tiers the star-rating picker offers, in rating units, ascending — every half step for a
    /// half-star user, every whole star otherwise. Rated 0★ is not a tier: "0 stars and up" is the
    /// whole library, which is not a playlist anyone wants.
    /// </summary>
    public static int[] Tiers(bool halfStars) =>
        halfStars
            ? new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }
            : new[] { 2, 4, 6, 8, 10 };

    /// <summary>
    /// The definition id for a tier. Whole stars keep the id they have always had ("stars-4"); halves
    /// take an underscore rather than a dot, so the id stays a clean URL path segment.
    /// </summary>
    internal static string TierId(int ratingUnits) =>
        ratingUnits % 2 == 0 ? $"stars-{ratingUnits / 2}" : $"stars-{ratingUnits / 2}_5";

    /// <summary>A tier written as a star count: "4", "3.5".</summary>
    internal static string TierStars(int ratingUnits) =>
        ratingUnits % 2 == 0 ? $"{ratingUnits / 2}" : $"{ratingUnits / 2}.5";

    /// <summary>How a tier is written for a human: "4★+", "3.5★+".</summary>
    internal static string TierLabel(int ratingUnits) => $"{TierStars(ratingUnits)}★+";

    /// <summary>Every definition on offer, in display order.</summary>
    public static IReadOnlyList<StockPlaylistDefinition> Build(StockPlaylistOptions options)
    {
        var definitions = new List<StockPlaylistDefinition>
        {
            MyLibrary(options.LikedArtistMoodTagId, options.LikedAlbumMoodTagId),
            Frontier(options),
            DeepFrontier(options),
        };

        // Every tier the scale allows, not just the one the page happens to be showing: the survey has
        // to be able to answer "do you already have this?" for whichever the slider lands on, and it is
        // pure arithmetic over state that is already loaded.
        foreach (var tier in Tiers(options.HalfStars))
        {
            definitions.Add(Stars(tier, freshMonths: null));
            definitions.Add(Stars(tier, options.FreshMonths));
        }

        return definitions;
    }

    /// <summary>
    /// Everything the user has thumbed up — "their" music, as distinct from whatever else happens to be
    /// on a shared server. Rides on the mood tags the thumbs already write into Plex.
    ///
    /// <para><b>Two rules, not one.</b> A verdict normally lands on the artist, but a collection — a
    /// various-artists compilation, a soundtrack — has no act that could hold it, so its like is
    /// stamped on the album instead (see <c>PlexAlbumTagger</c>). Matching only "Artist Mood" would
    /// leave exactly those records out of the playlist that is supposed to be everything you like, so
    /// the rule is the union of the two, joined by Any.</para>
    ///
    /// <para>Either half may be missing: Plex tag rules store ids, and a tag has no id until something
    /// carries it. With one tag the filter is that single condition (no redundant bracket — Plex's own
    /// editor would flatten it and the playlist would stop matching its definition); with neither there
    /// is nothing to build.</para>
    /// </summary>
    private static StockPlaylistDefinition MyLibrary(string? likedArtistMoodTagId, string? likedAlbumMoodTagId)
    {
        var rules = new List<PlexFilter>();
        if (likedArtistMoodTagId is not null)
        {
            rules.Add(new PlexCondition("artist.mood", PlexOp.Is, likedArtistMoodTagId));
        }
        if (likedAlbumMoodTagId is not null)
        {
            rules.Add(new PlexCondition("album.mood", PlexOp.Is, likedAlbumMoodTagId));
        }

        return new StockPlaylistDefinition(
            Id: MyLibraryId,
            Title: "My Library",
            Description: "Contains everything you've thumbed up in Mycelium",
            Filter: rules.Count == 0 ? null : Sorted(PlexGroup.Flatten(PlexGroup.Any(rules.ToArray()))),
            Unavailable: rules.Count == 0
                ? "Thumb up an artist first."
                : null);
    }

    /// <summary>
    /// The staleness and worth-hearing halves shared by both Frontier variants — "things you either
    /// haven't heard in a long time, or have barely played", weighted so that well-rated music has to
    /// age longer before it comes back around than unrated or poorly-rated music does.
    ///
    /// <para>Reproduced from a hand-built playlist, minus its two library-specific exclusions (moods
    /// tagged "interlude" and "delete"), which are personal housekeeping rather than part of the idea,
    /// and with the one-year lane opened up to take in 3★ itself rather than starting above it.</para>
    ///
    /// <para><b>The floor is the only thing the rating scale moves.</b> The bottom two bands are
    /// written against <see cref="Floor"/> rather than a fixed rating, because "the worst score I can
    /// give" is 0.5★ for one user and 1★ for another — and a track at that score should not be dragged
    /// back out by a playlist whose whole premise is that you might still want to hear it. At half
    /// stars this is exactly the hand-built original; at whole stars the rejected band widens to take
    /// in 1★ and the undecided band above it narrows to match.</para>
    /// </summary>
    private static PlexFilter[] FrontierRules(bool halfStars)
    {
        var floor = Floor(halfStars);
        return new PlexFilter[]
        {
            // Stale enough to be worth resurfacing. Anything rated 3★ or better comes back after a
            // year; anything at all comes back after two.
            PlexGroup.Any(
                PlexGroup.All(
                    new PlexCondition("track.userRating", PlexOp.GreaterThan, Above(6)),
                    new PlexCondition("track.lastViewedAt", PlexOp.LessThan, "-1y")),
                new PlexCondition("track.lastViewedAt", PlexOp.LessThan, "-2y")),
            // ...and worth hearing: never rated, or rated in a band that says "undecided" rather than
            // "rejected", or rated highly enough that age is the only reason it fell off.
            PlexGroup.Any(
                new PlexCondition("track.userRating", PlexOp.Is, "-1"),
                // Above the floor but still under 2★: not rejected, just unconvincing — so it gets a
                // few plays to make its case and then stops coming back.
                PlexGroup.All(
                    new PlexCondition("track.userRating", PlexOp.GreaterThan, floor.ToString()),
                    new PlexCondition("track.viewCount", PlexOp.LessThan, "5"),
                    new PlexCondition("track.userRating", PlexOp.LessThan, "4")),
                new PlexCondition("track.userRating", PlexOp.GreaterThan, "3"),
                // At or below the floor — rejected — but never actually played, so the verdict was
                // passed on something nobody has heard. One chance, then it's gone for good.
                PlexGroup.All(
                    new PlexCondition("track.userRating", PlexOp.GreaterThan, "-1"),
                    new PlexCondition("track.viewCount", PlexOp.LessThan, "1"),
                    new PlexCondition("track.userRating", PlexOp.LessThan, (floor + 1).ToString()))),
        };
    }

    /// <summary>
    /// The "find something you'd forgotten" playlist, kept inside music this user has some claim on:
    /// the shared <see cref="FrontierRules"/> plus a third rule narrowing the whole thing to acts they
    /// thumbed up, or that the discovery sweep marked as recommended to them.
    ///
    /// <para><b>Why "recommended" belongs here.</b> The marker sits on owned artists the user hasn't
    /// rated that their liked artists point at — precisely the unheard-but-vouched-for half of the
    /// library. Without it this playlist could only ever resurface music the user already knows, which
    /// is the opposite of a frontier.</para>
    ///
    /// <para>The liked <em>album</em> mood joins the same Any group for the reason it does in
    /// <see cref="MyLibrary"/>: a compilation or soundtrack carries its like on the album because its
    /// umbrella credit is not an act anyone has taste about, and leaving it out would silently exclude
    /// exactly those records. Each of the three is optional — a tag has no id until something carries
    /// it — and with none of them there is nothing to narrow by, which is what
    /// <see cref="DeepFrontier"/> is for.</para>
    /// </summary>
    private static StockPlaylistDefinition Frontier(StockPlaylistOptions options)
    {
        var tags = new List<PlexFilter>();
        if (options.LikedArtistMoodTagId is not null)
        {
            tags.Add(new PlexCondition("artist.mood", PlexOp.Is, options.LikedArtistMoodTagId));
        }
        if (options.RecommendedArtistMoodTagId is not null)
        {
            tags.Add(new PlexCondition("artist.mood", PlexOp.Is, options.RecommendedArtistMoodTagId));
        }
        if (options.LikedAlbumMoodTagId is not null)
        {
            tags.Add(new PlexCondition("album.mood", PlexOp.Is, options.LikedAlbumMoodTagId));
        }

        return new StockPlaylistDefinition(
            Id: FrontierId,
            Title: "Frontier",
            Description: "New or forgotten music, from bands you like or that are recommended to you.",
            // Flattened because a single surviving tag must be a bare condition, not a one-child
            // bracket Plex's editor would drop on the user's next save.
            Filter: tags.Count == 0
                ? null
                : Sorted(PlexGroup.Flatten(PlexGroup.All(
                    FrontierRules(options.HalfStars).Append(PlexGroup.Any(tags.ToArray())).ToArray()))),
            Unavailable: tags.Count == 0
                ? "Thumb up an artist first."
                : null);
    }

    /// <summary>
    /// The same idea with no tag filter at all: the whole library, however you feel about it. This is
    /// the original hand-built Frontier verbatim, and the one to reach for when the tagged variant has
    /// been mined out.
    /// </summary>
    private static StockPlaylistDefinition DeepFrontier(StockPlaylistOptions options) => new(
        Id: DeepFrontierId,
        Title: "Deep Frontier",
        Description: "New or forgotten music from anywhere in the library, liked or not.",
        Filter: Sorted(PlexGroup.All(FrontierRules(options.HalfStars))));

    /// <summary>
    /// A star-rating tier over the whole library. The Fresh variant additionally drops anything played
    /// within the last <paramref name="freshMonths"/> months, which turns a favourites list into
    /// something you can actually put on without hearing the same twenty songs.
    ///
    /// <para><c>lastViewedAt</c> reads oddly for music but is the right field: it is Plex's generic
    /// name across all media, and the server labels it "Track Last Played" for audio. There is no
    /// <c>lastPlayedAt</c>. Same for <c>viewCount</c>, which Plex labels "Track Plays".</para>
    /// </summary>
    private static StockPlaylistDefinition Stars(int ratingUnits, int? freshMonths)
    {
        var threshold = new PlexCondition("track.userRating", PlexOp.GreaterThan, Above(ratingUnits));
        var label = TierLabel(ratingUnits);
        var stars = TierStars(ratingUnits);

        if (freshMonths is null)
        {
            return new StockPlaylistDefinition(
                Id: TierId(ratingUnits),
                Title: label,
                Description: $"Rated {stars} stars and up.",
                Filter: Sorted(threshold));
        }

        return new StockPlaylistDefinition(
            Id: $"{TierId(ratingUnits)}-fresh",
            Title: $"{label} (Fresh {freshMonths}mo)",
            Description: $"Rated {stars} stars and up, not played in "
                         + $"{freshMonths} month{(freshMonths == 1 ? "" : "s")}.",
            Filter: Sorted(PlexGroup.All(
                threshold,
                new PlexCondition("track.lastViewedAt", PlexOp.LessThan, $"-{freshMonths}mon"))));
    }

    /// <summary>
    /// Wraps rules as an artist-scoped query sorted by title — the shape Plex's own filter editor writes,
    /// so a generated playlist is indistinguishable from a hand-made one. Under <c>type=8</c>,
    /// <c>titleSort</c> orders by artist, which keeps an album's tracks together.
    /// </summary>
    private static PlexSmartFilter Sorted(PlexFilter rules) => new(
        PlexSmartFilter.ArtistType,
        rules,
        new[] { new KeyValuePair<string, string>("sort", "titleSort") });
}
