using FluentAssertions;
using Mycelium.Backend.Services.Singletons;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The covers that ship with the app.
///
/// <para>Worth pinning down because the id crosses the network — it goes out to the browser in the
/// survey and comes back on the art route — so "which resource does this open" has to be answered by
/// a fixed list and never by the caller.</para>
/// </summary>
public class PlaylistArtTests
{
    [Theory]
    [InlineData(PlaylistArt.Ridge)]
    [InlineData(PlaylistArt.Poolroom)]
    [InlineData(PlaylistArt.Prism)]
    [InlineData(PlaylistArt.Frontier)]
    [InlineData(PlaylistArt.DeepFrontier)]
    public void Every_named_cover_is_embedded_in_the_build(string id)
    {
        using var image = PlaylistArt.Open(id);

        // Catches the two ways this breaks silently: the file dropping out of the csproj glob, and a
        // build whose resource keys stop matching the "Posters/{id}.jpg" name they are looked up by.
        image.Should().NotBeNull();

        // ...and that it opened the file it was asked for: the covers are JPEGs, which open FF D8 FF.
        var header = new byte[3];
        image!.ReadExactly(header);
        header.Should().Equal(0xFF, 0xD8, 0xFF);
    }

    /// <summary>
    /// Anything not on the list opens nothing — including a name trying to walk out of the resource
    /// prefix it gets interpolated into, and one that differs only in case.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("my-library")]
    [InlineData("four-star")]
    [InlineData("grid")]
    [InlineData("../appsettings")]
    [InlineData("Posters/four-star")]
    [InlineData("FOUR-STAR")]
    public void Anything_else_is_not_art(string? id)
    {
        PlaylistArt.Open(id).Should().BeNull();
    }
}
