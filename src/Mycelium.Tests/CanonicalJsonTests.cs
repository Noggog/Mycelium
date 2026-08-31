using System.Text.Json.Nodes;
using FluentAssertions;
using Mycelium.Backend.Services.Archive;
using Xunit;

namespace Mycelium.Tests;

public class CanonicalJsonTests
{
    [Fact]
    public void Field_order_in_the_source_cannot_change_the_output()
    {
        // The whole archive rests on this. Mongo makes no promise about the order it hands a document's
        // fields back in, and the snapshot only commits when bytes change — so if insertion order could
        // reach the file, every night would rewrite every file and bury the real changes.
        var one = new JsonObject { ["b"] = "second", ["a"] = "first", ["c"] = "third" };
        var other = new JsonObject { ["c"] = "third", ["a"] = "first", ["b"] = "second" };

        CanonicalJson.Line(one).Should().Be(CanonicalJson.Line(other));
        CanonicalJson.Line(one).Should().Be("""{"a": "first", "b": "second", "c": "third"}""");
    }

    [Fact]
    public void Nulls_are_omitted_rather_than_written()
    {
        // A field arriving in the schema shouldn't flip every existing row from absent to null.
        var record = new JsonObject { ["a"] = "kept", ["b"] = null };

        CanonicalJson.Line(record).Should().Be("""{"a": "kept"}""");
    }

    [Fact]
    public void Non_ascii_is_written_through_rather_than_escaped()
    {
        // The archive is meant to be read by a person; ó in place of an accent defeats that.
        var record = new JsonObject { ["artist"] = "Sigur Rós" };

        CanonicalJson.Line(record).Should().Be("""{"artist": "Sigur Rós"}""");
    }

    [Fact]
    public void Quotes_backslashes_and_control_characters_are_escaped()
    {
        var record = new JsonObject { ["t"] = "a\"b\\c\nd\te" };

        CanonicalJson.Line(record).Should().Be("""{"t": "a\"b\\c\nd\te"}""");
    }

    [Fact]
    public void Numbers_and_booleans_keep_a_stable_shape()
    {
        var record = new JsonObject
        {
            ["id"] = 1234567890123L,
            ["score"] = 2.5,
            ["whole"] = 3.0,
            ["flag"] = true,
        };

        // An integral double prints without a decimal point, so a score stored as 3 and one stored as
        // 3.0 can't produce two different lines.
        CanonicalJson.Line(record).Should()
            .Be("""{"flag": true, "id": 1234567890123, "score": 2.5, "whole": 3}""");
    }

    [Fact]
    public void Nested_objects_and_arrays_are_sorted_too()
    {
        var record = new JsonObject
        {
            ["albums"] = new JsonArray("Kid A", "Amnesiac"),
            ["reconsider"] = new JsonObject { ["ratedCount"] = 4L, ["average"] = 3.5 },
        };

        // Array order is data and is preserved; object key order is not and is normalised.
        CanonicalJson.Line(record).Should()
            .Be("""{"albums": ["Kid A", "Amnesiac"], "reconsider": {"average": 3.5, "ratedCount": 4}}""");
    }
}
