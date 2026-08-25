using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The album tagger is what makes a liked compilation reachable from a smart playlist. Two things
/// about it are easy to get wrong and invisible when wrong: it must find the album under the title
/// <em>Plex</em> chose rather than the one we queued, and it must write a delta, so a hand-applied
/// mood the user's own smart collections filter on survives a thumb.
/// </summary>
public class PlexAlbumTaggerTests
{
    private const string Artist = PlaceholderArtist.VariousArtists;
    private const string Album = "The Breakfast Club";
    private const string Liked = "noggog_liked";
    private const string Disliked = "noggog_disliked";

    private readonly IPlexApi _plex = Substitute.For<IPlexApi>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly IAlbumMatchOverrideRepo _overrides = Substitute.For<IAlbumMatchOverrideRepo>();
    private readonly PlexAlbumTagger _sut;

    public PlexAlbumTaggerTests()
    {
        _sut = new PlexAlbumTagger(_plex, _catalog, _overrides, NullLogger<PlexAlbumTagger>.Instance);
        _plex.ResolveLibrary().Returns(new PlexLibrary { Key = 1, Title = "Music", Type = "artist" });
        // Default: the library holds nothing under this act, and no merges are recorded — individual
        // tests override.
        _catalog.GetOwnedAlbums()
            .Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase));
        _catalog.GetAlbumPlexRatingKeys(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase));
        _overrides.GetAll().Returns(Array.Empty<AlbumMatchOverride>());
    }

    /// <summary>Puts one album under <see cref="Artist"/> in the catalog at <paramref name="key"/>.</summary>
    private void Owned(string title, int key)
    {
        _catalog.GetOwnedAlbums()
            .Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
            {
                [Artist] = new(StringComparer.OrdinalIgnoreCase) { [title] = null },
            });
        _catalog.GetAlbumPlexRatingKeys(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
            {
                [Artist] = new(StringComparer.OrdinalIgnoreCase) { [title] = key },
            });
    }

    private void PlexAlbum(int key, string title, params string[] moods) =>
        _plex.GetMusicAlbum(key).Returns(new PlexMusicAlbum
        {
            RatingKey = key,
            Title = title,
            ParentTitle = Artist,
            Mood = moods.Select(m => new PlexTag { Tag = m }).ToArray(),
        });

    private Task ReceivedEdit(int key, string[] add, string[] remove) =>
        _plex.Received(1).SetAlbumMoods(1, key,
            Arg.Is<IReadOnlyCollection<string>>(a => a.SequenceEqual(add)),
            Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(remove)));

    [Fact]
    public async Task Stamps_the_verdict_and_preserves_hand_applied_moods()
    {
        // "80s" stands in for a mood the user applied themselves, which an existing smart collection
        // may filter on — a verdict write must never disturb it.
        Owned(Album, 42);
        PlexAlbum(42, Album, "80s");

        await _sut.SetTags(Artist, Album, Liked, Array.Empty<string>());

        await ReceivedEdit(42, add: new[] { Liked }, remove: Array.Empty<string>());
    }

    [Fact]
    public async Task Flipping_a_verdict_drops_the_opposite_tag()
    {
        Owned(Album, 42);
        PlexAlbum(42, Album, "80s", Liked);

        await _sut.SetTags(Artist, Album, Disliked, new[] { Liked });

        await ReceivedEdit(42, add: new[] { Disliked }, remove: new[] { Liked });
    }

    /// <summary>
    /// The case the record-level match exists for. Plex names an album from its own metadata and drops
    /// the edition decoration, so a compilation queued as "Now That's What I Call Music! (Deluxe
    /// Edition)" is on the shelf as "Now That's What I Call Music!" — an exact-title lookup would never
    /// find it and the verdict would silently never land.
    /// </summary>
    [Fact]
    public async Task Finds_the_album_under_the_title_plex_gave_it()
    {
        Owned("Now That's What I Call Music!", 42);
        PlexAlbum(42, "Now That's What I Call Music!");

        await _sut.SetTags(
            Artist, "Now That's What I Call Music! (Deluxe Edition)", Liked, Array.Empty<string>());

        await ReceivedEdit(42, add: new[] { Liked }, remove: Array.Empty<string>());
    }

    /// <summary>
    /// The rename no rule can reach — Deezer's "DOOM (Original Game Soundtrack)" against Plex's "Doom:
    /// Original Game Soundtrack" — which is what a recorded merge is for. The purchase reconcile has
    /// always honoured those; if the tagger didn't, a merged collection would close out on the buy list
    /// and still never reach a playlist.
    /// </summary>
    [Fact]
    public async Task Honours_a_recorded_merge()
    {
        Owned("Doom: Original Game Soundtrack", 42);
        PlexAlbum(42, "Doom: Original Game Soundtrack");
        _overrides.GetAll().Returns(new[]
        {
            new AlbumMatchOverride(Artist, "DOOM (Original Game Soundtrack)", "Doom: Original Game Soundtrack"),
        });

        await _sut.SetTags(Artist, "DOOM (Original Game Soundtrack)", Liked, Array.Empty<string>());

        await ReceivedEdit(42, add: new[] { Liked }, remove: Array.Empty<string>());
    }

    [Fact]
    public async Task Writes_nothing_when_the_tag_is_already_there()
    {
        Owned(Album, 42);
        PlexAlbum(42, Album, Liked);

        await _sut.SetTags(Artist, Album, Liked, new[] { Disliked });

        await _plex.DidNotReceive().SetAlbumMoods(
            Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    /// <summary>
    /// The normal case for a fresh collection: rated before it has been downloaded. Not an error, and
    /// emphatically not a reason to sweep the library — the backfill re-stamps it on arrival.
    /// </summary>
    [Fact]
    public async Task Does_nothing_when_the_library_does_not_hold_the_album()
    {
        await _sut.SetTags(Artist, Album, Liked, Array.Empty<string>());

        await _plex.DidNotReceive().GetMusicAlbum(Arg.Any<int>());
        await _plex.DidNotReceive().SetAlbumMoods(
            Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task Skips_a_stale_rating_key()
    {
        Owned(Album, 42);
        _plex.GetMusicAlbum(42).Returns((PlexMusicAlbum?)null);

        await _sut.SetTags(Artist, Album, Liked, Array.Empty<string>());

        await _plex.DidNotReceive().SetAlbumMoods(
            Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    /// <summary>Tagging is a side effect of rating: a Plex failure must never surface as a failed thumb.</summary>
    [Fact]
    public async Task Never_throws()
    {
        Owned(Album, 42);
        _plex.GetMusicAlbum(42).Returns<PlexMusicAlbum?>(_ => throw new HttpRequestException("boom"));

        var act = async () => await _sut.SetTags(Artist, Album, Liked, Array.Empty<string>());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Ignores_a_call_with_nothing_to_do()
    {
        await _sut.SetTags(Artist, Album, add: null, remove: Array.Empty<string>());
        await _sut.SetTags(Artist, "   ", Liked, Array.Empty<string>());

        await _catalog.DidNotReceive().GetAlbumPlexRatingKeys(Arg.Any<IReadOnlyCollection<string>>());
    }
}
