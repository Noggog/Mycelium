using FluentAssertions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Smart;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// Turning a Plex smart playlist into something a reader who has never seen Plex can act on.
///
/// <para>This is the one archived field that used to be pure server-local state: a URI carrying a
/// machine id and section number, tag conditions holding numeric ids, wire-token operators, and star
/// ratings on a scale nothing else in the archive uses. The rules are stored *instead of* the
/// membership on the grounds that they are the durable thing — which is only true if they can still
/// be read on other hardware.</para>
/// </summary>
public class PlaylistRuleMapperTests
{
    private static PlexSmartFilter Parse(string query) => PlexFilterParser.Parse(query);

    private static PlaylistRules Map(string query, PlaylistRuleMapper.TagResolver? tags = null) =>
        PlaylistRuleMapper.ToPortable(Parse(query), tags)!;

    private static PlaylistCondition Condition(PlaylistRule rule) => (PlaylistCondition)rule;

    // ---- the four things that made the stored form unusable ----

    [Fact]
    public void Wire_operators_are_spelled_out()
    {
        // ">>" is not something a reader can look up, and Plex's own editor labels the same key
        // differently per field type ("is greater than" on a number, "is after" on a date).
        Condition(Map("type=8&track.userRating%3E%3E=7").Rules[0]).Op.Should().Be("greater than");
        Condition(Map("type=8&artist.title!=Hearts%20of%20Space").Rules[0]).Op.Should().Be("is not");
        Condition(Map("type=8&artist.title!%3D=Hearts%20of%20Space").Rules[0]).Op.Should().Be("not equals");

        // Plex spells plain equality two ways and means different things by them: "=" is the tag /
        // number "is" (and "contains" on free text), "==" the exact-match form for text. Both survive
        // as distinct words rather than being flattened into one.
        Condition(Map("type=8&track.userRating=1").Rules[0]).Op.Should().Be("is");
        Condition(Map("type=8&artist.title%3D=Hearts").Rules[0]).Op.Should().Be("equals");
    }

    [Fact]
    public void Star_ratings_move_onto_the_scale_the_rest_of_the_archive_uses()
    {
        // Plex doubles ratings so half stars are whole numbers. An album file records the same user's
        // rating of the same track as 4.5, so a rule saying 9 would read as a different measurement.
        Condition(Map("type=8&track.userRating%3E%3E=8").Rules[0]).Value.Should().Be("4");
        Condition(Map("type=8&track.userRating%3E%3E=7").Rules[0]).Value.Should().Be("3.5");

        // -1 is Plex's "no rating at all", which halved would be a nonsense -0.5.
        Condition(Map("type=8&track.userRating=-1").Rules[0]).Value.Should().Be("unrated");
    }

    [Fact]
    public void Tag_ids_become_tag_names()
    {
        // The single biggest reason a stored rule means nothing elsewhere: 2779 is a row id in one
        // server's database, and says nothing about ambient music.
        var tags = (string field, string value) =>
            field == "artist.mood" && value == "2779" ? "Ambient" : null;

        var rules = Map("type=8&artist.mood=2779", new PlaylistRuleMapper.TagResolver(tags));
        Condition(rules.Rules[0]).Value.Should().Be("Ambient");
    }

    [Fact]
    public void An_unresolvable_tag_keeps_its_id_rather_than_losing_the_rule()
    {
        // A vocabulary Plex wouldn't hand over costs the name, not the condition. A dropped rule would
        // silently change what the playlist claims to select.
        var rules = Map("type=8&artist.mood=2779");

        Condition(rules.Rules[0]).Field.Should().Be("artist.mood");
        Condition(rules.Rules[0]).Value.Should().Be("2779");
    }

    [Fact]
    public void A_bare_field_name_is_qualified_by_the_querys_own_type()
    {
        // Only API-written filters use the bare form, where it means "the queried type's own field".
        // The archive doesn't keep `type`, so leaving it bare would make the rule ambiguous.
        Condition(Map("type=10&userRating%3E%3E=8").Rules[0]).Field.Should().Be("track.userRating");
        Condition(Map("type=8&userRating%3E%3E=8").Rules[0]).Field.Should().Be("artist.userRating");
    }

    // ---- structure ----

    [Fact]
    public void A_single_rule_still_says_how_it_matches()
    {
        // "all" of one thing is trivially true, but stating it means a reader never has to guess what
        // the shape would mean if another rule were added.
        var rules = Map("type=8&track.userRating%3E%3E=7");

        rules.Match.Should().Be("all");
        rules.Rules.Should().ContainSingle();
    }

    [Fact]
    public void An_or_group_keeps_its_nesting_and_its_join()
    {
        // "1 - Suspect", captured from a real server: rated 1 OR below 3, which is not the same
        // playlist as rated 1 AND below 3.
        var rules = Map(
            "type=8&sort=titleSort&push=1&track.userRating%3E%3E=1&or=1&track.userRating%3C%3C=3&pop=1");

        // The outer group has a single child, so flattening dissolves the brackets and the "any" rises
        // to the top — the same rewrite Plex's own editor performs.
        rules.Match.Should().Be("any");
        rules.Rules.Should().HaveCount(2);
        Condition(rules.Rules[0]).Op.Should().Be("greater than");
        Condition(rules.Rules[1]).Op.Should().Be("less than");
    }

    [Fact]
    public void A_group_nested_under_a_different_join_stays_nested()
    {
        // "Ambient": (album mood OR artist mood) AND artist is not X. Collapsing that would widen the
        // playlist to everything by that artist.
        var rules = Map(
            "type=8&push=1&album.mood=2779&or=1&artist.mood=2779&pop=1&and=1&artist.title!=Hearts%20of%20Space");

        rules.Match.Should().Be("all");
        rules.Rules.Should().HaveCount(2);

        var group = rules.Rules.OfType<PlaylistRuleGroup>().Single();
        group.Match.Should().Be("any");
        group.Rules.Should().HaveCount(2);
    }

    [Fact]
    public void Sort_and_limit_are_kept_but_the_metadata_type_is_not()
    {
        // Sort and limit change what the playlist yields or in what order. `type` does not — for an
        // audio playlist it only decides what a sort key refers to, and it is a Plex-internal code.
        var rules = Map("type=10&sort=random%3Adesc&limit=50&track.userRating%3E%3E=8");

        rules.Sort.Should().Be("random:desc");
        rules.Limit.Should().Be(50);
    }

    [Fact]
    public void A_filter_with_no_rules_is_not_a_definition()
    {
        PlaylistRuleMapper.ToPortable(Parse("type=8&sort=titleSort")).Should().BeNull();
    }

    // ---- the whole captured set ----

    [Theory]
    [MemberData(nameof(RealPlaylists))]
    public void Every_real_playlist_maps_without_leaving_a_wire_token_behind(string title, string query)
    {
        // Swept across every hand-made playlist captured from a real server, so a shape the author
        // never anticipated shows up here rather than in the archive.
        var rules = PlaylistRuleMapper.ToPortable(Parse(query));
        if (rules is null)
        {
            return;
        }

        foreach (var condition in Flatten(rules.Rules))
        {
            condition.Field.Should().Contain(".", $"{title}: every field is scoped");
            condition.Op.Should().NotBeNullOrWhiteSpace();
            // The operator is a phrase, never one of Plex's punctuation tokens.
            condition.Op.Should().NotContainAny(">", "<", "=", "!");
        }
    }

    public static TheoryData<string, string> RealPlaylists()
    {
        var data = new TheoryData<string, string>();
        foreach (var (title, query) in PlexSmartFilterFixtures.Real)
        {
            data.Add(title, query);
        }

        return data;
    }

    private static IEnumerable<PlaylistCondition> Flatten(IEnumerable<PlaylistRule> rules)
    {
        foreach (var rule in rules)
        {
            switch (rule)
            {
                case PlaylistCondition condition:
                    yield return condition;
                    break;
                case PlaylistRuleGroup group:
                    foreach (var child in Flatten(group.Rules))
                    {
                        yield return child;
                    }

                    break;
            }
        }
    }
}
