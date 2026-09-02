using System.Text.Json;
using FluentAssertions;
using Mycelium.Interfaces;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The enums that cross the wire to the web client are serialized by name, not by ordinal. The
/// client's mirrored union types are string literals ('MissingAlbum', 'Pending', …) and it compares
/// against them everywhere — badge lookups, feed filters, status sections — so an enum that ships as
/// a number doesn't fail loudly, it just makes every one of those comparisons quietly false.
///
/// That is exactly what happened once: a class was inserted between
/// <c>[JsonConverter(typeof(JsonStringEnumConverter))]</c> and <see cref="FeedKind"/>, so the
/// attribute landed on the class (where it is a legal no-op) and the enum lost its converter. It
/// compiled, it deployed, and the only symptom was blank badges on the Discover feed. These asserts
/// are cheap and would have caught it at the commit.
/// </summary>
public class WireEnumTests
{
    [Theory]
    [InlineData(FeedKind.RecommendedArtist, "RecommendedArtist")]
    [InlineData(FeedKind.MissingAlbum, "MissingAlbum")]
    [InlineData(FeedKind.UpgradeAlbum, "UpgradeAlbum")]
    [InlineData(FeedKind.LibraryArtist, "LibraryArtist")]
    [InlineData(FeedKind.RecommendedLibraryArtist, "RecommendedLibraryArtist")]
    [InlineData(FeedKind.SeedLibraryArtist, "SeedLibraryArtist")]
    [InlineData(FeedKind.ReconsiderArtist, "ReconsiderArtist")]
    [InlineData(FeedKind.SecondThoughtsArtist, "SecondThoughtsArtist")]
    [InlineData(FeedKind.IndifferentLikeArtist, "IndifferentLikeArtist")]
    [InlineData(FeedKind.IndifferentDislikeArtist, "IndifferentDislikeArtist")]
    public void A_feed_kind_goes_over_the_wire_as_its_name(FeedKind kind, string expected)
    {
        JsonSerializer.Serialize(kind).Should().Be($"\"{expected}\"");
    }

    [Theory]
    [InlineData(PurchaseStatus.Pending, "Pending")]
    [InlineData(PurchaseStatus.Queued, "Queued")]
    [InlineData(PurchaseStatus.Downloading, "Downloading")]
    [InlineData(PurchaseStatus.Sent, "Sent")]
    [InlineData(PurchaseStatus.InLibrary, "InLibrary")]
    [InlineData(PurchaseStatus.Failed, "Failed")]
    public void A_purchase_status_goes_over_the_wire_as_its_name(PurchaseStatus status, string expected)
    {
        JsonSerializer.Serialize(status).Should().Be($"\"{expected}\"");
    }

    [Theory]
    [InlineData(ManualAddResult.Added, "Added")]
    [InlineData(ManualAddResult.BadLink, "BadLink")]
    [InlineData(ManualAddResult.NotFound, "NotFound")]
    [InlineData(ManualAddResult.AlreadyQueued, "AlreadyQueued")]
    [InlineData(ManualAddResult.AlreadyOwned, "AlreadyOwned")]
    public void A_manual_add_result_goes_over_the_wire_as_its_name(ManualAddResult result, string expected)
    {
        JsonSerializer.Serialize(result).Should().Be($"\"{expected}\"");
    }

    [Theory]
    [InlineData(AudioQuality.Lossy, "Lossy")]
    [InlineData(AudioQuality.Lossless, "Lossless")]
    public void An_audio_quality_goes_over_the_wire_as_its_name(AudioQuality quality, string expected)
    {
        JsonSerializer.Serialize(quality).Should().Be($"\"{expected}\"");
    }

    [Theory]
    [InlineData(DiscoveryStatus.Pending, "Pending")]
    [InlineData(DiscoveryStatus.Liked, "Liked")]
    [InlineData(DiscoveryStatus.Disliked, "Disliked")]
    [InlineData(DiscoveryStatus.Snoozed, "Snoozed")]
    [InlineData(DiscoveryStatus.Indifferent, "Indifferent")]
    public void A_discovery_status_goes_over_the_wire_as_its_name(DiscoveryStatus status, string expected)
    {
        JsonSerializer.Serialize(status).Should().Be($"\"{expected}\"");
    }

    /// <summary>
    /// The whole record, not just the bare enum: an attribute on the enum declaration is what the
    /// property inherits, and serializing a member is the path the API actually takes.
    /// </summary>
    [Fact]
    public void A_feed_item_carries_its_kind_as_a_name()
    {
        var item = new FeedItem(
            FeedKind.UpgradeAlbum,
            new ArtistKey("Boards of Canada"),
            "Geogaddi",
            null,
            0,
            [],
            null,
            null,
            null,
            AudioQuality.Lossy);

        JsonSerializer.Serialize(item)
            .Should().Contain("\"UpgradeAlbum\"")
            // The owned quality on an upgrade card is read through a string-keyed label map, so an
            // ordinal here is the same silent blank as a numeric kind.
            .And.Contain("\"Lossy\"");
    }

    /// <summary>
    /// The manual-add reply, for the same reason: the paste box looks its result up in a string-keyed
    /// copy map, so an ordinal renders no message at all — the refusal reads as the button doing
    /// nothing, which is worse than an error.
    /// </summary>
    [Fact]
    public void A_manual_add_outcome_carries_its_result_as_a_name()
    {
        var outcome = new ManualAddOutcome(ManualAddResult.AlreadyOwned, null);

        JsonSerializer.Serialize(outcome).Should().Contain("\"AlreadyOwned\"");
    }
}
