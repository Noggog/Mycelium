using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Background;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Deezer.Models;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// Collections are the app's one path to a record no discography lists. Everything worth asserting
/// here is about what a thumb <em>writes</em>: the global row that carries the Deezer id to the
/// downloader (without it a liked compilation sits on the buy list for ever with nothing to fetch),
/// the per-user verdict, and the album mood — which must be stamped for an umbrella credit and, just
/// as importantly, must not be for an ordinary album whose artist already carries one.
/// </summary>
public class CollectionServiceTests
{
    private const string User = "user-1";
    private const string Username = "noggog";
    private const string Liked = "noggog_liked";
    private const string Disliked = "noggog_disliked";

    private readonly IDeezerApi _deezer = Substitute.For<IDeezerApi>();
    private readonly IMissingAlbumRepo _missing = Substitute.For<IMissingAlbumRepo>();
    private readonly IUserAlbumRatingRepo _ratings = Substitute.For<IUserAlbumRatingRepo>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly IAlbumMatchOverrideRepo _overrides = Substitute.For<IAlbumMatchOverrideRepo>();
    private readonly IPlexApi _plex = Substitute.For<IPlexApi>();
    private readonly IAlbumTagFollowUp _followUps = Substitute.For<IAlbumTagFollowUp>();
    private readonly CollectionService _sut;

    public CollectionServiceTests()
    {
        _sut = new CollectionService(
            _deezer, _missing, _ratings, _catalog, _overrides, _plex, _followUps,
            NullLogger<CollectionService>.Instance);

        _ratings.GetRated(User).Returns(Array.Empty<AlbumRating>());
        _missing.GetAll().Returns(Array.Empty<MissingAlbum>());
        _catalog.GetAllPresent().Returns(Array.Empty<CatalogArtist>());
        _catalog.GetOwnedAlbums()
            .Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase));
        _catalog.GetAlbumPlexRatingKeys(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase));
        _overrides.GetAll().Returns(Array.Empty<AlbumMatchOverride>());
        _plex.GetMachineIdentifier().Returns((string?)null);
    }

    private static DeezerAlbum Album(long id, string title, string artist, string recordType = "album") =>
        new()
        {
            id = id,
            title = title,
            record_type = recordType,
            nb_tracks = 10,
            release_date = "1985-02-19",
            artist = new DeezerArtist { name = artist },
        };

    private void Present(params string[] artists) =>
        _catalog.GetAllPresent().Returns(artists
            .Select(a => new CatalogArtist(new ArtistKey(a), null, DateTimeOffset.UtcNow))
            .ToArray());

    private void Owns(string artist, params string[] titles) =>
        _catalog.GetOwnedAlbums().Returns(
            new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
            {
                [artist] = titles.ToDictionary(t => t, _ => (AudioQuality?)null, StringComparer.OrdinalIgnoreCase),
            });

    // --- Rate ------------------------------------------------------------------------------------

    /// <summary>
    /// The row in the global missing-album store is the load-bearing part: the purchase reconcile reads
    /// the Deezer id out of it, so a like that skipped it would queue a compilation the downloader can
    /// never fetch.
    /// </summary>
    [Fact]
    public async Task Liking_records_the_deezer_id_the_downloader_needs()
    {
        _deezer.GetAlbum(246803).Returns(Album(246803, "The Breakfast Club", "Various Artists"));

        await _sut.Rate(User, Username, 246803, DiscoveryStatus.Liked);

        await _missing.Received(1).Upsert(Arg.Is<MissingAlbum>(m =>
            m.DeezerAlbumId == 246803
            && m.Album.AlbumName == "The Breakfast Club"
            && m.Artist.ArtistName == "Various Artists"
            // Same act on both sides: there is no discography this was reached through, so nothing for
            // the album-artist to differ from.
            && m.MatchArtist.ArtistName == "Various Artists"
            && m.Year == 1985));
        await _ratings.Received(1).Rate(
            User, "Various Artists", "The Breakfast Club", Arg.Any<string?>(), DiscoveryStatus.Liked);
    }

    /// <summary>
    /// The additive write matters: every collection anyone adds files under the same handful of
    /// umbrella acts, so replacing the act's rows would delete its neighbours' Deezer ids.
    /// </summary>
    [Fact]
    public async Task Liking_never_replaces_the_umbrella_acts_other_rows()
    {
        _deezer.GetAlbum(246803).Returns(Album(246803, "The Breakfast Club", "Various Artists"));

        await _sut.Rate(User, Username, 246803, DiscoveryStatus.Liked);

        await _missing.DidNotReceive().ReplaceForArtist(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<MissingAlbum>>());
    }

    [Fact]
    public async Task Liking_a_collection_queues_the_album_mood_and_strips_the_opposite()
    {
        _deezer.GetAlbum(246803).Returns(Album(246803, "The Breakfast Club", "Various Artists"));

        await _sut.Rate(User, Username, 246803, DiscoveryStatus.Liked);

        _followUps.Received(1).QueueAlbumTagWrite(
            "Various Artists", "The Breakfast Club", Liked,
            Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(new[] { Disliked })));
    }

    /// <summary>
    /// The line the whole design turns on. An ordinary album's verdict is already carried by its
    /// artist; stamping the record too would put single albums by acts the user thumbed <em>down</em>
    /// into a "My Library" playlist.
    /// </summary>
    [Fact]
    public async Task Liking_an_ordinary_album_does_not_touch_the_album_mood()
    {
        _deezer.GetAlbum(999).Returns(Album(999, "Dragon New Warm Mountain", "Big Thief"));

        await _sut.Rate(User, Username, 999, DiscoveryStatus.Liked);

        _followUps.DidNotReceive().QueueAlbumTagWrite(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task Clearing_a_verdict_strips_both_tags()
    {
        _sut.QueueTagWrite(Username, "Various Artists", "The Breakfast Club", status: null);

        _followUps.Received(1).QueueAlbumTagWrite(
            "Various Artists", "The Breakfast Club", null,
            Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(new[] { Liked, Disliked })));
    }

    /// <summary>A snooze is a deferred decision, not a verdict — the artist path never tags one either.</summary>
    [Fact]
    public void Snoozing_writes_no_verdict_tag()
    {
        _sut.QueueTagWrite(Username, "Various Artists", "The Breakfast Club", DiscoveryStatus.Snoozed);

        _followUps.Received(1).QueueAlbumTagWrite(
            "Various Artists", "The Breakfast Club", null,
            Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(new[] { Liked, Disliked })));
    }

    [Fact]
    public async Task Rating_an_album_deezer_does_not_know_writes_nothing()
    {
        _deezer.GetAlbum(1).Returns((DeezerAlbum?)null);

        (await _sut.Rate(User, Username, 1, DiscoveryStatus.Liked)).Should().BeNull();

        await _missing.DidNotReceive().Upsert(Arg.Any<MissingAlbum>());
        await _ratings.DidNotReceive().Rate(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<DiscoveryStatus>());
    }

    // --- Search ----------------------------------------------------------------------------------

    [Fact]
    public async Task Search_puts_umbrella_credits_first_and_drops_singles()
    {
        _deezer.SearchAlbums(Arg.Any<string>(), Arg.Any<int>()).Returns(new[]
        {
            Album(1, "Breakfast Club", "Breakfast Club"),
            Album(2, "Breakfast Club", "SVEA", recordType: "single"),
            Album(3, "The Breakfast Club", "Various Artists"),
        });

        var results = await _sut.Search(User, "breakfast club");

        results.Select(r => r.DeezerAlbumId).Should().Equal(3, 1);
        results[0].Umbrella.Should().BeTrue();
        results[1].Umbrella.Should().BeFalse();
    }

    /// <summary>
    /// An unanswered Deezer call must not read as "no such record": the client caches what it is told,
    /// so a rate-limit blip would pin an empty result on screen until a hard reload.
    /// </summary>
    [Fact]
    public async Task Search_surfaces_a_deezer_outage_rather_than_an_empty_result()
    {
        _deezer.SearchAlbums(Arg.Any<string>(), Arg.Any<int>()).Returns((DeezerAlbum[]?)null);

        await _sut.Invoking(s => s.Search(User, "breakfast club"))
            .Should().ThrowAsync<DeezerUnavailableException>();
    }

    [Fact]
    public async Task Search_marks_a_record_the_library_already_holds()
    {
        Owns("Various Artists", "The Breakfast Club");
        _deezer.SearchAlbums(Arg.Any<string>(), Arg.Any<int>())
            .Returns(new[] { Album(3, "The Breakfast Club", "Various Artists") });

        var results = await _sut.Search(User, "breakfast club");

        results.Single().Owned.Should().BeTrue();
    }

    // --- List ------------------------------------------------------------------------------------

    /// <summary>
    /// The reason the list exists rather than just the search. A compilation on the shelf is invisible
    /// to the rest of the app — no artist page lists it, no feed offers it — so without this there is no
    /// way to say you like something you already own, and it could never reach a "My Library" playlist.
    /// </summary>
    [Fact]
    public async Task List_offers_owned_collections_that_have_never_been_rated()
    {
        Present("Various Artists", "Big Thief");
        Owns("Various Artists", "The Breakfast Club");

        var items = await _sut.List(User);

        var item = items.Single();
        item.Title.Should().Be("The Breakfast Club");
        item.Owned.Should().BeTrue();
        item.Verdict.Should().BeNull();
        // Never came through this app, so there is no Deezer id — and nothing to download either.
        item.DeezerAlbumId.Should().Be(0);
    }

    [Fact]
    public async Task List_includes_rated_collections_the_library_does_not_have_yet()
    {
        _ratings.GetRated(User).Returns(new[]
        {
            new AlbumRating(
                new ArtistKey("Various Artists"), new AlbumKey("The Breakfast Club"), null,
                DiscoveryStatus.Liked),
            // An ordinary album rating belongs to its artist, not here.
            new AlbumRating(
                new ArtistKey("Big Thief"), new AlbumKey("Dragon New Warm Mountain"), null,
                DiscoveryStatus.Liked),
        });
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(
                new ArtistKey("Various Artists"), new AlbumKey("The Breakfast Club"), null, 246803),
        });

        var items = await _sut.List(User);

        var item = items.Single();
        item.Verdict.Should().Be(DiscoveryStatus.Liked);
        item.Owned.Should().BeFalse();
        // Recovered from the global row, which is what makes it downloadable and linkable.
        item.DeezerAlbumId.Should().Be(246803);
    }
}
