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
    private static PlexSmartFilter Filter(string id, string? likedTagId = "749936", int freshMonths = 3) =>
        SmartPlaylistCatalog.Build(likedTagId, freshMonths).Single(d => d.Id == id).Filter!;

    [Theory]
    // 3 stars is a rating of 6, and Plex has no ">=", so the rule is "greater than 5".
    [InlineData(3, "5")]
    [InlineData(4, "7")]
    [InlineData(5, "9")]
    public void Star_tiers_land_on_the_half_step_below_the_tier(int stars, string expected)
    {
        SmartPlaylistCatalog.RatingAtLeast(stars).Should().Be(expected);

        Filter($"stars-{stars}").Rules
            .Should().Be(new PlexCondition("track.userRating", PlexOp.GreaterThan, expected));
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
        string Title(int months) => SmartPlaylistCatalog.Build("749936", months)
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
    /// Tag rules address tags by numeric id, so until the user has thumbed someone the tag doesn't exist
    /// and no rule can name it. That's offered as an explanation, not an error.
    /// </summary>
    [Fact]
    public void My_library_is_unavailable_until_the_liked_tag_exists()
    {
        var definition = SmartPlaylistCatalog.Build(likedMoodTagId: null, freshMonths: 3)
            .Single(d => d.Id == SmartPlaylistCatalog.MyLibraryId);

        definition.Filter.Should().BeNull();
        definition.Unavailable.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Frontier is a transcription of a playlist that already exists and works, so it is pinned to the
    /// byte — with only the two library-specific mood exclusions ("interlude", "delete") dropped.
    /// </summary>
    [Fact]
    public void Frontier_matches_the_playlist_it_was_transcribed_from()
    {
        PlexFilterSerializer.Serialize(Filter(SmartPlaylistCatalog.FrontierId)).Should().Be(
            "type=8&sort=titleSort"
            + "&push=1"
            + "&push=1&track.userRating%3E%3E=6&and=1&track.lastViewedAt%3C%3C=-1y&pop=1"
            + "&or=1&track.lastViewedAt%3C%3C=-2y"
            + "&pop=1"
            + "&and=1"
            + "&push=1"
            + "&track.userRating=-1"
            + "&or=1&push=1&track.userRating%3E%3E=1&and=1&track.viewCount%3C%3C=5&and=1&track.userRating%3C%3C=4&pop=1"
            + "&or=1&track.userRating%3E%3E=3"
            + "&or=1&push=1&track.userRating%3E%3E=-1&and=1&track.viewCount%3C%3C=1&and=1&track.userRating%3C%3C=2&pop=1"
            + "&pop=1");
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

        PlexFilterCanonicalizer.AreEquivalent(Filter(SmartPlaylistCatalog.FrontierId), original)
            .Should().BeFalse();
    }

    /// <summary>
    /// The user's own tier playlists are the same idea at different thresholds, so none of them should
    /// be mistaken for ours — this is the check that the survey won't tell someone they already have a
    /// playlist they don't.
    /// </summary>
    [Fact]
    public void Existing_tier_playlists_at_other_thresholds_are_not_matches()
    {
        foreach (var stars in SmartPlaylistCatalog.StarTiers)
        {
            var ours = Filter($"stars-{stars}");
            foreach (var (title, query) in PlexSmartFilterFixtures.Real)
            {
                PlexFilterCanonicalizer.AreEquivalent(ours, PlexFilterParser.Parse(query))
                    .Should().BeFalse($"'{title}' is not the same rule set as our {stars}-star tier");
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
        foreach (var definition in SmartPlaylistCatalog.Build("749936", 3).Where(d => d.Filter is not null))
        {
            var roundTripped = PlexFilterParser.Parse(PlexFilterSerializer.Serialize(definition.Filter!));

            PlexFilterCanonicalizer.AreEquivalent(definition.Filter!, roundTripped)
                .Should().BeTrue($"{definition.Id} must recognise itself");
        }
    }
}
