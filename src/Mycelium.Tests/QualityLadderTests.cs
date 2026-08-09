using FluentAssertions;
using Mycelium.Backend;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The fallback ladder decides how far a download is allowed to downgrade before giving up, and
/// getting it wrong is silent: a chain that stops one rung early leaves tracks undownloaded while the
/// album still reports as promoted. Deezer's formats vary per track, so the ladder has to run all the
/// way to the bottom (0 = 128kbps MP3) — that's the only floor there is.
/// </summary>
public class QualityLadderTests
{
    [Theory]
    [InlineData("2", new[] { "1", "0" })]   // FLAC: fall back to 320, then 128
    [InlineData("1", new[] { "0" })]        // already lossy: only 128 remains
    [InlineData("0", new string[0])]        // nothing below 128 exists
    public void Unset_derives_every_step_below_the_preferred_quality(string preferred, string[] expected)
    {
        MainModule.ParseQualities(null, preferred).Should().Equal(expected);
    }

    [Fact]
    public void An_explicit_chain_overrides_the_derived_one_and_keeps_its_order()
    {
        MainModule.ParseQualities("1,0", "2").Should().Equal("1", "0");
        MainModule.ParseQualities("0,1", "2").Should().Equal("0", "1");
    }

    [Fact]
    public void An_explicit_blank_refuses_to_downgrade_at_all()
    {
        // Deliberate: accept gaps rather than 128kbps files. Distinct from unset, which derives.
        MainModule.ParseQualities("", "2").Should().BeEmpty();
    }

    [Fact]
    public void Whitespace_and_duplicates_are_dropped_without_reordering()
    {
        MainModule.ParseQualities(" 1 , , 0 , 1 ", "2").Should().Equal("1", "0");
    }

    [Fact]
    public void A_non_numeric_preferred_quality_invents_no_ladder()
    {
        // Better to attempt only what was configured than to guess a numeric scale that may not apply.
        MainModule.ParseQualities(null, "lossless").Should().BeEmpty();
        MainModule.ParseQualities(null, "").Should().BeEmpty();
    }
}
