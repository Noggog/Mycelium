using Mycelium.Plex.Services.Smart;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// One playlist the app knows how to build. <see cref="Filter"/> is null exactly when
/// <see cref="Unavailable"/> explains why it can't be built for this user right now — currently only
/// "My Library", which has nothing to filter on until the user has thumbed at least one artist.
/// </summary>
/// <param name="Description">
/// A one-line tagline saying what the playlist is <em>for</em>, or null for a row whose
/// <paramref name="Details"/> already say it — a star tier is named "4★+" and then explained by its
/// own bullet, and a tagline there would only repeat the title.
/// </param>
/// <param name="Details">
/// What the rules actually do, one clause per line, in the order they are worth reading. This is the
/// honest half of the row: it is generated from the same options the filter is, so a clause only
/// appears when the rule behind it does (the reject floor moves with the rating scale, and Deep
/// Frontier only claims to exclude rejects when there is a reject tag on the server to exclude by).
/// </param>
/// <param name="Art">
/// The <see cref="PlaylistArt"/> id of this playlist's cover, or null for one that has none. Only the
/// fixed rows carry art: the tiers the picker generates are a family, not a named playlist, and
/// stamping the 4★ cover on every window of it would say they were the same thing.
/// </param>
public record StockPlaylistDefinition(
    string Id,
    string Title,
    string? Description,
    IReadOnlyList<string> Details,
    PlexSmartFilter? Filter,
    string? Unavailable = null,
    string? Art = null);

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
/// <param name="DislikedArtistMoodTagId">
/// The tag id of this user's "&lt;username&gt;_disliked" <em>artist</em> mood — the thumbs-down twin of
/// the liked one — or null when nobody has been thumbed down yet. Unlike the others this is only ever
/// used to <em>exclude</em>: see <see cref="SmartPlaylistCatalog.DeepFrontier"/>.
/// </param>
/// <param name="DislikedAlbumMoodTagId">
/// The same tag in the <em>album</em> vocabulary, for the collections that carry their verdict on the
/// record rather than on an act (<c>PlexAlbumTagger</c>). Looked up separately for the same reason the
/// liked pair is: Plex keys tags per metadata type.
/// </param>
/// <param name="FreshMonths">The play-recency window for the Fresh variants.</param>
/// <param name="HalfStars">Whether this user rates in half stars — see <see cref="SmartPlaylistCatalog.Floor"/>.</param>
/// </summary>
public record StockPlaylistOptions(
    string? LikedArtistMoodTagId = null,
    string? LikedAlbumMoodTagId = null,
    string? RecommendedArtistMoodTagId = null,
    string? DislikedArtistMoodTagId = null,
    string? DislikedAlbumMoodTagId = null,
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
    /// The rating scale assumed for a user who has never said. Whole stars: it is what every Plex
    /// client can set, so it is the safe guess — half stars are opt-in per app (Plexamp and the mobile
    /// apps have them, Plex for the web doesn't) and so are opt-in here too.
    /// </summary>
    public const bool DefaultHalfStars = false;

    /// <summary>The "not played in the last N months" windows the picker offers for Fresh variants.</summary>
    public static readonly int[] FreshWindows = { 1, 3, 6, 12 };

    /// <summary>
    /// The tiers offered as ready-made starters, in rating units, ascending — 3★/4★/5★ on a whole-star
    /// scale, 3★/3.5★/4★ on a half-star one. Someone rating in halves is offered the half step because
    /// it is a threshold only they can set, and a scale with twice the rungs puts "the good stuff"
    /// lower down. Three rows wide either way, which is what lets <see cref="StarterArt"/> follow a
    /// row's rank rather than its star count.
    /// </summary>
    public static int[] StarterTiers(bool halfStars) =>
        halfStars ? new[] { 6, 7, 8 } : new[] { 6, 8, 10 };

    /// <summary>
    /// The window the starter tiers use. One month is the tightest on offer, which is the point: the
    /// starters are for putting music on right now, and anything heard this month isn't that.
    /// </summary>
    public const int StarterFreshMonths = 1;

    /// <summary>The id of a starter tier — the window is in the id, because the picker generates a
    /// same-shaped playlist at whatever window it is set to.</summary>
    public static string StarterTierId(int ratingUnits) =>
        $"{TierId(ratingUnits)}-fresh-{StarterFreshMonths}mo";

    /// <summary>
    /// The covers the starter tiers wear, lowest row first. Positional, because which three tiers are
    /// on offer depends on the rating scale (see <see cref="StarterTiers"/>) — and one image per row,
    /// never shared, since a cover that turns up twice stops identifying either row.
    /// </summary>
    private static readonly string[] StarterArt =
    {
        PlaylistArt.Ridge,
        PlaylistArt.Poolroom,
        PlaylistArt.Prism,
    };

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

    /// <summary>
    /// The second half of an <see cref="StockPlaylistDefinition.Unavailable"/> note: which tag Plex
    /// hasn't got yet. Worth naming rather than leaving at "do something first", because the two ways
    /// to reach this state look identical from the page — the user genuinely hasn't rated anything, or
    /// the <c>MoodTagSeeder</c> found no record to anchor to — and the tag name is the thread an
    /// operator pulls to tell them apart.
    /// </summary>
    private static string NoTag(params string[] verdicts) =>
        "nothing in Plex carries your "
        + string.Join(" or ", verdicts.Select(v => $"\"{v}\""))
        + " tag yet.";

    /// <summary>
    /// The staleness clause both Frontier variants are built on, in the page's words. Deliberately the
    /// shorter truth: the rule lets anything at all back in after two years, but the line a reader
    /// needs is when music they actually rated comes back around.
    /// </summary>
    private const string StaleDetail = "Not heard in 1+ years";

    /// <summary>
    /// The "never play again" clause, naming <em>this</em> user's worst score — 1★ on a whole-star
    /// scale, 0.5★ on a half-star one (see <see cref="Floor"/>). A fixed "1★" here would describe a
    /// rule half the users don't have.
    /// </summary>
    private static string FloorDetail(bool halfStars) =>
        $"Excludes {TierStars(Floor(halfStars))}★ rated songs";

    /// <summary>Every definition on offer, in display order.</summary>
    public static IReadOnlyList<StockPlaylistDefinition> Build(StockPlaylistOptions options)
    {
        var definitions = new List<StockPlaylistDefinition>
        {
            MyLibrary(options.LikedArtistMoodTagId, options.LikedAlbumMoodTagId),
            Frontier(options),
            DeepFrontier(options),
        };

        // The ready-made "put something on" trio. Fixed at one month rather than following the
        // picker's window: a starter that changed meaning depending on a control in another section
        // would be a puzzle, not a shortcut.
        var starters = StarterTiers(options.HalfStars);
        for (var i = 0; i < starters.Length; i++)
        {
            definitions.Add(Stars(
                starters[i], StarterFreshMonths, id: StarterTierId(starters[i]), art: StarterArt[i]));
        }

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
            Description: null,
            Details: new[] { "Mycelium approved artists and their albums" },
            Filter: rules.Count == 0 ? null : Sorted(PlexGroup.Flatten(PlexGroup.Any(rules.ToArray()))),
            Unavailable: rules.Count == 0
                ? $"Approve an artist first — {NoTag("liked")}"
                : null,
            Art: PlaylistArt.MyLibrary);
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
            Description: "New or forgotten music in your wheelhouse",
            Details: new[]
            {
                StaleDetail,
                "Mycelium approved or recommended",
                FloorDetail(options.HalfStars),
            },
            // Flattened because a single surviving tag must be a bare condition, not a one-child
            // bracket Plex's editor would drop on the user's next save.
            Filter: tags.Count == 0
                ? null
                : Sorted(PlexGroup.Flatten(PlexGroup.All(
                    FrontierRules(options.HalfStars).Append(PlexGroup.Any(tags.ToArray())).ToArray()))),
            Unavailable: tags.Count == 0
                ? $"Approve an artist first — {NoTag("liked", "recommended")}"
                : null,
            Art: PlaylistArt.Frontier);
    }

    /// <summary>
    /// The same idea across the whole library rather than the part the user has a claim on: the one to
    /// reach for when the tagged variant has been mined out.
    ///
    /// <para><b>Unapproved, but not rejected.</b> "Approved or not" is the point — an artist the user
    /// has never thumbed is exactly what this playlist is for. An artist they thumbed <em>down</em> is
    /// the opposite: they have already heard it and said no, so resurfacing it isn't a frontier, it's
    /// ignoring them. So the verdict moods the thumbs already write are subtracted here, the same two
    /// vocabularies <see cref="MyLibrary"/> adds: the artist mood for ordinary acts, the album mood for
    /// the collections whose umbrella credit is nobody's act to reject.</para>
    ///
    /// <para>Each is <c>and</c>-ed in as its own "is not" rather than bracketed together, because
    /// excluding either is excluding both — <c>NOT (a OR b)</c> is <c>NOT a AND NOT b</c> — and the
    /// flat form is what Plex's own editor writes. Either alone is enough; only having <em>neither</em>
    /// is a problem.</para>
    ///
    /// <para><b>And with neither, it isn't offered at all.</b> This is the one place where a missing
    /// tag would produce a playlist that <em>lies</em> rather than one that is honestly unavailable:
    /// the other two rows lose their whole point without their tag and say so, but a Deep Frontier
    /// stripped of its exclusion still looks entirely correct while quietly resurfacing music the user
    /// has already rejected. A tag has no id until something in the library carries it, so the rule
    /// simply cannot be written — and shipping the playlist without it would break the promise its own
    /// description makes. <c>MoodTagSeeder</c> is what normally keeps this from ever being reached, so
    /// a row that <em>is</em> blocked is also the visible symptom of a seed that found nothing to
    /// anchor to.</para>
    /// </summary>
    private static StockPlaylistDefinition DeepFrontier(StockPlaylistOptions options)
    {
        var exclusions = new List<PlexFilter>();
        if (options.DislikedArtistMoodTagId is not null)
        {
            exclusions.Add(new PlexCondition("artist.mood", PlexOp.IsNot, options.DislikedArtistMoodTagId));
        }
        if (options.DislikedAlbumMoodTagId is not null)
        {
            exclusions.Add(new PlexCondition("album.mood", PlexOp.IsNot, options.DislikedAlbumMoodTagId));
        }

        return new StockPlaylistDefinition(
            Id: DeepFrontierId,
            Title: "Deep Frontier",
            Description: "New or forgotten music from the entire library",
            // Stated flatly rather than conditionally: a Deep Frontier that exists at all now carries
            // the exclusion, so the bullet is describing an invariant, not a maybe.
            Details: new[]
            {
                StaleDetail,
                FloorDetail(options.HalfStars),
                "Excludes Mycelium rejected artists and their albums",
            },
            Filter: exclusions.Count == 0
                ? null
                : Sorted(PlexGroup.All(
                    FrontierRules(options.HalfStars).Concat(exclusions).ToArray())),
            Unavailable: exclusions.Count == 0
                ? $"Reject an artist first — {NoTag("disliked")}"
                : null,
            Art: PlaylistArt.DeepFrontier);
    }

    /// <summary>
    /// A star-rating tier over the whole library. The Fresh variant additionally drops anything played
    /// within the last <paramref name="freshMonths"/> months, which turns a favourites list into
    /// something you can actually put on without hearing the same twenty songs.
    ///
    /// <para><c>lastViewedAt</c> reads oddly for music but is the right field: it is Plex's generic
    /// name across all media, and the server labels it "Track Last Played" for audio. There is no
    /// <c>lastPlayedAt</c>. Same for <c>viewCount</c>, which Plex labels "Track Plays".</para>
    /// </summary>
    /// <param name="id">
    /// Overrides the derived id, for a tier offered twice — the starter rows pin a one-month window,
    /// which the picker can also be set to, and two definitions can't share an id.
    /// </param>
    /// <param name="art">
    /// The cover for this row, which only the starter rows pass — see
    /// <see cref="StockPlaylistDefinition.Art"/>.
    /// </param>
    private static StockPlaylistDefinition Stars(
        int ratingUnits, int? freshMonths, string? id = null, string? art = null)
    {
        var threshold = new PlexCondition("track.userRating", PlexOp.GreaterThan, Above(ratingUnits));
        var label = TierLabel(ratingUnits);
        var rated = $"Rated {TierStars(ratingUnits)}★ and up";

        if (freshMonths is null)
        {
            return new StockPlaylistDefinition(
                Id: id ?? TierId(ratingUnits),
                Title: label,
                Description: null,
                Details: new[] { rated },
                Filter: Sorted(threshold),
                Art: art);
        }

        return new StockPlaylistDefinition(
            Id: id ?? $"{TierId(ratingUnits)}-fresh",
            Title: $"{label} (Fresh {freshMonths}mo)",
            Description: null,
            Details: new[]
            {
                rated,
                $"Not played in {freshMonths} month{(freshMonths == 1 ? "" : "s")}",
            },
            Filter: Sorted(PlexGroup.All(
                threshold,
                new PlexCondition("track.lastViewedAt", PlexOp.LessThan, $"-{freshMonths}mon"))),
            Art: art);
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
