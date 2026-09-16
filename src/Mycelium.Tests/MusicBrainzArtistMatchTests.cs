using FluentAssertions;
using Mycelium.ListenBrainz.Models;
using Mycelium.ListenBrainz.Services;
using Xunit;

namespace Mycelium.Tests;

public class MusicBrainzArtistMatchTests
{
    private static MusicBrainzArtist Hit(string id, string name, params string[] aliases) => new()
    {
        Id = id,
        Name = name,
        Aliases = aliases.Select(a => new MusicBrainzAlias { Name = a }).ToList(),
    };

    /// <summary>The case that started this: MusicBrainz's confident top hit for an act it doesn't know.</summary>
    [Fact]
    public void A_confident_hit_under_another_name_is_no_match()
    {
        MusicBrainzArtistMatch.Pick(
                new[] { Hit("cb", "Canadian Brass"), Hit("nc", "Noel Comia Jr.") }, "Noel Brass Jr.")
            .Should().BeNull();
    }

    [Theory]
    [InlineData("radiohead", "Radiohead")]
    [InlineData("Trans-Siberian Orchestra", "Trans‐Siberian Orchestra")]
    [InlineData("Guns N' Roses", "Guns N’ Roses")]
    [InlineData("Beyonce", "Beyoncé")]
    [InlineData("AC-DC", "AC/DC")]
    [InlineData("Beatles", "The Beatles")]
    [InlineData("Simon and Garfunkel", "Simon & Garfunkel")]
    public void Spelling_noise_still_matches(string library, string musicBrainz)
    {
        MusicBrainzArtistMatch.Pick(new[] { Hit("x", musicBrainz) }, library)!.Id.Should().Be("x");
        MusicBrainzArtistMatch.NamesMatch(musicBrainz, library).Should().BeTrue();
    }

    [Fact]
    public void A_strict_primary_name_beats_a_higher_ranked_alias_or_loose_match()
    {
        var hits = new[]
        {
            Hit("alias", "Something Else", "Nirvana"),
            Hit("loose", "Nirvána"),
            Hit("exact", "Nirvana"),
        };

        MusicBrainzArtistMatch.Pick(hits, "Nirvana")!.Id.Should().Be("exact");
    }

    [Fact]
    public void An_alias_is_accepted_when_no_primary_name_matches()
    {
        MusicBrainzArtistMatch.Pick(new[] { Hit("s", "Sync"), Hit("n", "*NSYNC", "NSYNC") }, "NSYNC")!
            .Id.Should().Be("n");
    }

    [Fact]
    public void An_all_punctuation_name_only_matches_itself()
    {
        MusicBrainzArtistMatch.Pick(new[] { Hit("q", "???") }, "!!!").Should().BeNull();
        MusicBrainzArtistMatch.Pick(new[] { Hit("q", "???"), Hit("c", "!!!") }, "!!!")!.Id.Should().Be("c");
    }
}
