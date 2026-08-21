using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

public class PlexArtistTaggerTests
{
    private const string Artist = "Radiohead";
    private const string Liked = "noggog_liked";
    private const string Disliked = "noggog_disliked";

    private readonly IPlexApi _plex = Substitute.For<IPlexApi>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly PlexArtistTagger _sut;

    public PlexArtistTaggerTests()
    {
        _sut = new PlexArtistTagger(_plex, _catalog, NullLogger<PlexArtistTagger>.Instance);
        _plex.ResolveLibrary().Returns(new PlexLibrary { Key = 1, Title = "Music", Type = "artist" });
        // Default: artist not in catalog (no stored keys) and an empty library — individual tests override.
        _catalog.GetPlexRatingKeys(Arg.Any<ArtistKey>()).Returns(Array.Empty<int>());
        _plex.GetMusicArtists(Arg.Any<int>()).Returns(Array.Empty<PlexMusicArtist>());
    }

    private static PlexMusicArtist ArtistItem(int ratingKey, string title, params string[] moods) =>
        new()
        {
            RatingKey = ratingKey,
            Title = title,
            Mood = moods.Select(l => new PlexTag { Tag = l }).ToArray(),
        };

    private void StoredKeys(params int[] keys) =>
        _catalog.GetPlexRatingKeys(new ArtistKey(Artist)).Returns(keys);

    /// <summary>Asserts exactly one edit of item <paramref name="ratingKey"/> with the given add/remove delta.</summary>
    private Task ReceivedEdit(int ratingKey, string[] add, string[] remove) =>
        _plex.Received(1).SetArtistMoods(1, ratingKey,
            Arg.Is<IReadOnlyCollection<string>>(a => a.SequenceEqual(add)),
            Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(remove)));

    // --- ReconcileMoods (pure) ------------------------------------------------------------------

    [Fact]
    public void Reconcile_adds_when_absent_and_preserves_others()
    {
        // "ambient" stands in for a hand-applied mood an existing smart collection filters on — a verdict
        // write must never disturb it.
        PlexArtistTagger.ReconcileMoods(new[] { "ambient" }, Liked, Array.Empty<string>())
            .Should().Equal("ambient", Liked);
    }

    [Fact]
    public void Reconcile_removes_case_insensitively_and_preserves_others()
    {
        PlexArtistTagger.ReconcileMoods(new[] { "rock", "NOGGOG_LIKED" }, null, new[] { Liked })
            .Should().Equal("rock");
    }

    [Fact]
    public void Reconcile_flip_drops_opposite_and_adds_new()
    {
        PlexArtistTagger.ReconcileMoods(new[] { "rock", Liked }, Disliked, new[] { Liked })
            .Should().Equal("rock", Disliked);
    }

    [Fact]
    public void Reconcile_returns_null_when_already_in_desired_state()
    {
        // Add already present, nothing to remove.
        PlexArtistTagger.ReconcileMoods(new[] { "rock", Liked }, Liked, new[] { Disliked })
            .Should().BeNull();
        // Add present (case-insensitive), remove tag absent.
        PlexArtistTagger.ReconcileMoods(new[] { "NOGGOG_LIKED" }, Liked, new[] { Disliked })
            .Should().BeNull();
    }

    // --- SetTags orchestration -----------------------------------------------------------------

    [Fact]
    public async Task Stored_key_fast_path_never_scans_the_library()
    {
        StoredKeys(5);
        _plex.GetMusicArtist(5).Returns(ArtistItem(5, Artist, "rock"));

        await _sut.SetTags(Artist, Liked, Array.Empty<string>());

        await ReceivedEdit(5, add: new[] { Liked }, remove: Array.Empty<string>());
        await _plex.DidNotReceive().GetMusicArtists(Arg.Any<int>());
    }

    [Fact]
    public async Task Flip_strips_opposite_and_stamps_new_verdict()
    {
        StoredKeys(5);
        _plex.GetMusicArtist(5).Returns(ArtistItem(5, Artist, Liked));

        await _sut.SetTags(Artist, Disliked, new[] { Liked });

        await ReceivedEdit(5, add: new[] { Disliked }, remove: new[] { Liked });
    }

    [Fact]
    public async Task Flip_removes_stale_tag_with_its_stored_casing()
    {
        // A pre-existing tag whose case differs from the app's generated form must still be dropped,
        // spelled exactly as Plex stores it ("Noggog_liked", not the lowercase "noggog_liked").
        StoredKeys(5);
        _plex.GetMusicArtist(5).Returns(ArtistItem(5, Artist, "Noggog_liked"));

        await _sut.SetTags(Artist, Disliked, new[] { Liked });

        await ReceivedEdit(5, add: new[] { Disliked }, remove: new[] { "Noggog_liked" });
    }

    [Fact]
    public async Task Clear_strips_both_verdict_tags()
    {
        StoredKeys(5);
        _plex.GetMusicArtist(5).Returns(ArtistItem(5, Artist, "rock", Liked));

        await _sut.SetTags(Artist, add: null, remove: new[] { Liked, Disliked });

        await ReceivedEdit(5, add: Array.Empty<string>(), remove: new[] { Liked });
    }

    [Fact]
    public async Task No_op_when_already_in_desired_state_writes_nothing()
    {
        StoredKeys(5);
        _plex.GetMusicArtist(5).Returns(ArtistItem(5, Artist, "rock", Liked));

        await _sut.SetTags(Artist, Liked, new[] { Disliked });

        await _plex.DidNotReceive().SetArtistMoods(
            Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task Multi_key_collaborators_tag_every_item()
    {
        StoredKeys(5, 9);
        _plex.GetMusicArtist(5).Returns(ArtistItem(5, Artist, "rock"));
        _plex.GetMusicArtist(9).Returns(ArtistItem(9, $"{Artist};Other", "pop"));

        await _sut.SetTags(Artist, Liked, Array.Empty<string>());

        await ReceivedEdit(5, add: new[] { Liked }, remove: Array.Empty<string>());
        await ReceivedEdit(9, add: new[] { Liked }, remove: Array.Empty<string>());
    }

    [Fact]
    public async Task Cold_cache_falls_back_to_the_name_scan()
    {
        // No stored keys (default) → scan path.
        _plex.GetMusicArtists(1).Returns(new[] { ArtistItem(7, Artist, "rock") });

        await _sut.SetTags(Artist, Liked, Array.Empty<string>());

        await _plex.Received(1).GetMusicArtists(1);
        await ReceivedEdit(7, add: new[] { Liked }, remove: Array.Empty<string>());
        await _plex.DidNotReceive().GetMusicArtist(Arg.Any<int>());
    }

    [Fact]
    public async Task Stale_key_falls_back_to_the_name_scan()
    {
        StoredKeys(5);
        _plex.GetMusicArtist(5).Returns((PlexMusicArtist?)null); // key no longer resolves
        _plex.GetMusicArtists(1).Returns(new[] { ArtistItem(7, Artist, "rock") });

        await _sut.SetTags(Artist, Liked, Array.Empty<string>());

        await _plex.Received(1).GetMusicArtists(1);
        await ReceivedEdit(7, add: new[] { Liked }, remove: Array.Empty<string>());
    }

    [Fact]
    public async Task Best_effort_does_not_rethrow()
    {
        StoredKeys(5);
        _plex.GetMusicArtist(5).Returns<PlexMusicArtist?>(_ => throw new InvalidOperationException("plex down"));

        var act = async () => await _sut.SetTags(Artist, Liked, Array.Empty<string>());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Blank_input_short_circuits_without_touching_plex_or_catalog()
    {
        await _sut.SetTags("   ", add: null, remove: Array.Empty<string>());

        await _catalog.DidNotReceive().GetPlexRatingKeys(Arg.Any<ArtistKey>());
        await _plex.DidNotReceive().ResolveLibrary();
    }
}
