using FluentAssertions;
using Mycelium.Deezer;
using Xunit;

namespace Mycelium.Tests;

public class DeezerArtistLinkTests
{
    [Theory]
    [InlineData("https://www.deezer.com/us/artist/1588149", 1588149L)]
    [InlineData("  https://www.deezer.com/artist/1588149?utm_source=deezer&host=1  ", 1588149L)]
    [InlineData("deezer.com/en/artist/42/top_track", 42L)]
    public void Reads_the_id_from_an_artist_url(string pasted, long expected) =>
        DeezerArtistLink.TryParseUrl(pasted).Should().Be(expected);

    [Theory]
    [InlineData("https://www.deezer.com/us/album/1588149")]
    [InlineData("https://www.deezer.com/artist/0")]
    [InlineData("1588149")]
    [InlineData("Norska")]
    [InlineData(null)]
    public void Rejects_anything_but_an_artist_url(string? pasted) =>
        DeezerArtistLink.TryParseUrl(pasted).Should().BeNull();

    [Theory]
    [InlineData(" 1588149 ", 1588149L)]
    [InlineData("0", null)]
    [InlineData("311 band", null)]
    public void Reads_a_bare_id(string pasted, long? expected) =>
        DeezerArtistLink.TryParseBareId(pasted).Should().Be(expected);
}
