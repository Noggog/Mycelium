using FluentAssertions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Plex.Services.Smart;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The stock definitions. These assert the exact query Plex will be sent, because a rule that is merely
/// plausible is indistinguishable from a correct one until someone plays the playlist — the star
/// thresholds in particular are off-by-one bait (Plex only compares strictly-greater, on a 0–10 scale).
/// </summary>
public class SmartPlaylistCatalogTests
{
    private const string LikedArtist = "749936";
    private const string LikedAlbum = "812004";
    private const string RecommendedArtist = "901122";
    private const string DislikedArtist = "700001";
    private const string DislikedAlbum = "700002";

    private static PlexSmartFilter Filter(
        string id,
        string? likedTagId = LikedArtist,
        string? recommendedTagId = null,
        string? dislikedArtistTagId = DislikedArtist,
        string? dislikedAlbumTagId = null,
        int freshMonths = 3,
        bool halfStars = true) =>
        SmartPlaylistCatalog
            .Build(new StockPlaylistOptions(
                likedTagId, null, recommendedTagId, dislikedArtistTagId, dislikedAlbumTagId,
                freshMonths, halfStars))
            .Single(d => d.Id == id).Filter!;

    [Theory]
    // 3 stars is a rating of 6, and Plex has no ">=", so the rule is "greater than 5".
    [InlineData(3, "5")]
    [InlineData(4, "7")]
    [InlineData(5, "9")]
    public void Star_tiers_land_on_the_half_step_below_the_tier(int stars, string expected)
    {
        SmartPlaylistCatalog.Above(stars * 2).Should().Be(expected);

        Filter($"stars-{stars}").Rules
            .Should().Be(new PlexCondition("track.userRating", PlexOp.GreaterThan, expected));
    }

    /// <summary>
    /// The half tiers sit on the odd rating values between the whole ones — 3.5★ is a rating of 7, so
    /// its rule is "greater than 6". Half-step ids take an underscore so they stay clean URL segments.
    /// </summary>
    [Theory]
    [InlineData(1, "stars-0_5", "0.5★+", "0")]
    [InlineData(7, "stars-3_5", "3.5★+", "6")]
    [InlineData(9, "stars-4_5", "4.5★+", "8")]
    public void Half_tiers_sit_between_the_whole_ones(
        int ratingUnits, string id, string title, string threshold)
    {
        SmartPlaylistCatalog.TierId(ratingUnits).Should().Be(id);
        SmartPlaylistCatalog.TierLabel(ratingUnits).Should().Be(title);

        Filter(id).Rules
            .Should().Be(new PlexCondition("track.userRating", PlexOp.GreaterThan, threshold));
    }

    /// <summary>
    /// The scale decides which tiers exist, not what they mean: a whole-star user is offered five,
    /// a half-star user ten, and the tiers they share are the identical definition — "4★ and up" is
    /// <c>&gt;&gt; 7</c> however the user rates, because Plex compares on the 0–10 scale either way.
    /// </summary>
    [Fact]
    public void Whole_star_users_are_offered_only_the_whole_tiers()
    {
        static string[] TierIds(bool halfStars) => SmartPlaylistCatalog
            .Build(new StockPlaylistOptions(HalfStars: halfStars))
            .Select(d => d.Id)
            .Where(id => id.StartsWith("stars-") && !id.Contains("-fresh"))
            .ToArray();

        TierIds(halfStars: false).Should().Equal("stars-1", "stars-2", "stars-3", "stars-4", "stars-5");
        TierIds(halfStars: true).Should().HaveCount(10).And.Contain("stars-3_5");

        PlexFilterSerializer.Serialize(Filter("stars-4", halfStars: false))
            .Should().Be(PlexFilterSerializer.Serialize(Filter("stars-4", halfStars: true)));
    }

    [Fact]
    public void Fresh_variants_add_the_play_recency_window()
    {
        PlexFilterSerializer.Serialize(Filter("stars-4-fresh", freshMonths: 6)).Should().Be(
            "type=8&sort=titleSort"
            + "&track.userRating%3E%3E=7"
            + "&and=1&track.lastViewedAt%3C%3C=-6mon");
    }

    [Fact]
    public void The_fresh_window_is_reflected_in_the_title_so_two_windows_dont_collide()
    {
        string Title(int months) => SmartPlaylistCatalog
            .Build(new StockPlaylistOptions(LikedArtist, FreshMonths: months))
            .Single(d => d.Id == "stars-4-fresh").Title;

        Title(3).Should().Be("4★+ (Fresh 3mo)");
        Title(6).Should().Be("4★+ (Fresh 6mo)");
    }

    [Fact]
    public void My_library_filters_on_the_users_liked_mood_tag_id()
    {
        PlexFilterSerializer.Serialize(Filter(SmartPlaylistCatalog.MyLibraryId))
            .Should().Be("type=8&sort=titleSort&artist.mood=749936");
    }

    /// <summary>
    /// A collection — a compilation or soundtrack — carries its like on the album, because its umbrella
    /// credit is not an act anyone has taste about. Matching only "Artist Mood" would leave exactly
    /// those records out of the playlist that is meant to be everything you like, so the rule is the
    /// union of the two. Plex keys tags per metadata type, hence two different ids for one tag name.
    /// </summary>
    [Fact]
    public void My_library_also_matches_the_album_mood_a_collection_carries()
    {
        var filter = SmartPlaylistCatalog.Build(new StockPlaylistOptions(LikedArtist, LikedAlbum))
            .Single(d => d.Id == SmartPlaylistCatalog.MyLibraryId).Filter!;

        // No push/pop: the root group is the query itself, so its brackets are implicit — the same
        // shape Plex's own editor writes for a top-level "Match any".
        PlexFilterSerializer.Serialize(filter).Should().Be(
            "type=8&sort=titleSort&artist.mood=749936&or=1&album.mood=812004");
    }

    /// <summary>
    /// Until a collection has been liked there is no album tag on the server and so no id to name, and
    /// the rule must stay the bare artist condition rather than a one-child group: Plex's own editor
    /// flattens redundant brackets on save, and a playlist whose stored rules didn't match the
    /// definition that made them would read as "differs" the moment the user opened it.
    /// </summary>
    [Fact]
    public void My_library_stays_a_single_rule_when_only_one_tag_exists()
    {
        PlexFilterSerializer.Serialize(
                SmartPlaylistCatalog.Build(new StockPlaylistOptions(null, LikedAlbum))
                    .Single(d => d.Id == SmartPlaylistCatalog.MyLibraryId).Filter!)
            .Should().Be("type=8&sort=titleSort&album.mood=812004");
    }

    /// <summary>
    /// Tag rules address tags by numeric id, so until the user has thumbed someone the tag doesn't exist
    /// and no rule can name it. That's offered as an explanation, not an error.
    /// </summary>
    [Fact]
    public void My_library_is_unavailable_until_the_liked_tag_exists()
    {
        var definition = SmartPlaylistCatalog.Build(new StockPlaylistOptions())
            .Single(d => d.Id == SmartPlaylistCatalog.MyLibraryId);

        definition.Filter.Should().BeNull();
        definition.Unavailable.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The staleness/worth-hearing rules both Frontier variants are built from. The one-year lane is
    /// "3★ and up", which on Plex's strictly-greater operator is <c>&gt;&gt; 5</c> — one half-step below
    /// the tier, so 3★ itself is in.
    /// </summary>
    /// <summary>
    /// The exclusion every buildable Deep Frontier carries — the artist half alone, which is what the
    /// helper's default supplies.
    /// </summary>
    private const string RejectExclusion = "&and=1&artist.mood!=700001";

    private const string FrontierBody =
        "&push=1"
        + "&push=1&track.userRating%3E%3E=5&and=1&track.lastViewedAt%3C%3C=-1y&pop=1"
        + "&or=1&track.lastViewedAt%3C%3C=-2y"
        + "&pop=1"
        + "&and=1"
        + "&push=1"
        + "&track.userRating=-1"
        + "&or=1&push=1&track.userRating%3E%3E=1&and=1&track.viewCount%3C%3C=5&and=1&track.userRating%3C%3C=4&pop=1"
        + "&or=1&track.userRating%3E%3E=3"
        + "&or=1&push=1&track.userRating%3E%3E=-1&and=1&track.viewCount%3C%3C=1&and=1&track.userRating%3C%3C=2&pop=1"
        + "&pop=1";

    /// <summary>
    /// Deep Frontier is a transcription of a playlist that already exists and works, so it is pinned to
    /// the byte — with the two library-specific mood exclusions ("interlude", "delete") dropped and the
    /// one-year lane lowered to take in 3★ itself.
    /// </summary>
    [Fact]
    public void Deep_frontier_is_the_hand_built_rules_with_the_one_year_lane_starting_at_three_stars()
    {
        PlexFilterSerializer.Serialize(Filter(SmartPlaylistCatalog.DeepFrontierId))
            .Should().Be("type=8&sort=titleSort" + FrontierBody + RejectExclusion);
    }

    /// <summary>
    /// "Approved or not" means unrated, not rejected. An act the user thumbed down has already been
    /// heard and answered, so both verdict moods are subtracted — the artist one for ordinary acts, the
    /// album one for the collections whose umbrella credit is nobody's act to reject. Flat <c>and</c>-ed
    /// "is not" terms, because excluding either is excluding both.
    /// </summary>
    [Fact]
    public void Deep_frontier_excludes_the_artists_and_albums_the_user_thumbed_down()
    {
        PlexFilterSerializer.Serialize(Filter(
                SmartPlaylistCatalog.DeepFrontierId,
                dislikedArtistTagId: DislikedArtist,
                dislikedAlbumTagId: DislikedAlbum))
            .Should().Be(
                "type=8&sort=titleSort" + FrontierBody
                + "&and=1&artist.mood!=700001&and=1&album.mood!=700002");
    }

    /// <summary>
    /// Each half is optional for the same reason every other tag rule here is: a Plex tag has no id
    /// until something carries it, and a user who has only ever rejected acts (never a collection) has
    /// no album-vocabulary tag to name.
    /// </summary>
    [Fact]
    public void Deep_frontier_takes_whichever_reject_tags_exist()
    {
        PlexFilterSerializer.Serialize(
                Filter(SmartPlaylistCatalog.DeepFrontierId, dislikedArtistTagId: DislikedArtist))
            .Should().Be("type=8&sort=titleSort" + FrontierBody + RejectExclusion);

        PlexFilterSerializer.Serialize(Filter(
                SmartPlaylistCatalog.DeepFrontierId,
                dislikedArtistTagId: null,
                dislikedAlbumTagId: DislikedAlbum))
            .Should().Be("type=8&sort=titleSort" + FrontierBody + "&and=1&album.mood!=700002");
    }

    /// <summary>
    /// With no reject tag the exclusion cannot be written at all, and this is the one row where that
    /// silently changes what the playlist <em>selects</em> rather than making it obviously pointless —
    /// so it is withheld rather than shipped without its promise, and the note names the tag that is
    /// missing. In normal operation MoodTagSeeder means this is never reached; when it is, this row is
    /// the visible symptom of a seed that found nothing to anchor to.
    /// </summary>
    [Fact]
    public void Deep_frontier_is_withheld_when_there_is_no_reject_tag_to_exclude_by()
    {
        var definition = SmartPlaylistCatalog
            .Build(new StockPlaylistOptions(LikedArtist, RecommendedArtistMoodTagId: RecommendedArtist))
            .Single(d => d.Id == SmartPlaylistCatalog.DeepFrontierId);

        definition.Filter.Should().BeNull();
        definition.Unavailable.Should().Be(
            "Reject an artist first — nothing in Plex carries your \"disliked\" tag yet.");
    }

    /// <summary>
    /// Either vocabulary on its own is enough to build it — only having neither withholds the row.
    /// </summary>
    [Fact]
    public void Deep_frontier_needs_only_one_of_the_two_reject_vocabularies()
    {
        foreach (var options in new[]
                 {
                     new StockPlaylistOptions(DislikedArtistMoodTagId: DislikedArtist),
                     new StockPlaylistOptions(DislikedAlbumMoodTagId: DislikedAlbum),
                 })
        {
            var definition = SmartPlaylistCatalog.Build(options)
                .Single(d => d.Id == SmartPlaylistCatalog.DeepFrontierId);

            definition.Filter.Should().NotBeNull();
            definition.Unavailable.Should().BeNull();
        }
    }

    /// <summary>
    /// An unavailable row still describes the playlist you would get, so the reader can judge whether
    /// it is worth unblocking. The exclusion bullet is now an invariant of a built Deep Frontier, not
    /// a maybe, so it is stated flatly.
    /// </summary>
    [Fact]
    public void Deep_frontier_always_claims_the_exclusion_it_now_requires()
    {
        foreach (var options in new[]
                 {
                     new StockPlaylistOptions(),
                     new StockPlaylistOptions(DislikedArtistMoodTagId: DislikedArtist),
                 })
        {
            SmartPlaylistCatalog.Build(options)
                .Single(d => d.Id == SmartPlaylistCatalog.DeepFrontierId)
                .Details.Should().Contain("Excludes Mycelium rejected artists and their albums");
        }
    }

    /// <summary>
    /// The bullets are generated from the same options the rules are, so the reject floor they name is
    /// the user's own worst score — 1★ for a whole-star user, 0.5★ for a half-star one. A fixed number
    /// here would describe a rule half the users don't have.
    /// </summary>
    [Theory]
    [InlineData(false, "Excludes 1★ rated songs")]
    [InlineData(true, "Excludes 0.5★ rated songs")]
    public void The_frontier_bullets_name_this_users_reject_floor(bool halfStars, string expected)
    {
        var definitions = SmartPlaylistCatalog.Build(new StockPlaylistOptions(
            LikedArtist, RecommendedArtistMoodTagId: RecommendedArtist, HalfStars: halfStars));

        definitions.Single(d => d.Id == SmartPlaylistCatalog.FrontierId)
            .Details.Should().Equal(
                "Not heard in 1+ years", expected,
                "Includes Mycelium approved or recommended artists and their albums");
        definitions.Single(d => d.Id == SmartPlaylistCatalog.DeepFrontierId)
            .Details.Should().Equal(
                "Not heard in 1+ years", expected,
                "Excludes Mycelium rejected artists and their albums");
    }

    /// <summary>
    /// A star tier is explained by its bullets rather than by a tagline: the title already says "4★+",
    /// and the Fresh variant's second line is the only thing that distinguishes it.
    /// </summary>
    [Fact]
    public void Star_tiers_spell_out_their_threshold_and_window()
    {
        var definitions = SmartPlaylistCatalog.Build(new StockPlaylistOptions(FreshMonths: 1));

        var plain = definitions.Single(d => d.Id == "stars-3");
        plain.Description.Should().BeNull();
        plain.Details.Should().Equal("Rated 3★ and up");

        definitions.Single(d => d.Id == "stars-3-fresh")
            .Details.Should().Equal("Rated 3★ and up", "Not played in 1 month");

        SmartPlaylistCatalog.Build(new StockPlaylistOptions(FreshMonths: 6))
            .Single(d => d.Id == "stars-3-fresh")
            .Details.Should().Equal("Rated 3★ and up", "Not played in 6 months");
    }

    /// <summary>
    /// The plain Frontier is the same body with one more <c>and</c>-ed term: the union of the tags that
    /// say this is music the user has a claim on. Two ids of the same field is not a mistake — "liked"
    /// and "recommended" are separate moods that happen to live in the same artist vocabulary.
    /// </summary>
    [Fact]
    public void Frontier_narrows_the_same_rules_to_liked_and_recommended_artists()
    {
        PlexFilterSerializer.Serialize(
                Filter(SmartPlaylistCatalog.FrontierId, recommendedTagId: RecommendedArtist))
            .Should().Be(
                "type=8&sort=titleSort" + FrontierBody
                + "&and=1&push=1&artist.mood=749936&or=1&artist.mood=901122&pop=1");
    }

    /// <summary>
    /// A liked collection carries its verdict on the album, for the same reason it does in My Library —
    /// so it joins the same Any group rather than being silently excluded from the tagged variant.
    /// </summary>
    [Fact]
    public void Frontier_also_admits_the_album_mood_a_liked_collection_carries()
    {
        var filter = SmartPlaylistCatalog
            .Build(new StockPlaylistOptions(
                LikedArtist, LikedAlbum, RecommendedArtist, HalfStars: true))
            .Single(d => d.Id == SmartPlaylistCatalog.FrontierId).Filter!;

        PlexFilterSerializer.Serialize(filter).Should().Be(
            "type=8&sort=titleSort" + FrontierBody
            + "&and=1&push=1&artist.mood=749936&or=1&artist.mood=901122&or=1&album.mood=812004&pop=1");
    }

    /// <summary>
    /// With only one tag on the server the narrowing term is a bare condition, not a one-child bracket:
    /// Plex's editor flattens those on save, and the playlist would stop matching its definition.
    /// </summary>
    [Fact]
    public void Frontier_stays_a_single_condition_when_only_one_tag_exists()
    {
        PlexFilterSerializer.Serialize(Filter(SmartPlaylistCatalog.FrontierId))
            .Should().Be("type=8&sort=titleSort" + FrontierBody + "&and=1&artist.mood=749936");
    }

    /// <summary>
    /// No tags on the server means nothing to narrow by, and a Frontier that silently widened to the
    /// whole library would just be a second Deep Frontier under the wrong name.
    /// </summary>
    [Fact]
    public void Frontier_is_unavailable_until_one_of_its_tags_exists()
    {
        var definitions = SmartPlaylistCatalog.Build(new StockPlaylistOptions());

        var frontier = definitions.Single(d => d.Id == SmartPlaylistCatalog.FrontierId);
        frontier.Filter.Should().BeNull();
        frontier.Unavailable.Should().NotBeNullOrWhiteSpace();

        // ...and with no tags at all Deep Frontier is withheld too, for its own reason: see
        // Deep_frontier_is_withheld_when_there_is_no_reject_tag_to_exclude_by.
        definitions.Single(d => d.Id == SmartPlaylistCatalog.DeepFrontierId).Filter.Should().BeNull();
    }

    /// <summary>
    /// The rating scale moves exactly one number: the floor under which a rating means "never play
    /// again". A whole-star user's worst score is 1★, so the rejected-but-never-played band widens to
    /// take it in (<c>&lt;&lt; 3</c> rather than <c>&lt;&lt; 2</c>) and the undecided band above it
    /// starts a step higher (<c>&gt;&gt; 2</c> rather than <c>&gt;&gt; 1</c>). Nothing else differs —
    /// the staleness lane and the 2★+ clause are the same rules in both scales.
    /// </summary>
    [Fact]
    public void Whole_star_users_get_a_one_star_reject_floor()
    {
        PlexFilterSerializer.Serialize(Filter(SmartPlaylistCatalog.DeepFrontierId, halfStars: false))
            .Should().Be(
                "type=8&sort=titleSort"
                + "&push=1"
                + "&push=1&track.userRating%3E%3E=5&and=1&track.lastViewedAt%3C%3C=-1y&pop=1"
                + "&or=1&track.lastViewedAt%3C%3C=-2y"
                + "&pop=1"
                + "&and=1"
                + "&push=1"
                + "&track.userRating=-1"
                + "&or=1&push=1&track.userRating%3E%3E=2&and=1&track.viewCount%3C%3C=5&and=1&track.userRating%3C%3C=4&pop=1"
                + "&or=1&track.userRating%3E%3E=3"
                + "&or=1&push=1&track.userRating%3E%3E=-1&and=1&track.viewCount%3C%3C=1&and=1&track.userRating%3C%3C=3&pop=1"
                + "&pop=1"
                + RejectExclusion);
    }

    /// <summary>
    /// A user who has never answered gets the whole-star rules — the scale every Plex client can
    /// actually set. Half stars are opt-in per app, so they are opt-in here too.
    /// </summary>
    [Fact]
    public void The_default_scale_is_whole_stars()
    {
        SmartPlaylistCatalog.DefaultHalfStars.Should().BeFalse();

        PlexFilterSerializer.Serialize(Filter(SmartPlaylistCatalog.DeepFrontierId, halfStars: false))
            .Should().Be(PlexFilterSerializer.Serialize(
                SmartPlaylistCatalog.Build(new StockPlaylistOptions(DislikedArtistMoodTagId: DislikedArtist))
                    .Single(d => d.Id == SmartPlaylistCatalog.DeepFrontierId).Filter!));
    }

    /// <summary>
    /// The source playlist carries two extra exclusions we deliberately don't generate, so it must
    /// <em>not</em> be reported as already existing. Extra rules change what a playlist selects; claiming
    /// a match would be a lie, and the page says "differs" instead.
    /// </summary>
    [Fact]
    public void The_original_frontier_with_its_extra_rules_is_not_treated_as_a_match()
    {
        var original = PlexFilterParser.Parse(PlexSmartFilterFixtures.Real.Single(p => p.Title == "Frontier").Query);

        PlexFilterCanonicalizer.AreEquivalent(Filter(SmartPlaylistCatalog.DeepFrontierId), original)
            .Should().BeFalse();
    }

    /// <summary>
    /// The starter rows are a fixed one-month window whatever the picker is set to, so they carry the
    /// window in their id — two definitions can't share one, and the picker generates the same shape
    /// at whichever window the user chose.
    /// </summary>
    [Fact]
    public void The_fresh_starters_are_pinned_to_one_month()
    {
        var definitions = SmartPlaylistCatalog.Build(new StockPlaylistOptions(FreshMonths: 12));

        foreach (var tier in SmartPlaylistCatalog.StarterTiers(SmartPlaylistCatalog.DefaultHalfStars))
        {
            var starter = definitions.Single(d => d.Id == SmartPlaylistCatalog.StarterTierId(tier));

            starter.Title.Should().Be($"{SmartPlaylistCatalog.TierLabel(tier)} (Fresh 1mo)");
            PlexFilterSerializer.Serialize(starter.Filter!)
                .Should().EndWith("&and=1&track.lastViewedAt%3C%3C=-1mon");
        }

        // ...and the picker's own fresh variant is still the window it was asked for.
        definitions.Single(d => d.Id == "stars-4-fresh").Title.Should().Be("4★+ (Fresh 12mo)");
    }

    /// <summary>
    /// The user's own tier playlists are the same idea at neighbouring thresholds, and telling someone
    /// they already have a playlist they don't is the worst thing this page can do. Every tier is
    /// checked against every real playlist: a match is allowed only where the rules are literally the
    /// same query — which two of the fixtures are, now that the half tiers exist. Iterating the
    /// half-star tiers covers the whole-star ones too, since those are a subset.
    /// </summary>
    [Fact]
    public void A_tier_matches_a_real_playlist_only_when_the_rules_are_identical()
    {
        foreach (var ratingUnits in SmartPlaylistCatalog.Tiers(halfStars: true))
        {
            var ours = Filter(SmartPlaylistCatalog.TierId(ratingUnits));
            foreach (var (title, query) in PlexSmartFilterFixtures.Real)
            {
                var theirs = PlexFilterParser.Parse(query);
                var identical = PlexFilterSerializer.Serialize(theirs)
                                == PlexFilterSerializer.Serialize(ours);

                PlexFilterCanonicalizer.AreEquivalent(ours, theirs)
                    .Should().Be(
                        identical,
                        "'{0}' should match our {1} tier only if it is the same rule set",
                        title,
                        SmartPlaylistCatalog.TierLabel(ratingUnits));
            }
        }
    }

    /// <summary>
    /// The recognition the page depends on: a playlist we generated, read back off the server and parsed,
    /// still matches the definition that produced it — including after the flattening Plex applies.
    /// </summary>
    [Fact]
    public void A_generated_playlist_read_back_still_matches_its_definition()
    {
        foreach (var halfStars in new[] { true, false })
        {
            var options = new StockPlaylistOptions(
                LikedArtist, LikedAlbum, RecommendedArtist, DislikedArtist, DislikedAlbum,
                HalfStars: halfStars);

            foreach (var definition in SmartPlaylistCatalog.Build(options).Where(d => d.Filter is not null))
            {
                var roundTripped = PlexFilterParser.Parse(PlexFilterSerializer.Serialize(definition.Filter!));

                PlexFilterCanonicalizer.AreEquivalent(definition.Filter!, roundTripped)
                    .Should().BeTrue($"{definition.Id} must recognise itself");
            }
        }
    }
}
