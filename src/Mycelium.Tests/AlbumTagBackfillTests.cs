using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The backfill closes the gap between liking a collection and owning it. A compilation is normally
/// rated <em>before</em> it exists in the library — that's the point of the view — so at rating time
/// there is no album to tag, and without this pass the verdict would never reach Plex and a "My
/// Library" playlist would silently omit exactly the records the user went and acquired.
/// </summary>
public class AlbumTagBackfillTests
{
    private const string User = "user-1";
    private const string Umbrella = PlaceholderArtist.VariousArtists;
    private const string Album = "The Breakfast Club";
    private const string Liked = "noggog_liked";
    private const string Disliked = "noggog_disliked";

    private readonly IAlbumTagger _tagger = Substitute.For<IAlbumTagger>();
    private readonly IUserAlbumRatingRepo _ratings = Substitute.For<IUserAlbumRatingRepo>();
    private readonly IUserQueueRepo _queue = Substitute.For<IUserQueueRepo>();
    private readonly IUserRepo _users = Substitute.For<IUserRepo>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly IAlbumMatchOverrideRepo _overrides = Substitute.For<IAlbumMatchOverrideRepo>();
    private readonly AlbumTagBackfill _sut;

    public AlbumTagBackfillTests()
    {
        _sut = new AlbumTagBackfill(
            _tagger, _ratings, _queue, _users, _catalog, _overrides,
            NullLogger<AlbumTagBackfill>.Instance);

        _queue.GetAllUserIds().Returns(new[] { User });
        _users.Get(User).Returns(new AppUser(
            User, "noggog", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        _ratings.GetRated(User).Returns(Array.Empty<AlbumRating>());
        _catalog.GetOwnedAlbums()
            .Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase));
        _overrides.GetAll().Returns(Array.Empty<AlbumMatchOverride>());
    }

    private void Rated(string artist, string album, DiscoveryStatus status) =>
        _ratings.GetRated(User).Returns(new[]
        {
            new AlbumRating(new ArtistKey(artist), new AlbumKey(album), null, status),
        });

    private void Owns(string artist, string title) =>
        _catalog.GetOwnedAlbums().Returns(
            new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
            {
                [artist] = new(StringComparer.OrdinalIgnoreCase) { [title] = null },
            });

    [Fact]
    public async Task Stamps_a_liked_collection_that_has_arrived()
    {
        Rated(Umbrella, Album, DiscoveryStatus.Liked);
        Owns(Umbrella, Album);

        (await _sut.Backfill()).Should().Be(1);

        await _tagger.Received(1).SetTags(Umbrella, Album, Liked,
            Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(new[] { Disliked })));
    }

    /// <summary>
    /// Plex renames what it imports, so the title on the shelf is not the one that was queued. Asked at
    /// literal-title granularity the arrival would never be noticed and the verdict would never land.
    /// </summary>
    [Fact]
    public async Task Recognises_an_arrival_plex_renamed()
    {
        Rated(Umbrella, "Now That's What I Call Music! (Deluxe Edition)", DiscoveryStatus.Liked);
        Owns(Umbrella, "Now That's What I Call Music!");

        (await _sut.Backfill()).Should().Be(1);
    }

    [Fact]
    public async Task Leaves_a_collection_that_has_not_arrived_alone()
    {
        Rated(Umbrella, Album, DiscoveryStatus.Liked);

        (await _sut.Backfill()).Should().Be(0);

        await _tagger.DidNotReceive().SetTags(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    /// <summary>An ordinary album's verdict is carried by its artist; stamping the record too would put
    /// single albums by disliked acts into "My Library".</summary>
    [Fact]
    public async Task Ignores_ordinary_album_ratings()
    {
        Rated("Big Thief", "Dragon New Warm Mountain", DiscoveryStatus.Liked);
        Owns("Big Thief", "Dragon New Warm Mountain");

        (await _sut.Backfill()).Should().Be(0);
    }

    /// <summary>A snooze is a deferred decision, not a verdict — <c>GetRated</c> returns those too.</summary>
    [Fact]
    public async Task Ignores_a_snoozed_collection()
    {
        Rated(Umbrella, Album, DiscoveryStatus.Snoozed);
        Owns(Umbrella, Album);

        (await _sut.Backfill()).Should().Be(0);
    }

    [Fact]
    public async Task Stamps_a_dislike_and_strips_the_like()
    {
        Rated(Umbrella, Album, DiscoveryStatus.Disliked);
        Owns(Umbrella, Album);

        await _sut.Backfill();

        await _tagger.Received(1).SetTags(Umbrella, Album, Disliked,
            Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(new[] { Liked })));
    }

    [Fact]
    public async Task Skips_a_user_with_no_usable_username()
    {
        _users.Get(User).Returns((AppUser?)null);
        Rated(Umbrella, Album, DiscoveryStatus.Liked);
        Owns(Umbrella, Album);

        (await _sut.Backfill()).Should().Be(0);
    }
}
