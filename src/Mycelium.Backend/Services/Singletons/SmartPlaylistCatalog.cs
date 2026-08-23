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
/// The stock smart playlists offered on the Playlists page — the whole point of which is that a user
/// gets a working set without learning Plex's filter editor, and can go build their own once they see
/// the shape of it.
///
/// <para><b>Plex's rating scale.</b> <c>track.userRating</c> runs 0–10 in half-star steps, so four stars
/// is 8 and <c>-1</c> means unrated. Plex's only comparison is strictly-greater, so "4 stars and up" is
/// <c>&gt;&gt; 7</c> — see <see cref="RatingAtLeast"/>.</para>
/// </summary>
public static class SmartPlaylistCatalog
{
    public const string MyLibraryId = "my-library";
    public const string FrontierId = "frontier";

    /// <summary>The star tiers offered by the picker.</summary>
    public static readonly int[] StarTiers = { 3, 4, 5 };

    /// <summary>The "not played in the last N months" windows the picker offers for Fresh variants.</summary>
    public static readonly int[] FreshWindows = { 1, 3, 6, 12 };

    /// <summary>
    /// The threshold for "<paramref name="stars"/> stars and up", expressed for Plex's strictly-greater
    /// operator: 3★ is a rating of 6, so the rule is "greater than 5".
    /// </summary>
    internal static string RatingAtLeast(int stars) => ((stars * 2) - 1).ToString();

    /// <summary>
    /// Every definition on offer, in display order.
    ///
    /// <param name="likedMoodTagId">
    /// The Plex tag id of this user's "&lt;username&gt;_liked" artist mood, or null when the tag doesn't
    /// exist on the server yet (nobody has been thumbed up). Tag rules store ids, not names, so there is
    /// no way to write this rule ahead of the tag existing.
    /// </param>
    /// <param name="freshMonths">The play-recency window for the Fresh variants.</param>
    /// </summary>
    public static IReadOnlyList<StockPlaylistDefinition> Build(string? likedMoodTagId, int freshMonths)
    {
        var definitions = new List<StockPlaylistDefinition>
        {
            MyLibrary(likedMoodTagId),
            Frontier(),
        };

        foreach (var stars in StarTiers)
        {
            definitions.Add(Stars(stars, freshMonths: null));
            definitions.Add(Stars(stars, freshMonths));
        }

        return definitions;
    }

    /// <summary>
    /// Everything by an artist the user has thumbed up — "their" music, as distinct from whatever else
    /// happens to be on a shared server. Rides on the mood tag the thumbs already write into Plex.
    /// </summary>
    private static StockPlaylistDefinition MyLibrary(string? likedMoodTagId) => new(
        Id: MyLibraryId,
        Title: "My Library",
        Description: "Contains all artists you've thumbed up: your library",
        Filter: likedMoodTagId is null
            ? null
            : Sorted(new PlexCondition("artist.mood", PlexOp.Is, likedMoodTagId)),
        Unavailable: likedMoodTagId is null
            ? "Thumb up an artist first."
            : null);

    /// <summary>
    /// The "find something you'd forgotten" playlist: things you either haven't heard in a long time, or
    /// have barely played, weighted so that well-rated music has to age longer before it comes back
    /// around than unrated or poorly-rated music does.
    ///
    /// <para>Reproduced from a hand-built playlist, minus its two library-specific exclusions (moods
    /// tagged "interlude" and "delete"), which are personal housekeeping rather than part of the idea.</para>
    /// </summary>
    private static StockPlaylistDefinition Frontier() => new(
        Id: FrontierId,
        Title: "Frontier",
        Description: "For when you want to experience new or forgotten music.",
        Filter: Sorted(PlexGroup.All(
            // Stale enough to be worth resurfacing. Rated tracks come back after a year; anything at all
            // comes back after two.
            PlexGroup.Any(
                PlexGroup.All(
                    new PlexCondition("track.userRating", PlexOp.GreaterThan, "6"),
                    new PlexCondition("track.lastViewedAt", PlexOp.LessThan, "-1y")),
                new PlexCondition("track.lastViewedAt", PlexOp.LessThan, "-2y")),
            // ...and worth hearing: never rated, or rated in a band that says "undecided" rather than
            // "rejected", or rated highly enough that age is the only reason it fell off.
            PlexGroup.Any(
                new PlexCondition("track.userRating", PlexOp.Is, "-1"),
                PlexGroup.All(
                    new PlexCondition("track.userRating", PlexOp.GreaterThan, "1"),
                    new PlexCondition("track.viewCount", PlexOp.LessThan, "5"),
                    new PlexCondition("track.userRating", PlexOp.LessThan, "4")),
                new PlexCondition("track.userRating", PlexOp.GreaterThan, "3"),
                PlexGroup.All(
                    new PlexCondition("track.userRating", PlexOp.GreaterThan, "-1"),
                    new PlexCondition("track.viewCount", PlexOp.LessThan, "1"),
                    new PlexCondition("track.userRating", PlexOp.LessThan, "2"))))));

    /// <summary>
    /// A star-rating tier over the whole library. The Fresh variant additionally drops anything played
    /// within the last <paramref name="freshMonths"/> months, which turns a favourites list into
    /// something you can actually put on without hearing the same twenty songs.
    ///
    /// <para><c>lastViewedAt</c> reads oddly for music but is the right field: it is Plex's generic
    /// name across all media, and the server labels it "Track Last Played" for audio. There is no
    /// <c>lastPlayedAt</c>. Same for <c>viewCount</c>, which Plex labels "Track Plays".</para>
    /// </summary>
    private static StockPlaylistDefinition Stars(int stars, int? freshMonths)
    {
        var threshold = new PlexCondition("track.userRating", PlexOp.GreaterThan, RatingAtLeast(stars));

        if (freshMonths is null)
        {
            return new StockPlaylistDefinition(
                Id: $"stars-{stars}",
                Title: $"{stars}★+",
                Description: $"Rated {stars} stars and up.",
                Filter: Sorted(threshold));
        }

        return new StockPlaylistDefinition(
            Id: $"stars-{stars}-fresh",
            Title: $"{stars}★+ (Fresh {freshMonths}mo)",
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
