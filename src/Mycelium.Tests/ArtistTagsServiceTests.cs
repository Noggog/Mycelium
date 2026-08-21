using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// Covers the tag editor's two invariants: the app's own like/dislike verdict moods are neither shown
/// nor editable, and every write is a delta (so the rest of the field — the invisible verdict tags
/// included — survives an edit).
/// </summary>
public class ArtistTagsServiceTests
{
    private const string Artist = "Radiohead";
    private const int LibraryKey = 1;

    private readonly IPlexApi _plex = Substitute.For<IPlexApi>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly ArtistTagsService _sut;

    public ArtistTagsServiceTests()
    {
        _sut = new ArtistTagsService(_catalog, _plex, NullLogger<ArtistTagsService>.Instance);
        _catalog.GetPlexRatingKeys(Arg.Any<ArtistKey>()).Returns(Array.Empty<int>());
        _plex.ResolveLibrary().Returns(new PlexLibrary { Key = LibraryKey, Title = "Music", Type = "artist" });
    }

    private static PlexTag[] Tags(params string[] tags) => tags.Select(t => new PlexTag { Tag = t }).ToArray();

    /// <summary>Stores one Plex item under the artist's name and stubs the fetch for it.</summary>
    private PlexMusicArtist Item(int ratingKey, string[]? genres = null, string[]? styles = null, string[]? moods = null)
    {
        var item = new PlexMusicArtist
        {
            RatingKey = ratingKey,
            Title = Artist,
            Genre = Tags(genres ?? Array.Empty<string>()),
            Style = Tags(styles ?? Array.Empty<string>()),
            Mood = Tags(moods ?? Array.Empty<string>()),
        };
        _plex.GetMusicArtist(ratingKey).Returns(item);
        return item;
    }

    private void StoredKeys(params int[] keys) =>
        _catalog.GetPlexRatingKeys(new ArtistKey(Artist)).Returns(keys);

    [Fact]
    public async Task NotInPlex_ReportsAbsentWithNoTags()
    {
        var tags = await _sut.Get(new ArtistKey(Artist));

        tags.Present.Should().BeFalse();
        tags.Genres.Should().BeEmpty();
        await _plex.DidNotReceive().GetMusicArtist(Arg.Any<int>());
    }

    [Fact]
    public async Task Get_HidesTheAppsOwnVerdictMoods()
    {
        StoredKeys(10);
        Item(10, genres: new[] { "Rock" }, styles: new[] { "Shoegaze" },
            moods: new[] { "Melancholy", "noggog_liked", "other_disliked" });

        var tags = await _sut.Get(new ArtistKey(Artist));

        tags.Present.Should().BeTrue();
        tags.Genres.Should().Equal("Rock");
        tags.Styles.Should().Equal("Shoegaze");
        tags.Moods.Should().Equal("Melancholy");
    }

    [Fact]
    public async Task Get_UnionsTagsAcrossEveryPlexItemTheNameMapsTo()
    {
        // A ';'-joined collaborator title makes one name span several Plex items.
        StoredKeys(10, 11);
        Item(10, genres: new[] { "Rock" });
        Item(11, genres: new[] { "rock", "Electronic" });

        var tags = await _sut.Get(new ArtistKey(Artist));

        tags.Genres.Should().Equal("Electronic", "Rock"); // de-duped case-insensitively, alphabetical
    }

    [Fact]
    public async Task AddGenre_WritesTheDeltaAndMirrorsIntoTheCatalog()
    {
        StoredKeys(10);
        var item = Item(10, genres: new[] { "Rock" });
        // The refreshed read after the write sees the new tag.
        _plex.When(p => p.SetArtistGenres(LibraryKey, 10, Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<IReadOnlyCollection<string>>()))
            .Do(_ => item.Genre = Tags("Rock", "Electronic"));

        var tags = await _sut.Update(new ArtistKey(Artist), "genre", new[] { "Electronic" }, Array.Empty<string>());

        await _plex.Received(1).SetArtistGenres(LibraryKey, 10,
            Arg.Is<IReadOnlyCollection<string>>(a => a.SequenceEqual(new[] { "Electronic" })),
            Arg.Is<IReadOnlyCollection<string>>(r => r.Count == 0));
        tags.Genres.Should().Equal("Electronic", "Rock");
        await _catalog.Received(1).SetGenres(new ArtistKey(Artist),
            Arg.Is<IReadOnlyList<string>>(g => g.SequenceEqual(new[] { "Electronic", "Rock" })));
    }

    [Fact]
    public async Task RemoveMood_SendsPlexsOwnCasingSoTheDropMatches()
    {
        StoredKeys(10);
        Item(10, moods: new[] { "Melancholy" });

        await _sut.Update(new ArtistKey(Artist), "mood", Array.Empty<string>(), new[] { "melancholy" });

        await _plex.Received(1).SetArtistMoods(LibraryKey, 10,
            Arg.Is<IReadOnlyCollection<string>>(a => a.Count == 0),
            Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(new[] { "Melancholy" })));
    }

    [Fact]
    public async Task AlreadyInTheDesiredState_WritesNothing()
    {
        StoredKeys(10);
        Item(10, genres: new[] { "Rock" });

        await _sut.Update(new ArtistKey(Artist), "genre", new[] { "rock" }, new[] { "Jazz" });

        await _plex.DidNotReceive().SetArtistGenres(
            Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    [Theory]
    [InlineData("noggog_liked")]
    [InlineData("someone_disliked")]
    public async Task VerdictMoodsAreRejected(string tag)
    {
        StoredKeys(10);
        Item(10, moods: new[] { tag });

        // Both directions: the tab must not be able to grant or revoke a rating behind the thumbs' back.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.Update(new ArtistKey(Artist), "mood", new[] { tag }, Array.Empty<string>()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.Update(new ArtistKey(Artist), "mood", Array.Empty<string>(), new[] { tag }));

        await _plex.DidNotReceive().SetArtistMoods(
            Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task UnknownFieldIsRejected()
    {
        StoredKeys(10);
        Item(10);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.Update(new ArtistKey(Artist), "collection", new[] { "Anything" }, Array.Empty<string>()));
    }

    [Fact]
    public async Task BlankTagsAreIgnoredRatherThanWritten()
    {
        StoredKeys(10);
        Item(10, styles: new[] { "Shoegaze" });

        await _sut.Update(new ArtistKey(Artist), "style", new[] { "   " }, Array.Empty<string>());

        await _plex.DidNotReceive().SetArtistStyles(
            Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task StaleRatingKeysAreSkipped()
    {
        // A key the last sync captured can go stale (library rebuild); the good item is still edited.
        StoredKeys(10, 11);
        Item(10, genres: new[] { "Rock" });
        _plex.GetMusicArtist(11).Returns((PlexMusicArtist?)null);

        await _sut.Update(new ArtistKey(Artist), "genre", new[] { "Electronic" }, Array.Empty<string>());

        await _plex.Received(1).SetArtistGenres(LibraryKey, 10,
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>());
    }
}
