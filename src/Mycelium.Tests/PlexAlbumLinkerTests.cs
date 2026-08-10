using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

public class PlexAlbumLinkerTests
{
    private const string MachineId = "abc123";
    private const string Artist = "Mick Gordon";
    private const string Album = "Doom: Original Game Soundtrack";

    private readonly IPlexApi _plex = Substitute.For<IPlexApi>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly PlexAlbumLinker _sut;

    public PlexAlbumLinkerTests()
    {
        _sut = new PlexAlbumLinker(_catalog, _plex, NullLogger<PlexAlbumLinker>.Instance);
        _plex.GetMachineIdentifier().Returns(MachineId);
        // Default: nothing captured — individual tests store the keys they need.
        _catalog.GetAlbumPlexRatingKeys(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase));
    }

    private void StoredKeys(string artist, params (string Album, int Key)[] albums) =>
        _catalog.GetAlbumPlexRatingKeys(Arg.Is<IReadOnlyCollection<string>>(a => a.Contains(artist)))
            .Returns(new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
            {
                [artist] = albums.ToDictionary(a => a.Album, a => a.Key, StringComparer.OrdinalIgnoreCase),
            });

    [Fact]
    public async Task Links_the_album_by_its_stored_rating_key()
    {
        StoredKeys(Artist, (Album, 4242));

        var linked = await _sut.WithLinks(new[] { new LibraryAlbumOption(Artist, Album) });

        linked.Should().ContainSingle().Which.PlexUrl.Should()
            .Be($"https://app.plex.tv/desktop/#!/server/{MachineId}/details?key=%2Flibrary%2Fmetadata%2F4242");
    }

    [Fact]
    public async Task Leaves_an_album_with_no_captured_key_unlinked()
    {
        StoredKeys(Artist, ("Some Other Record", 7));

        var linked = await _sut.WithLinks(new[] { new LibraryAlbumOption(Artist, Album) });

        linked.Should().ContainSingle().Which.PlexUrl.Should().BeNull();
    }

    [Fact]
    public async Task Returns_options_unlinked_when_plex_cant_be_reached()
    {
        StoredKeys(Artist, (Album, 4242));
        _plex.GetMachineIdentifier().Returns<string?>(_ => throw new HttpRequestException("down"));

        var linked = await _sut.WithLinks(new[] { new LibraryAlbumOption(Artist, Album) });

        linked.Should().ContainSingle().Which.PlexUrl.Should().BeNull();
    }

    [Fact]
    public async Task Asks_plex_for_nothing_when_there_are_no_options()
    {
        (await _sut.WithLinks(Array.Empty<LibraryAlbumOption>())).Should().BeEmpty();

        await _plex.DidNotReceive().GetMachineIdentifier();
    }

    [Fact]
    public async Task Looks_up_only_the_artists_actually_offered()
    {
        await _sut.WithLinks(new[]
        {
            new LibraryAlbumOption(Artist, Album),
            new LibraryAlbumOption(Artist, "Doom Eternal"),
            new LibraryAlbumOption("Matthewdavid's Mindflight", "Care Tracts"),
        });

        await _catalog.Received(1).GetAlbumPlexRatingKeys(
            Arg.Is<IReadOnlyCollection<string>>(a => a.Count == 2));
    }
}
