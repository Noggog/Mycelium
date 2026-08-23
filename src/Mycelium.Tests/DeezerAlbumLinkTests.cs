using FluentAssertions;
using Mycelium.Deezer;
using Xunit;

namespace Mycelium.Tests;

public class DeezerAlbumLinkTests
{
    [Theory]
    // What copying the address bar actually yields, with and without the locale segment.
    [InlineData("https://www.deezer.com/en/album/225323002", 225323002L)]
    [InlineData("https://www.deezer.com/album/225323002", 225323002L)]
    [InlineData("https://www.deezer.com/fr/album/225323002", 225323002L)]
    // Deezer's own share button appends tracking params.
    [InlineData("https://www.deezer.com/album/225323002?utm_source=deezer&utm_medium=web", 225323002L)]
    [InlineData("http://deezer.com/album/225323002", 225323002L)]
    // Bare id, for anyone who already has one.
    [InlineData("225323002", 225323002L)]
    [InlineData("  225323002  ", 225323002L)]
    public void Reads_the_album_id_out_of_what_a_user_pastes(string pasted, long expected) =>
        DeezerAlbumLink.TryParse(pasted).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Cluster Flies")]
    // Different keyspaces: an artist or playlist id read as an album id would resolve to nothing, or
    // to an unrelated album — worse than refusing.
    [InlineData("https://www.deezer.com/en/artist/5080")]
    [InlineData("https://www.deezer.com/en/playlist/225323002")]
    [InlineData("https://www.deezer.com/en/track/1353302352")]
    // Short links can't be read without following a redirect, which a paste shouldn't trigger.
    [InlineData("https://deezer.page.link/abc123")]
    // Deezer has no album 0, and the downloader reads 0 as "no id".
    [InlineData("https://www.deezer.com/album/0")]
    [InlineData("0")]
    public void Returns_null_for_anything_that_is_not_an_album_link(string? pasted) =>
        DeezerAlbumLink.TryParse(pasted).Should().BeNull();
}
