using FluentAssertions;
using Mycelium.Interfaces;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// <see cref="UmbrellaArtist"/> decides two things a user sees: whether a record is a
/// <em>collection</em> (findable only by naming it, since no discography lists it), and whether a like
/// is stamped on the album instead of the artist. A false positive is the expensive direction — it
/// would pull a real band out of the recommendation feed and misfile its verdicts — so the collisions
/// are what these tests are mostly about.
/// </summary>
public class UmbrellaArtistTests
{
    [Theory]
    // Everything the strict placeholder list covers is covered here too — this is a superset.
    [InlineData("Various Artists")]
    [InlineData("various artists")]
    [InlineData("  Various Artists  ")]
    [InlineData("Various Artist")]
    [InlineData("Unknown Artist")]
    // Soundtrack and score credits, as Deezer and Plex spell them.
    [InlineData("Original Soundtrack")]
    [InlineData("Soundtrack")]
    [InlineData("Original Motion Picture Soundtrack")]
    [InlineData("Original Score")]
    [InlineData("Original Video Game Soundtrack")]
    // Compilation buckets in the locales Deezer answers in.
    [InlineData("Multi-interprètes")]
    [InlineData("Varios Artistas")]
    // Cast credits — matched by pattern, because Deezer appends the show and no fixed list could
    // enumerate every production.
    [InlineData("Original Cast")]
    [InlineData("Original Cast Recording")]
    [InlineData("Original Broadway Cast")]
    [InlineData("Original Broadway Cast Recording")]
    [InlineData("Original London Cast Recording")]
    [InlineData("Original Broadway Cast of Hamilton")]
    [InlineData("The Original Cast")]
    public void Recognises_umbrella_credits(string name) =>
        UmbrellaArtist.Is(name).Should().BeTrue();

    [Theory]
    // The collisions PlaceholderArtist warns about, which is why none of these one-word names is on
    // the list: Cast is the Liverpool britpop band, Various and VA are real acts.
    [InlineData("Cast")]
    [InlineData("Various")]
    [InlineData("VA")]
    // Ordinary bands, including ones whose names brush the vocabulary.
    [InlineData("Radiohead")]
    [InlineData("Hans Zimmer")]
    [InlineData("The Soundtrack of Our Lives")]
    [InlineData("Cast Iron Filter")]
    [InlineData("Original Sin")]
    [InlineData("Broadway Calls")]
    [InlineData("")]
    [InlineData(null)]
    public void Leaves_real_acts_alone(string? name) =>
        UmbrellaArtist.Is(name).Should().BeFalse();

    /// <summary>
    /// The feed filter and the similarity walk moved from the strict list to this one, so anything the
    /// strict list caught must still be caught — otherwise a "Various Artists" bucket would start
    /// appearing as a card to rate.
    /// </summary>
    [Theory]
    [InlineData("Various Artists")]
    [InlineData("Various Artist")]
    [InlineData("Unknown Artist")]
    public void Is_a_superset_of_the_placeholder_list(string name)
    {
        PlaceholderArtist.Is(name).Should().BeTrue();
        UmbrellaArtist.Is(name).Should().BeTrue();
    }
}
