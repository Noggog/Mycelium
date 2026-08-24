using FluentAssertions;
using Mycelium.Interfaces;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The quality scale and its two mappings. The comparison semantics matter more than they look:
/// every "do we already have this?" decision is a comparison against a possibly-null tier, and
/// getting the null case backwards would offer an upgrade for every album nobody has inspected yet.
/// </summary>
public class AudioQualityTests
{
    [Fact]
    public void An_unknown_quality_never_looks_upgradeable()
    {
        AudioQuality? unknown = null;

        // This is the whole reason "don't know" is null rather than an Unknown = 0 enum member: the
        // lifted comparison answers "no" in both directions, so an un-inspected album is never
        // mistaken for one that needs replacing.
        (unknown < AudioQuality.Lossless).Should().BeFalse();
        (unknown < AudioQuality.Lossy).Should().BeFalse();
    }

    [Fact]
    public void Lossless_outranks_lossy()
    {
        (AudioQuality.Lossy < AudioQuality.Lossless).Should().BeTrue();
    }

    [Theory]
    [InlineData(AudioQuality.Lossless, "2")]
    [InlineData(AudioQuality.Lossy, "1")]
    public void Tiers_map_onto_streamrips_quality_scale(AudioQuality quality, string expected)
    {
        quality.ToStreamripQuality().Should().Be(expected);
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("FLAC")]
    [InlineData("alac")]
    [InlineData("wav")]
    public void Lossless_codecs_are_recognised_case_insensitively(string codec)
    {
        AudioQualityTier.FromCodec(codec).Should().Be(AudioQuality.Lossless);
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("aac")]
    [InlineData("opus")]
    // Anything not on the (short, closed) lossless list is lossy — the alternative is a lossy list
    // that has to grow with every new encoder, where a miss silently claims CD quality.
    [InlineData("some-codec-from-2030")]
    public void Everything_else_is_lossy(string codec)
    {
        AudioQualityTier.FromCodec(codec).Should().Be(AudioQuality.Lossy);
    }

    [Fact]
    public void No_codec_at_all_is_unknown_rather_than_lossy()
    {
        AudioQualityTier.FromCodec(null).Should().BeNull();
        AudioQualityTier.FromCodec("  ").Should().BeNull();
    }

    [Fact]
    public void An_album_is_judged_by_its_majority_not_its_worst_track()
    {
        // The shape Deezer's per-track gaps actually produce: lossless bar one track the fallback
        // ladder had to fetch at 320. Judged on its worst track this album would be offered for
        // upgrade forever.
        var mostlyFlac = Enumerable.Repeat((AudioQuality?)AudioQuality.Lossless, 20)
            .Append(AudioQuality.Lossy);

        AudioQualityTier.Majority(mostlyFlac).Should().Be(AudioQuality.Lossless);
    }

    [Fact]
    public void A_genuinely_lossy_album_is_not_rescued_by_a_stray_lossless_track()
    {
        var mostlyMp3 = Enumerable.Repeat((AudioQuality?)AudioQuality.Lossy, 19)
            .Append(AudioQuality.Lossless);

        AudioQualityTier.Majority(mostlyMp3).Should().Be(AudioQuality.Lossy);
    }

    [Fact]
    public void A_tie_goes_to_lossless()
    {
        var half = new AudioQuality?[] { AudioQuality.Lossless, AudioQuality.Lossy };
        AudioQualityTier.Majority(half).Should().Be(AudioQuality.Lossless);
    }

    [Fact]
    public void An_unreadable_track_sides_with_lossless()
    {
        // So one track Plex reports no codec for can't drag an otherwise-lossless album into the
        // upgrade queue.
        var withUnknown = new AudioQuality?[] { AudioQuality.Lossless, null, AudioQuality.Lossy };
        AudioQualityTier.Majority(withUnknown).Should().Be(AudioQuality.Lossless);
    }

    [Fact]
    public void No_tracks_is_no_verdict()
    {
        AudioQualityTier.Majority(Array.Empty<AudioQuality?>()).Should().BeNull();
    }

    [Theory]
    [InlineData("Lossless", AudioQuality.Lossless)]
    [InlineData("lossy", AudioQuality.Lossy)]
    public void Stored_names_round_trip_case_insensitively(string raw, AudioQuality expected)
    {
        AudioQualityTier.Parse(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Perfect")]
    // A numeric value would be streamrip's vocabulary, not ours — accepting it here would let the two
    // scales blur together.
    [InlineData("2")]
    public void An_unrecognised_name_parses_as_unset_so_the_callers_default_applies(string? raw)
    {
        AudioQualityTier.Parse(raw).Should().BeNull();
    }
}
