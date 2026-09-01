using System.Text.Json.Nodes;
using FluentAssertions;
using Mycelium.Backend.Services.Archive;
using Xunit;

namespace Mycelium.Tests;

public class CanonicalYamlTests
{
    private static string Yaml(string block) => block.ReplaceLineEndings("\n");

    [Fact]
    public void Field_order_in_the_source_cannot_change_the_output()
    {
        // The archive rests on this. Mongo makes no promise about the order it hands a document's
        // fields back in, and the snapshot commits only when bytes change — so if insertion order
        // reached the file, every night would rewrite everything and bury the real changes.
        var one = new JsonObject { ["b"] = "second", ["a"] = "first" };
        var other = new JsonObject { ["a"] = "first", ["b"] = "second" };

        CanonicalYaml.Document(one).Should().Be(CanonicalYaml.Document(other));
    }

    [Fact]
    public void An_album_reads_as_block_yaml()
    {
        var album = new JsonObject
        {
            ["album"] = "Kid A",
            ["artist"] = "Radiohead",
            ["songs"] = new JsonArray(
                new JsonObject { ["title"] = "Everything in Its Right Place" },
                new JsonObject
                {
                    ["title"] = "Idioteque",
                    ["ratings"] = new JsonObject { ["kelsey"] = 4.5, ["noggog"] = 5.0 },
                }),
        };

        CanonicalYaml.Document(album).Should().Be(Yaml(
            """
            album: "Kid A"
            artist: "Radiohead"
            songs:
              - title: "Everything in Its Right Place"
              - ratings:
                  kelsey: 4.5
                  noggog: 5
                title: "Idioteque"

            """));
    }

    [Fact]
    public void Adding_a_rating_is_one_added_line_and_nothing_else_moves()
    {
        // The actual requirement, stated as a diff. Block YAML has no commas, so an insertion never
        // rewrites its neighbour the way appending to a JSON object does.
        var before = CanonicalYaml.Document(new JsonObject
        {
            ["ratings"] = new JsonObject { ["kelsey"] = 4.5 },
            ["title"] = "Idioteque",
        });

        var after = CanonicalYaml.Document(new JsonObject
        {
            ["ratings"] = new JsonObject { ["kelsey"] = 4.5, ["noggog"] = 5.0 },
            ["title"] = "Idioteque",
        });

        var beforeLines = before.Split('\n');
        after.Split('\n').Length.Should().Be(beforeLines.Length + 1);
        after.Split('\n').Should().Contain(beforeLines.Where(l => l.Length > 0));
    }

    // ---- the reason every string is quoted ----

    [Theory]
    // Real artists in the library this was built for. Bare, YAML 1.1 reads these as booleans and null.
    [InlineData("No")]
    [InlineData("Yes")]
    [InlineData("On")]
    [InlineData("Y")]
    [InlineData("Null")]
    // Real album titles. Bare, "0034" loads as 34 and the leading zeros are gone for ever.
    [InlineData("0034")]
    [InlineData("11100011")]
    [InlineData("7")]
    // Reserved openers: a comment, an anchor, a flow sequence, an alias.
    [InlineData("#digitalfreedom")]
    [InlineData("& The Brite Lites at Svenska Grammofonstudion")]
    [InlineData("[bsd.u]")]
    [InlineData("*asterisk")]
    // 307 titles in the library contain ": ", which ends a plain scalar mid-name.
    [InlineData("1492: Conquest of Paradise")]
    [InlineData("A I A : Alien Observer")]
    public void A_name_yaml_would_misread_survives_a_round_trip(string name)
    {
        var document = CanonicalYaml.Document(new JsonObject { ["artist"] = name });

        // Quoted, so nothing coerces it...
        document.Should().StartWith("artist: \"");
        // ...and it comes back as the string it went in as. Parsed rather than pattern-matched,
        // because the point is what a reader will actually get.
        YamlRoundTrip.Scalar(document, "artist").Should().Be(name);
    }

    [Fact]
    public void Numbers_and_booleans_are_not_quoted_so_a_rating_stays_a_number()
    {
        var record = new JsonObject { ["stars"] = 4.5, ["whole"] = 3.0, ["pinned"] = true, ["id"] = 399L };

        CanonicalYaml.Document(record).Should().Be(Yaml(
            """
            id: 399
            pinned: true
            stars: 4.5
            whole: 3

            """));
    }

    [Fact]
    public void A_key_that_yaml_would_misread_is_quoted_too()
    {
        // Usernames are keys in the ratings map, and "no" is a plausible username.
        var record = new JsonObject { ["ratings"] = new JsonObject { ["no"] = 4.5, ["kelsey"] = 3.0 } };

        var document = CanonicalYaml.Document(record);
        document.Should().Contain("\"no\": 4.5");
        // ...while ordinary keys stay bare and readable.
        document.Should().Contain("kelsey: 3");
    }

    // ---- shape ----

    [Fact]
    public void Empty_containers_are_written_inline()
    {
        var record = new JsonObject { ["genres"] = new JsonArray(), ["plex"] = new JsonObject() };

        CanonicalYaml.Document(record).Should().Be(Yaml(
            """
            genres: []
            plex: {}

            """));
    }

    [Fact]
    public void Array_order_is_preserved_because_it_is_data()
    {
        // A playlist's running order and an album's track order are meaning, not presentation.
        var record = new JsonObject { ["tracks"] = new JsonArray("Second", "First") };

        CanonicalYaml.Document(record).Should().Be(Yaml(
            """
            tracks:
              - "Second"
              - "First"

            """));
    }

    [Fact]
    public void Nulls_are_omitted_rather_than_written()
    {
        // A field arriving in the schema shouldn't flip every existing record from absent to null.
        CanonicalYaml.Document(new JsonObject { ["a"] = "kept", ["b"] = null })
            .Should().NotContain("b");
    }

    [Fact]
    public void Non_ascii_is_written_through_rather_than_escaped()
    {
        CanonicalYaml.Document(new JsonObject { ["artist"] = "Sigur Rós" })
            .Should().Contain("Sigur Rós");
    }

    [Fact]
    public void Quotes_and_backslashes_are_escaped()
    {
        var document = CanonicalYaml.Document(new JsonObject { ["artist"] = "a\"b\\c" });

        document.Should().Contain(@"""a\""b\\c""");
        YamlRoundTrip.Scalar(document, "artist").Should().Be("a\"b\\c");
    }
}

/// <summary>
/// A deliberately tiny YAML reader for the one thing these tests need to know: that a top-level
/// <c>key: "value"</c> line round-trips to the string that went in. Hand-rolled rather than pulled in
/// as a dependency, because it only has to understand the subset the archive emits.
/// </summary>
internal static class YamlRoundTrip
{
    public static string Scalar(string document, string key)
    {
        var line = document.Split('\n').Single(l => l.StartsWith(key + ": ", StringComparison.Ordinal));
        var value = line[(key.Length + 2)..];

        if (!value.StartsWith('"'))
        {
            return value;
        }

        var builder = new System.Text.StringBuilder();
        for (var i = 1; i < value.Length - 1; i++)
        {
            if (value[i] != '\\')
            {
                builder.Append(value[i]);
                continue;
            }

            i++;
            builder.Append(value[i] switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                _ => value[i],
            });
        }

        return builder.ToString();
    }
}
