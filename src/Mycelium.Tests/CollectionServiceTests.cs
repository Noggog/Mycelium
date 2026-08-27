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
/// the per-user verdict, and the mood.
///
/// <para>The mood is where the care is. Through <see cref="CollectionService.Rate"/> — the id-keyed
/// path external automation uses — it lands on the <em>album</em> for an umbrella credit and, on a
/// <em>like</em> only, on the <em>artist</em> for anything else, so a record acquired there is never
/// left with nothing written to Plex at all. A dislike acquires nothing, so it repairs nothing and
/// leaves the act alone. Through <see cref="CollectionService.QueueTagWrite"/>, which the UI's rating
/// endpoints call, an ordinary album writes nothing at all: the act's mood is the user's own verdict
/// and a thumb on one record must not move it. Neither path ever records an artist <em>verdict</em> —
/// liking one album is not liking the act.</para>
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
    private readonly IArtistTagFollowUp _artistFollowUps = Substitute.For<IArtistTagFollowUp>();
    private readonly CollectionService _sut;

    public CollectionServiceTests()
    {
        _sut = new CollectionService(
            _deezer, _missing, _ratings, _catalog, _overrides, _plex, _followUps, _artistFollowUps,
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
    /// The umbrella act itself must stay clean. "Various Artists" liked would claim every compilation
    /// in the library at once — which is exactly why a collection's verdict goes on the record.
    /// </summary>
    [Fact]
    public async Task Liking_a_collection_leaves_the_umbrella_act_untagged()
    {
        _deezer.GetAlbum(246803).Returns(Album(246803, "The Breakfast Club", "Various Artists"));

        await _sut.Rate(User, Username, 246803, DiscoveryStatus.Liked);

        _artistFollowUps.DidNotReceive().QueueArtistTagWrite(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    /// <summary>
    /// The line the whole design turns on. An ordinary album's mood belongs on its artist; stamping the
    /// record too would put single albums by acts the user thumbed <em>down</em> into a "My Library"
    /// playlist.
    /// </summary>
    [Fact]
    public async Task Liking_an_ordinary_album_does_not_touch_the_album_mood()
    {
        _deezer.GetAlbum(999).Returns(Album(999, "Dragon New Warm Mountain", "Big Thief"));

        await _sut.Rate(User, Username, 999, DiscoveryStatus.Liked);

        _followUps.DidNotReceive().QueueAlbumTagWrite(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    /// <summary>
    /// The gap this closes. /api/collections/rate is the only id-keyed way to queue an album, so it is
    /// how an API client rates an ordinary release — and this used to write nothing to Plex at all.
    /// Nothing could repair it either: ArtistTagBackfill re-stamps from <em>artist</em> ratings, and a
    /// thumb on a record leaves none.
    /// </summary>
    [Fact]
    public async Task Liking_an_ordinary_album_tags_its_artist_and_strips_the_opposite()
    {
        _deezer.GetAlbum(999).Returns(Album(999, "Dragon New Warm Mountain", "Big Thief"));

        await _sut.Rate(User, Username, 999, DiscoveryStatus.Liked);

        _artistFollowUps.Received(1).QueueArtistTagWrite(
            "Big Thief", Liked,
            Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(new[] { Disliked })));
    }

    /// <summary>
    /// The other side of the gap, and the reason it is a like and not a verdict that writes the mood.
    /// A thumbs-down acquires nothing, so there is nothing missing from Plex for it to repair — while
    /// stamping it would strip the "&lt;username&gt;_liked" off a band the user likes on the strength
    /// of one bad record, dropping the whole act out of a "My Library" playlist. Automation is exactly
    /// where that would go unnoticed, which is why this endpoint of all of them must not do it.
    /// </summary>
    [Fact]
    public async Task Disliking_an_ordinary_album_leaves_its_artists_mood_alone()
    {
        _deezer.GetAlbum(999).Returns(Album(999, "Dragon New Warm Mountain", "Big Thief"));

        await _sut.Rate(User, Username, 999, DiscoveryStatus.Disliked);

        _artistFollowUps.DidNotReceive().QueueArtistTagWrite(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
        // The album verdict itself is still recorded — this is about the Plex mood, nothing else.
        await _ratings.Received(1).Rate(
            User, "Big Thief", "Dragon New Warm Mountain", Arg.Any<string?>(), DiscoveryStatus.Disliked);
    }

    /// <summary>
    /// The constraint the tag-only seam exists for. A thumb on one record says nothing about the act,
    /// so the only verdict written is the album's: recording an artist one would grow the recommendation
    /// frontier out of someone nobody rated, and put them on the user's Ratings page claiming a thumb
    /// they never gave. Asserted as "the artist seam is asked for a tag and nothing else" — the tag
    /// write is all it can do, which is precisely why the verdict seam isn't the one being used.
    /// </summary>
    [Fact]
    public async Task Liking_an_ordinary_album_rates_the_album_alone()
    {
        _deezer.GetAlbum(999).Returns(Album(999, "Dragon New Warm Mountain", "Big Thief"));

        await _sut.Rate(User, Username, 999, DiscoveryStatus.Liked);

        await _ratings.Received(1).Rate(
            User, "Big Thief", "Dragon New Warm Mountain", Arg.Any<string?>(), DiscoveryStatus.Liked);
        _artistFollowUps.ReceivedCalls().Should().ContainSingle()
            .Which.GetMethodInfo().Name.Should().Be(nameof(IArtistTagFollowUp.QueueArtistTagWrite));
    }

    [Fact]
    public async Task Clearing_a_verdict_strips_both_tags()
    {
        _sut.QueueTagWrite(Username, "Various Artists", "The Breakfast Club", status: null);

        _followUps.Received(1).QueueAlbumTagWrite(
            "Various Artists", "The Breakfast Club", null,
            Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(new[] { Liked, Disliked })));
    }

    // --- The discovery-rate path, which must not touch an artist's mood ---------------------------
    //
    // QueueTagWrite is what /api/discovery/rate (and the clear beside it) calls. Those back a UI that
    // rates artists directly, so the act's mood is the user's own and a verdict on one record is not
    // allowed to move it. Only CollectionService.Rate — the id-keyed path above — does that.

    /// <summary>
    /// The regression this boundary exists to prevent, pinned. Thumbing down one album by a band the
    /// user likes must leave their "&lt;username&gt;_liked" exactly where it is: stripping it would drop
    /// the whole band out of a "My Library" playlist over one bad record, and nothing would put it back
    /// — ArtistTagBackfill only re-stamps artists as they <em>arrive</em> in the library.
    /// </summary>
    [Fact]
    public void Disliking_one_album_through_the_discovery_path_leaves_the_artists_mood_alone()
    {
        _sut.QueueTagWrite(Username, "Big Thief", "Dragon New Warm Mountain", DiscoveryStatus.Disliked);

        _artistFollowUps.DidNotReceive().QueueArtistTagWrite(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    /// <summary>The like direction is refused for the same reason: it isn't this path's tag to write.</summary>
    [Fact]
    public void Liking_one_album_through_the_discovery_path_leaves_the_artists_mood_alone()
    {
        _sut.QueueTagWrite(Username, "Big Thief", "Dragon New Warm Mountain", DiscoveryStatus.Liked);

        _artistFollowUps.DidNotReceive().QueueArtistTagWrite(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
        _followUps.DidNotReceive().QueueAlbumTagWrite(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    /// <summary>
    /// Clearing an ordinary album's verdict is the same boundary from the other side. The artist's mood
    /// belongs to their own verdict, so there is nothing here to undo.
    /// </summary>
    [Fact]
    public void Clearing_an_ordinary_albums_verdict_writes_no_tag()
    {
        _sut.QueueTagWrite(Username, "Big Thief", "Dragon New Warm Mountain", status: null);

        _artistFollowUps.DidNotReceive().QueueArtistTagWrite(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
        _followUps.DidNotReceive().QueueAlbumTagWrite(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    /// <summary>
    /// Both moods need a username to prefix the tag with. Without one there is nothing to write, and
    /// queuing a write with nothing to add or remove would cost a Plex round trip for nothing.
    /// </summary>
    [Fact]
    public async Task A_verdict_with_no_usable_username_queues_no_tag_at_all()
    {
        _deezer.GetAlbum(999).Returns(Album(999, "Dragon New Warm Mountain", "Big Thief"));
        _deezer.GetAlbum(246803).Returns(Album(246803, "The Breakfast Club", "Various Artists"));

        await _sut.Rate(User, username: null, 999, DiscoveryStatus.Liked);
        await _sut.Rate(User, username: null, 246803, DiscoveryStatus.Liked);

        _artistFollowUps.DidNotReceive().QueueArtistTagWrite(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
        _followUps.DidNotReceive().QueueAlbumTagWrite(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
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

    // --- RateMany -------------------------------------------------------------------------------

    /// <summary>
    /// The case the batch endpoint exists to survive. A migration client pastes thirty ids and one of
    /// them doesn't resolve — Deezer answers an id it has never heard of and an id it is rate-limiting
    /// with the same nothing. The two that <em>did</em> resolve must still be written, and the caller
    /// must be told which one didn't and why, or it has to re-read its own ratings to find out.
    /// </summary>
    [Fact]
    public async Task A_batch_records_what_resolved_and_reports_what_didnt()
    {
        _deezer.GetAlbum(246803).Returns(Album(246803, "The Breakfast Club", "Various Artists"));
        _deezer.GetAlbum(999).Returns(Album(999, "Dragon New Warm Mountain", "Big Thief"));
        _deezer.GetAlbum(404).Returns((DeezerAlbum?)null); // Deezer answered: nothing under that id

        var response = await _sut.RateMany(User, Username, new[]
        {
            new CollectionRateItem(246803, "up"),
            new CollectionRateItem(404, "up"),
            new CollectionRateItem(999, "up"),
        });

        response.Total.Should().Be(3);
        response.Succeeded.Should().Be(2);
        response.Failed.Should().Be(1);

        // Order and index track the submission, so the caller can line the answer up against what it sent.
        response.Results.Select(r => r.Index).Should().Equal(0, 1, 2);
        response.Results[0].Ok.Should().BeTrue();
        response.Results[0].Item!.Title.Should().Be("The Breakfast Club");
        response.Results[2].Ok.Should().BeTrue();

        var failure = response.Results[1];
        failure.Ok.Should().BeFalse();
        failure.Id.Should().Be(404);
        failure.Item.Should().BeNull();
        failure.Error.Should().Contain("404", "the caller retries by id, so the reason has to name one");

        // The point of not aborting on the first failure: the item after it was still written.
        await _ratings.Received(1).Rate(
            User, "Big Thief", "Dragon New Warm Mountain", Arg.Any<string?>(), DiscoveryStatus.Liked);
    }

    /// <summary>
    /// A batch goes through the same <c>Rate</c> the single-item route does, so the writes a verdict
    /// implies cannot drift apart between the two paths. Asserted on the tagging in particular, across
    /// all three rules at once: an umbrella credit stamps the record, a liked ordinary album stamps its
    /// artist, and a disliked one stamps nothing. Getting any of those wrong in only one of the two
    /// paths fails nothing at the time it happens.
    /// </summary>
    [Fact]
    public async Task A_batch_tags_each_album_exactly_as_the_single_route_would()
    {
        _deezer.GetAlbum(246803).Returns(Album(246803, "The Breakfast Club", "Various Artists"));
        _deezer.GetAlbum(999).Returns(Album(999, "Dragon New Warm Mountain", "Big Thief"));
        _deezer.GetAlbum(777).Returns(Album(777, "Gemini Rights", "Steve Lacy"));

        await _sut.RateMany(User, Username, new[]
        {
            new CollectionRateItem(246803, "up"),
            new CollectionRateItem(999, "up"),
            new CollectionRateItem(777, "down"),
        });

        // Umbrella credit: the mood goes on the record, and the umbrella act stays clean.
        _followUps.Received(1).QueueAlbumTagWrite(
            "Various Artists", "The Breakfast Club", Liked,
            Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(new[] { Disliked })));
        // Liked ordinary album: the mood goes on the artist instead, never on the record.
        _artistFollowUps.Received(1).QueueArtistTagWrite(
            "Big Thief", Liked,
            Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(new[] { Disliked })));
        _followUps.DidNotReceive().QueueAlbumTagWrite(
            "Big Thief", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
        // Disliked ordinary album: nothing at all. A thumbs-down acquires nothing, so it has no gap to
        // repair, and moving the act's mood would cost a liked band its place in "My Library".
        _artistFollowUps.DidNotReceive().QueueArtistTagWrite(
            "Steve Lacy", Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
        _followUps.DidNotReceive().QueueAlbumTagWrite(
            "Steve Lacy", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    /// <summary>
    /// Over the cap the whole batch is refused and <em>nothing</em> is written. Truncating would be the
    /// worst of both: the caller is told it succeeded and then waits forever on the albums past the cut,
    /// with no way to learn they were never accepted.
    /// </summary>
    [Fact]
    public async Task An_overlong_batch_is_refused_whole_rather_than_truncated()
    {
        _deezer.GetAlbum(Arg.Any<long>()).Returns(Album(1, "Anything", "Various Artists"));
        var tooMany = Enumerable.Range(0, BatchLimits.MaxItems + 1)
            .Select(i => new CollectionRateItem(i, "up"))
            .ToArray();

        var rate = () => _sut.RateMany(User, Username, tooMany);

        (await rate.Should().ThrowAsync<ArgumentException>())
            .Which.Message.Should().Contain(BatchLimits.MaxItems.ToString())
            .And.Contain(tooMany.Length.ToString(), "the caller needs to see by how much it overshot");
        await _ratings.DidNotReceive().Rate(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<DiscoveryStatus>());
    }

    /// <summary>A batch exactly at the cap is accepted — the limit is inclusive.</summary>
    [Fact]
    public async Task A_batch_at_the_cap_is_accepted()
    {
        _deezer.GetAlbum(Arg.Any<long>()).Returns(Album(1, "Anything", "Various Artists"));
        var atCap = Enumerable.Range(0, BatchLimits.MaxItems)
            .Select(i => new CollectionRateItem(i, "up"))
            .ToArray();

        var response = await _sut.RateMany(User, Username, atCap);

        response.Total.Should().Be(BatchLimits.MaxItems);
        response.Failed.Should().Be(0);
    }
}
