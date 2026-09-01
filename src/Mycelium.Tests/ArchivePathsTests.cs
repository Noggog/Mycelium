using FluentAssertions;
using Mycelium.Backend.Services.Archive;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The examples here are taken from a real library of ~3,000 artists, where 579 album titles contain
/// a path separator, 19 artists end in a dot, and 26 pairs differ only by case.
/// </summary>
public class ArchivePathsTests
{
    [Theory]
    // The reason a naive directory-per-artist scheme cannot work: these are real titles.
    [InlineData("60/40", "60%2F40")]
    [InlineData("Gorgeous / Fantasy", "Gorgeous %2F Fantasy")]
    [InlineData("w/ Love", "w%2F Love")]
    [InlineData("AC/DC", "AC%2FDC")]
    // Colons are fine on Linux and fatal on Windows.
    [InlineData("Amenti: Psalms", "Amenti%3A Psalms")]
    // Percent itself is escaped, or the encoding would be ambiguous.
    [InlineData("50% Off", "50%25 Off")]
    // Ordinary names are left completely alone — most of the library reads as itself.
    [InlineData("Radiohead", "Radiohead")]
    [InlineData("Sigur Rós", "Sigur Rós")]
    public void Path_hostile_characters_are_percent_encoded(string name, string expected)
    {
        ArchivePaths.Escape(name).Should().Be(expected);
    }

    [Theory]
    // Windows silently strips these, which would collide "Dinosaur Jr." with a band called
    // "Dinosaur Jr" — so the last character is encoded instead.
    [InlineData("Dinosaur Jr.", "Dinosaur Jr%2E")]
    [InlineData("Fred again..", "Fred again.%2E")]
    [InlineData("Trailing space ", "Trailing space%20")]
    public void Trailing_dots_and_spaces_are_encoded(string name, string expected)
    {
        ArchivePaths.Escape(name).Should().Be(expected);
    }

    [Theory]
    // Claimed by the OS on Windows whatever the extension.
    [InlineData("CON")]
    [InlineData("aux")]
    [InlineData("COM1")]
    public void Windows_device_names_are_defused(string name)
    {
        ArchivePaths.Escape(name).Should().StartWith("_");
    }

    [Fact]
    public void A_name_that_escapes_to_nothing_still_gets_a_segment()
    {
        ArchivePaths.Escape("/").Should().NotBeEmpty();
    }

    [Fact]
    public void An_absurdly_long_name_is_cut_without_splitting_a_character()
    {
        var name = new string('é', 300);

        var segment = ArchivePaths.Escape(name);

        System.Text.Encoding.UTF8.GetByteCount(segment).Should().BeLessThan(255);
        // Cut on a character boundary, so the result is still valid text.
        segment.Should().NotContain("�");
    }

    [Fact]
    public void Names_differing_only_by_case_get_distinct_segments()
    {
        // Two directories on Linux, one on macOS — so without this, cloning the archive on a Mac would
        // silently merge two artists and lose one.
        var paths = ArchivePaths.ForNames(["tUnE-yArDs", "Tune-Yards"]);

        paths["tUnE-yArDs"].Should().NotBe(paths["Tune-Yards"]);
        paths.Values.Select(v => v.ToLowerInvariant()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_name_with_no_collision_keeps_its_plain_form()
    {
        // Suffixing everything would make 3,000 directories ugly to serve 26.
        ArchivePaths.ForNames(["Radiohead", "Portishead"])["Radiohead"].Should().Be("Radiohead");
    }

    [Fact]
    public void A_collision_suffix_does_not_move_when_a_neighbour_appears()
    {
        // The suffix comes from the name's own text, not its position in the set. Were it positional,
        // adding one artist would rename the others and turn one new album into a whole-tree rewrite.
        var before = ArchivePaths.ForNames(["24kGoldn", "24kgoldn"]);
        var after = ArchivePaths.ForNames(["24kGoldn", "24kgoldn", "Aardvark", "zzz"]);

        after["24kGoldn"].Should().Be(before["24kGoldn"]);
        after["24kgoldn"].Should().Be(before["24kgoldn"]);
    }

    [Fact]
    public void The_same_input_always_produces_the_same_segments()
    {
        var once = ArchivePaths.ForNames(["Radiohead", "AC/DC", "tUnE-yArDs", "Tune-Yards"]);
        var twice = ArchivePaths.ForNames(["Tune-Yards", "tUnE-yArDs", "AC/DC", "Radiohead"]);

        once.Should().BeEquivalentTo(twice);
    }
}
