using FluentAssertions;
using Mycelium.Plex.Services.Smart;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The smart-playlist rule engine. The parser and serializer are held to the real thing: every fixture
/// in <see cref="PlexSmartFilterFixtures"/> is a filter query read off a live Plex server, so anything
/// the format does in practice is covered whether or not it was anticipated here.
/// </summary>
public class PlexFilterTests
{
    public static TheoryData<string, string> RealPlaylists()
    {
        var data = new TheoryData<string, string>();
        foreach (var (title, query) in PlexSmartFilterFixtures.Real)
        {
            data.Add(title, query);
        }

        return data;
    }

    // ---- round trips against real data -------------------------------------------------------

    [Theory]
    [MemberData(nameof(RealPlaylists))]
    public void Every_real_playlist_parses(string title, string query)
    {
        var act = () => PlexFilterParser.Parse(query);
        act.Should().NotThrow($"'{title}' is a filter Plex actually stores");
    }

    /// <summary>
    /// Serializing a parsed filter and parsing it again must yield the same tree. This — rather than
    /// byte equality with the original — is the property that matters: Plex writes the same rule several
    /// ways (<c>!=</c> vs <c>!%3D=</c>, options before or after rules), so re-encoding is allowed to
    /// settle on one spelling as long as no meaning is lost.
    /// </summary>
    [Theory]
    [MemberData(nameof(RealPlaylists))]
    public void Reserializing_a_real_playlist_preserves_its_rules(string title, string query)
    {
        var parsed = PlexFilterParser.Parse(query);
        var reparsed = PlexFilterParser.Parse(PlexFilterSerializer.Serialize(parsed));

        PlexFilterCanonicalizer.Canonical(reparsed)
            .Should().Be(PlexFilterCanonicalizer.Canonical(parsed), $"'{title}' must survive a round trip");
        reparsed.Type.Should().Be(parsed.Type);
        reparsed.Options.Should().Equal(parsed.Options);
    }

    // ---- the shapes those fixtures contain, spelled out ---------------------------------------

    [Fact]
    public void Operators_are_read_from_the_param_name_not_the_value()
    {
        var rules = PlexFilterParser.Parse(
            "type=8&track.userRating%3E%3E=6&and=1&track.viewCount%3C%3C=5&and=1&track.mood!=8516").Rules;

        rules.Should().BeEquivalentTo(PlexGroup.All(
            new PlexCondition("track.userRating", PlexOp.GreaterThan, "6"),
            new PlexCondition("track.viewCount", PlexOp.LessThan, "5"),
            new PlexCondition("track.mood", PlexOp.IsNot, "8516")));
    }

    /// <summary>
    /// Plex writes "is not" two ways on a string field: <c>artist.title!=X</c> ("does not contain") and
    /// <c>artist.title!%3D=X</c> ("is not"). Both appear in the fixtures, on the same field, and they are
    /// genuinely different operators — so the parser must keep them apart.
    /// </summary>
    [Fact]
    public void The_two_spellings_of_is_not_are_different_operators()
    {
        PlexFilterParser.Parse("type=8&artist.title!=Hearts%20of%20Space").Rules
            .Should().Be(new PlexCondition("artist.title", PlexOp.IsNot, "Hearts of Space"));

        PlexFilterParser.Parse("type=8&artist.title!%3D=Hearts%20of%20Space").Rules
            .Should().Be(new PlexCondition("artist.title", PlexOp.StringIsNot, "Hearts of Space"));
    }

    [Fact]
    public void Push_and_pop_become_nested_groups()
    {
        // "1 - Suspect": rating between half a star and one and a half.
        var rules = PlexFilterParser.Parse(
            "type=8&sort=titleSort&push=1&track.userRating%3E%3E=1&or=1&track.userRating%3C%3C=3&pop=1").Rules;

        rules.Should().BeEquivalentTo(PlexGroup.Any(
            new PlexCondition("track.userRating", PlexOp.GreaterThan, "1"),
            new PlexCondition("track.userRating", PlexOp.LessThan, "3")));
    }

    [Fact]
    public void Sort_and_other_query_options_are_kept_but_are_not_rules()
    {
        var filter = PlexFilterParser.Parse(
            "type=10&sort=random%3Adesc&group=guid&track.userRating=8");

        filter.Type.Should().Be(10);
        filter.Rules.Should().Be(new PlexCondition("track.userRating", PlexOp.Is, "8"));
        filter.Options.Should().Equal(
            new KeyValuePair<string, string>("sort", "random%3Adesc"),
            new KeyValuePair<string, string>("group", "guid"));
    }

    [Fact]
    public void Unbalanced_grouping_is_rejected_rather_than_guessed_at()
    {
        var missingPop = () => PlexFilterParser.Parse("type=8&push=1&track.userRating=8");
        missingPop.Should().Throw<FormatException>();

        var strayPop = () => PlexFilterParser.Parse("type=8&track.userRating=8&pop=1");
        strayPop.Should().Throw<FormatException>();
    }

    [Fact]
    public void Content_uris_are_unwrapped_to_a_section_and_a_filter()
    {
        const string content =
            "library://x/directory/%2Flibrary%2Fsections%2F1%2Fall%3Ftype%3D8%26track%2EuserRating%3D10";

        PlexFilterParser.TryParseContent(content, out var section, out var filter).Should().BeTrue();
        section.Should().Be(1);
        filter.Rules.Should().Be(new PlexCondition("track.userRating", PlexOp.Is, "10"));
    }

    [Fact]
    public void A_content_uri_that_is_not_a_section_query_is_skipped_not_thrown_on()
    {
        PlexFilterParser.TryParseContent(null, out _, out _).Should().BeFalse();
        PlexFilterParser.TryParseContent("library://x/item/12345", out _, out _).Should().BeFalse();
    }

    // ---- serialization details that keep us indistinguishable from Plex's own editor ----------

    [Fact]
    public void Serialization_matches_the_spelling_plex_itself_writes()
    {
        var filter = new PlexSmartFilter(
            8,
            PlexGroup.All(
                new PlexCondition("track.userRating", PlexOp.GreaterThan, "7"),
                new PlexCondition("track.lastViewedAt", PlexOp.LessThan, "-3mon"),
                new PlexCondition("artist.title", PlexOp.IsNot, "Hearts of Space")),
            new[] { new KeyValuePair<string, string>("sort", "titleSort") });

        PlexFilterSerializer.Serialize(filter).Should().Be(
            "type=8&sort=titleSort"
            + "&track.userRating%3E%3E=7"
            + "&and=1&track.lastViewedAt%3C%3C=-3mon"
            + "&and=1&artist.title!=Hearts%20of%20Space");
    }

    [Fact]
    public void Nesting_that_alternates_join_is_written_with_push_and_pop()
    {
        var filter = new PlexSmartFilter(8, PlexGroup.All(
            PlexGroup.Any(
                new PlexCondition("track.userRating", PlexOp.Is, "10"),
                new PlexCondition("track.userRating", PlexOp.Is, "9")),
            new PlexCondition("artist.mood", PlexOp.Is, "749936")));

        PlexFilterSerializer.Serialize(filter).Should().Be(
            "type=8&push=1&track.userRating=10&or=1&track.userRating=9&pop=1&and=1&artist.mood=749936");
    }

    /// <summary>
    /// An AND inside an AND says nothing, and Plex's editor drops it on the next save — so we never
    /// write it in the first place. Otherwise a playlist would stop matching the definition that created
    /// it the moment the user opened and re-saved it by hand.
    /// </summary>
    [Fact]
    public void Nesting_that_repeats_its_parents_join_is_not_written_at_all()
    {
        var filter = new PlexSmartFilter(8, PlexGroup.All(
            PlexGroup.All(
                new PlexCondition("track.userRating", PlexOp.Is, "10"),
                new PlexCondition("track.viewCount", PlexOp.GreaterThan, "0")),
            new PlexCondition("artist.mood", PlexOp.Is, "749936")));

        PlexFilterSerializer.Serialize(filter).Should().Be(
            "type=8&track.userRating=10&and=1&track.viewCount%3E%3E=0&and=1&artist.mood=749936");
    }
}
