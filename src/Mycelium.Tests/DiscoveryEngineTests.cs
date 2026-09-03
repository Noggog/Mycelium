using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mycelium.Backend;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Deezer.Models;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

public class DiscoveryEngineTests
{
    private const string User = "user-1";

    private readonly IUserQueueRepo _queue = Substitute.For<IUserQueueRepo>();
    private readonly IRelatedArtistReader _related = Substitute.For<IRelatedArtistReader>();
    private readonly ILibraryProvider _library = Substitute.For<ILibraryProvider>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly IMissingAlbumRepo _missing = Substitute.For<IMissingAlbumRepo>();
    private readonly IUserRepo _users = Substitute.For<IUserRepo>();
    private readonly IAlbumMatchOverrideRepo _overrides = Substitute.For<IAlbumMatchOverrideRepo>();
    private readonly FakeDeezerAlbumArtistRepo _albumArtists = new();
    private readonly IUserAlbumRatingRepo _albumRatings = Substitute.For<IUserAlbumRatingRepo>();
    private readonly IAlbumBlockRepo _blocks = Substitute.For<IAlbumBlockRepo>();
    private readonly IDeezerApi _deezer = Substitute.For<IDeezerApi>();
    private readonly DiscoveryEngine _sut;

    public DiscoveryEngineTests()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var resolver = new DeezerArtistResolver(_deezer, cache, _catalog);
        _overrides.GetAll().Returns(Array.Empty<AlbumMatchOverride>());
        _users.GetAll().Returns(Array.Empty<AppUser>());
        var refresher = new MissingAlbumRefresher(
            _catalog, resolver, _deezer, _missing, _overrides, _albumArtists,
            // A lossless ceiling: these cases are about which albums are missing, and an owned album
            // with no recorded quality is never upgradeable whatever the ceiling is.
            new UserQualityService(_users, AudioQuality.Lossless),
            NullLogger<MissingAlbumRefresher>.Instance);
        _sut = new DiscoveryEngine(
            _queue, _related, _library, _catalog, _missing, _albumRatings, _blocks, refresher,
            new UserQualityService(_users, AudioQuality.Lossless),
            // The production defaults. Only the thresholds matter here, and only for splitting the
            // flagged Indifferent rows into their two feed sections.
            new ReconsiderPolicy(
                MinAverage: 3, MaxAverage: 2, MinRatedFraction: 1.0 / 3,
                Interval: TimeSpan.FromDays(7), StartupDelay: TimeSpan.Zero),
            NullLogger<DiscoveryEngine>.Instance);

        // Sensible empty defaults; individual tests override what they need.
        _queue.GetLikedArtistNames(User).Returns(Array.Empty<string>());
        _library.GetAllArtistMetadata().Returns(Array.Empty<ArtistMetadata>());
        _queue.GetDecidedArtists(User).Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        _queue.GetReconsiderable(User, Arg.Any<DiscoveryStatus>()).Returns(Array.Empty<ReconsiderCandidate>());
        _queue.CountPending(User).Returns(0);
        _queue.GetPending(User, Arg.Any<int>(), Arg.Any<int>())
            .Returns(new DiscoveryPage(Array.Empty<DiscoveryCandidate>(), 0, 20, 0));
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase));
        _missing.GetAll().Returns(Array.Empty<MissingAlbum>());
        _albumRatings.GetDecidedKeys(User).Returns(new HashSet<string>());
        _blocks.GetAll().Returns(Array.Empty<AlbumBlock>());
    }

    private void Relates(string artist, params (string name, string? image, int sources)[] related)
    {
        var list = related
            .Select(r => new UnifiedRelatedArtist(
                new ArtistKey(r.name), r.image, Enumerable.Repeat("deezer", r.sources).ToArray()))
            .ToArray();
        // Match any forceRefresh/readOnly so both the fetch paths (TopUp/Rebuild/RateArtist) and the
        // readOnly feed paths (EnsureQueue/OwnedRecommendedByLiked) resolve to the same stub.
        _related.GetRelated(new ArtistKey(artist), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new UnifiedRelations(new ArtistKey(artist), list));
    }

    private static IReadOnlyList<DiscoveryCandidate> Captured(IUserQueueRepo queue)
    {
        var calls = queue.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IUserQueueRepo.UpsertCandidates))
            .ToList();
        calls.Should().NotBeEmpty("the engine should have upserted candidates");
        return (IReadOnlyList<DiscoveryCandidate>)calls.Last().GetArguments()[1]!;
    }

    private Task<DiscoveryFeedPage> Recommended() => _sut.GetFeed(User, FeedKind.RecommendedArtist, 0, 20);

    [Fact]
    public async Task Empty_queue_builds_from_liked_artists_one_step_out()
    {
        _queue.GetLikedArtistNames(User).Returns(new[] { "boygenius" });
        Relates("boygenius", ("Phoebe Bridgers", "pb-img", 1), ("Snail Mail", null, 1));

        await Recommended();

        var upserted = Captured(_queue);
        upserted.Select(c => c.Artist.ArtistName).Should().BeEquivalentTo("Phoebe Bridgers", "Snail Mail");
        upserted.Single(c => c.Artist.ArtistName == "Phoebe Bridgers").Sources.Should().Equal("boygenius");
    }

    [Fact]
    public async Task Existing_queue_is_not_rebuilt()
    {
        _queue.CountPending(User).Returns(3);

        await Recommended();

        await _related.DidNotReceive().GetRelated(Arg.Any<ArtistKey>(), Arg.Any<bool>(), Arg.Any<bool>());
        await _queue.DidNotReceive().UpsertCandidates(Arg.Any<string>(), Arg.Any<IReadOnlyList<DiscoveryCandidate>>());
    }

    [Fact]
    public async Task Expansion_excludes_library_and_decided_artists()
    {
        _queue.GetLikedArtistNames(User).Returns(new[] { "boygenius" });
        _library.GetAllArtistMetadata().Returns(new[] { new ArtistMetadata(new ArtistKey("Big Thief"), null) });
        _queue.GetDecidedArtists(User).Returns(new HashSet<string>(new[] { "Alex G" }, StringComparer.OrdinalIgnoreCase));
        Relates("boygenius",
            ("Big Thief", null, 1),       // already in library -> excluded
            ("Alex G", null, 1),          // already decided -> excluded
            ("boygenius", null, 1),       // the frontier artist itself -> excluded
            ("Phoebe Bridgers", null, 1)); // the one genuinely new candidate

        await Recommended();

        Captured(_queue).Select(c => c.Artist.ArtistName).Should().Equal("Phoebe Bridgers");
    }

    [Fact]
    public async Task Candidate_recommended_by_multiple_liked_artists_accrues_score_and_provenance()
    {
        _queue.GetLikedArtistNames(User).Returns(new[] { "boygenius", "Snail Mail" });
        Relates("boygenius", ("Phoebe Bridgers", "img", 1));
        Relates("Snail Mail", ("Phoebe Bridgers", null, 1));

        await Recommended();

        var pb = Captured(_queue).Single(c => c.Artist.ArtistName == "Phoebe Bridgers");
        pb.Sources.Should().BeEquivalentTo("boygenius", "Snail Mail");
        pb.Score.Should().BeGreaterThan(2.0); // two frontier artists, each ≥1 point
        pb.ImageUrl.Should().Be("img");        // image carried from whichever sighting had one
    }

    [Fact]
    public async Task Liking_an_artist_records_verdict_then_grows_the_frontier_from_it()
    {
        _queue.Rate(User, "Phoebe Bridgers", DiscoveryStatus.Liked, null)
            .Returns(new DiscoveryCandidate(new ArtistKey("Phoebe Bridgers"), null, 3, new[] { "boygenius" }, 1));
        Relates("Phoebe Bridgers", ("Better Oblivion", null, 1));

        await _sut.RateArtist(User, "Phoebe Bridgers", DiscoveryStatus.Liked);

        await _queue.Received(1).Rate(User, "Phoebe Bridgers", DiscoveryStatus.Liked, null);
        var upserted = Captured(_queue);
        upserted.Select(c => c.Artist.ArtistName).Should().Equal("Better Oblivion");
        upserted.Single().Depth.Should().Be(2); // liked depth (1) + 1
    }

    [Fact]
    public async Task Disliking_an_artist_records_verdict_prunes_its_recommendations_and_does_not_expand()
    {
        await _sut.RateArtist(User, "Phoebe Bridgers", DiscoveryStatus.Disliked);

        await _queue.Received(1).Rate(User, "Phoebe Bridgers", DiscoveryStatus.Disliked, null);
        // Dislike takes the artist out of the frontier, so its seeded recommendations are pruned…
        await _queue.Received(1).PruneBySource(User, "Phoebe Bridgers");
        // …but it never grows the frontier or rebuilds the queue.
        await _related.DidNotReceive().GetRelated(Arg.Any<ArtistKey>(), Arg.Any<bool>(), Arg.Any<bool>());
        await _queue.DidNotReceive().UpsertCandidates(Arg.Any<string>(), Arg.Any<IReadOnlyList<DiscoveryCandidate>>());
    }

    [Fact]
    public async Task Liking_an_artist_does_not_prune()
    {
        _queue.Rate(User, "Phoebe Bridgers", DiscoveryStatus.Liked, null)
            .Returns(new DiscoveryCandidate(new ArtistKey("Phoebe Bridgers"), null, 3, new[] { "boygenius" }, 1));
        Relates("Phoebe Bridgers", ("Better Oblivion", null, 1));

        await _sut.RateArtist(User, "Phoebe Bridgers", DiscoveryStatus.Liked);

        await _queue.DidNotReceive().PruneBySource(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Recording_a_verdict_persists_it_without_touching_the_source_apis()
    {
        _queue.Rate(User, "Phoebe Bridgers", DiscoveryStatus.Liked, null)
            .Returns(new DiscoveryCandidate(new ArtistKey("Phoebe Bridgers"), null, 3, new[] { "boygenius" }, 1));
        Relates("Phoebe Bridgers", ("Better Oblivion", null, 1));

        var depth = await _sut.RecordArtistVerdict(User, "Phoebe Bridgers", DiscoveryStatus.Liked);

        // The half a request runs: the verdict lands and the expansion depth comes back for the worker…
        await _queue.Received(1).Rate(User, "Phoebe Bridgers", DiscoveryStatus.Liked, null);
        depth.Should().Be(2); // liked depth (1) + 1
        // …with none of the slow half — that's what kept a click waiting on Deezer/MusicBrainz.
        await _related.DidNotReceive().GetRelated(Arg.Any<ArtistKey>(), Arg.Any<bool>(), Arg.Any<bool>());
        await _queue.DidNotReceive().UpsertCandidates(Arg.Any<string>(), Arg.Any<IReadOnlyList<DiscoveryCandidate>>());
    }

    [Fact]
    public async Task Deferred_follow_up_expands_a_like_at_the_depth_it_was_recorded_with()
    {
        Relates("Phoebe Bridgers", ("Better Oblivion", null, 1));

        await _sut.ApplyVerdictFollowUp(User, "Phoebe Bridgers", DiscoveryStatus.Liked, depth: 2);

        Captured(_queue).Single().Depth.Should().Be(2);
    }

    [Fact]
    public async Task Deferred_follow_up_for_a_cleared_verdict_prunes_like_a_dislike()
    {
        await _sut.ApplyVerdictFollowUp(User, "boygenius", status: null, depth: 0);

        await _queue.Received(1).PruneBySource(User, "boygenius");
        await _queue.DidNotReceive().UpsertCandidates(Arg.Any<string>(), Arg.Any<IReadOnlyList<DiscoveryCandidate>>());
    }

    [Fact]
    public async Task Clearing_an_artist_rating_prunes_the_recommendations_it_seeded()
    {
        await _sut.ClearArtistRating(User, "boygenius");

        await _queue.Received(1).ClearVerdict(User, "boygenius");
        // Un-liking drops the artist from the frontier, so the recommendations it seeded are pruned too.
        await _queue.Received(1).PruneBySource(User, "boygenius");
    }

    [Fact]
    public async Task Snoozing_an_artist_records_a_snooze_and_does_not_expand()
    {
        await _sut.SnoozeArtist(User, "Phoebe Bridgers", TimeSpan.FromDays(7));

        // Snooze writes a future snoozeUntil and never grows the frontier (it's "not now", not "yes").
        await _queue.Received(1).Snooze(
            User, "Phoebe Bridgers", Arg.Is<DateTimeOffset>(d => d > DateTimeOffset.UtcNow), null);
        await _related.DidNotReceive().GetRelated(Arg.Any<ArtistKey>(), Arg.Any<bool>(), Arg.Any<bool>());
        await _queue.DidNotReceive().UpsertCandidates(Arg.Any<string>(), Arg.Any<IReadOnlyList<DiscoveryCandidate>>());
    }

    [Fact]
    public async Task Snoozing_an_album_records_a_snooze_on_the_album_ratings()
    {
        await _sut.SnoozeAlbum(User, "Big Thief", "Capacity", "art", TimeSpan.FromDays(30));

        await _albumRatings.Received(1).Snooze(
            User, "Big Thief", "Capacity", "art", Arg.Is<DateTimeOffset>(d => d > DateTimeOffset.UtcNow));
        // A snooze isn't a verdict — it must not record a Liked/Disliked rating.
        await _albumRatings.DidNotReceive().Rate(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DiscoveryStatus>());
    }

    [Fact]
    public async Task TopUp_expands_from_liked_artists_without_clearing_pending()
    {
        _queue.GetLikedArtistNames(User).Returns(new[] { "boygenius" });
        Relates("boygenius", ("Phoebe Bridgers", null, 1));

        await _sut.TopUp(User);

        // Additive: it grows the frontier but, unlike Rebuild, never wipes the existing pending queue.
        await _queue.DidNotReceive().DeletePending(Arg.Any<string>());
        Captured(_queue).Select(c => c.Artist.ArtistName).Should().Equal("Phoebe Bridgers");
    }

    [Fact]
    public async Task Rebuild_clears_pending_then_expands_from_liked_artists()
    {
        _queue.GetLikedArtistNames(User).Returns(new[] { "boygenius" });
        Relates("boygenius", ("Phoebe Bridgers", null, 1));

        await _sut.Rebuild(User);

        await _queue.Received(1).DeletePending(User);
        Captured(_queue).Select(c => c.Artist.ArtistName).Should().Equal("Phoebe Bridgers");
    }

    [Fact]
    public async Task Expansion_excludes_compilation_placeholder_artists()
    {
        _queue.GetLikedArtistNames(User).Returns(new[] { "boygenius" });
        Relates("boygenius", ("Various Artists", null, 1), ("Phoebe Bridgers", null, 1));

        await Recommended();

        Captured(_queue).Select(c => c.Artist.ArtistName).Should().Equal("Phoebe Bridgers");
    }

    [Fact]
    public async Task Feed_drops_placeholder_artists_already_queued_or_owned()
    {
        // A row written before the rule existed, and Plex's own compilation bucket.
        _queue.CountPending(User).Returns(1);
        _queue.GetPending(User, Arg.Any<int>(), Arg.Any<int>()).Returns(new DiscoveryPage(
            new[] { new DiscoveryCandidate(new ArtistKey("various artists"), null, 3, Array.Empty<string>(), 1) },
            0, 20, 1));
        _library.GetAllArtistMetadata().Returns(new[]
        {
            new ArtistMetadata(new ArtistKey("Various Artists"), null),
            new ArtistMetadata(new ArtistKey("Big Thief"), null),
        });

        (await Recommended()).Items.Should().BeEmpty();
        var owned = await _sut.GetFeed(User, FeedKind.LibraryArtist, 0, 20);
        owned.Items.Select(i => i.Artist.ArtistName).Should().Equal("Big Thief");
    }

    [Fact]
    public async Task Library_feed_shows_owned_artists_not_yet_rated()
    {
        _library.GetAllArtistMetadata().Returns(new[]
        {
            new ArtistMetadata(new ArtistKey("Big Thief"), "bt-img"),
            new ArtistMetadata(new ArtistKey("Alex G"), null),
        });
        _queue.GetDecidedArtists(User).Returns(new HashSet<string>(new[] { "Alex G" }, StringComparer.OrdinalIgnoreCase));

        var page = await _sut.GetFeed(User, FeedKind.LibraryArtist, 0, 20);

        page.Items.Select(i => i.Artist.ArtistName).Should().Equal("Big Thief");
        page.Items.Single().Kind.Should().Be(FeedKind.LibraryArtist);
        page.Total.Should().Be(1);
    }

    [Fact]
    public async Task Library_sections_split_owned_artists_by_whether_a_liked_artist_recommends_them()
    {
        _library.GetAllArtistMetadata().Returns(new[]
        {
            new ArtistMetadata(new ArtistKey("Big Thief"), "bt-img"), // recommended by a liked artist
            new ArtistMetadata(new ArtistKey("Alex G"), null),        // nobody recommends -> seed
        });
        _queue.GetLikedArtistNames(User).Returns(new[] { "boygenius" });
        Relates("boygenius", ("Big Thief", null, 1));

        var recommended = await _sut.GetFeed(User, FeedKind.RecommendedLibraryArtist, 0, 20);
        var seed = await _sut.GetFeed(User, FeedKind.SeedLibraryArtist, 0, 20);

        var rec = recommended.Items.Single();
        rec.Artist.ArtistName.Should().Be("Big Thief");
        rec.Kind.Should().Be(FeedKind.RecommendedLibraryArtist);
        rec.Sources.Should().Equal("boygenius"); // provenance: who vouched for it

        seed.Items.Select(i => i.Artist.ArtistName).Should().Equal("Alex G");
        seed.Items.Single().Kind.Should().Be(FeedKind.SeedLibraryArtist);
    }

    /// <summary>
    /// The arrival case behind the "&lt;user&gt;_recommended" marker: an artist that was recommended to
    /// this user while the library didn't have it, and has since shown up (somebody else bought it, or
    /// it came in with a compilation). A <em>pending</em> queue row is not a decision, so the moment the
    /// artist is owned it belongs to the recommended-library section — which is what gets it a marker at
    /// the next sweep, with no queue cleanup needed first.
    /// </summary>
    [Fact]
    public async Task An_artist_recommended_while_absent_joins_the_library_section_once_it_arrives()
    {
        // Still pending from the frontier walk — never thumbed, so never "decided"...
        _queue.GetPending(User, Arg.Any<int>(), Arg.Any<int>()).Returns(new DiscoveryPage(
            new[] { new DiscoveryCandidate(new ArtistKey("Big Thief"), null, 1, new[] { "boygenius" }, 1) },
            0, 20, 1));
        // ...and now present in Plex.
        _library.GetAllArtistMetadata().Returns(new[]
        {
            new ArtistMetadata(new ArtistKey("Big Thief"), "bt-img"),
        });
        _queue.GetLikedArtistNames(User).Returns(new[] { "boygenius" });
        Relates("boygenius", ("Big Thief", null, 1));

        var names = await _sut.RecommendedLibraryArtistNames(User);

        names.Should().Equal("Big Thief");
    }

    /// <summary>
    /// The other half of that: an artist the user <em>liked</em> before it arrived is decided, so it
    /// never enters the recommended section. Its arrival earns it "&lt;user&gt;_liked" from the tag
    /// backfill, not a recommendation marker — the user is past being nudged about it.
    /// </summary>
    [Fact]
    public async Task An_artist_liked_before_it_arrived_is_not_recommended_back_to_the_user()
    {
        _library.GetAllArtistMetadata().Returns(new[]
        {
            new ArtistMetadata(new ArtistKey("Big Thief"), null),
        });
        _queue.GetLikedArtistNames(User).Returns(new[] { "boygenius", "Big Thief" });
        _queue.GetDecidedArtists(User)
            .Returns(new HashSet<string>(new[] { "Big Thief" }, StringComparer.OrdinalIgnoreCase));
        Relates("boygenius", ("Big Thief", null, 1));
        Relates("Big Thief"); // now a frontier artist itself, with no stored edges of its own yet

        var names = await _sut.RecommendedLibraryArtistNames(User);

        names.Should().BeEmpty();
    }

    [Fact]
    public async Task Library_sections_exclude_already_rated_owned_artists()
    {
        _library.GetAllArtistMetadata().Returns(new[]
        {
            new ArtistMetadata(new ArtistKey("Big Thief"), null),
            new ArtistMetadata(new ArtistKey("Alex G"), null),
        });
        _queue.GetDecidedArtists(User).Returns(new HashSet<string>(new[] { "Alex G" }, StringComparer.OrdinalIgnoreCase));

        var seed = await _sut.GetFeed(User, FeedKind.SeedLibraryArtist, 0, 20);

        // Alex G was rated, so it's gone; Big Thief has no recommender, so it lands in the seed section.
        seed.Items.Select(i => i.Artist.ArtistName).Should().Equal("Big Thief");
    }

    [Fact]
    public async Task Missing_album_feed_excludes_albums_the_user_already_decided()
    {
        _queue.GetLikedArtistNames(User).Returns(new[] { "Big Thief" });
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(new ArtistKey("Big Thief"), new AlbumKey("Dragon New Warm Mountain"), "art1", 101),
            new MissingAlbum(new ArtistKey("Big Thief"), new AlbumKey("Capacity"), "art2", 102),
        });
        _albumRatings.GetDecidedKeys(User).Returns(new HashSet<string> { AlbumRatingKey.For("Big Thief", "Capacity") });

        var page = await _sut.GetFeed(User, FeedKind.MissingAlbum, 0, 20);

        page.Items.Select(i => i.Album).Should().Equal("Dragon New Warm Mountain");
        page.Items.Single().Kind.Should().Be(FeedKind.MissingAlbum);
    }

    [Fact]
    public async Task Missing_album_feed_withholds_singles_and_compilations()
    {
        // Singles and compilations are synced and persisted (so they're queueable from an artist's
        // discography and carry a Deezer id the downloader can use) but never pushed at anyone here —
        // a feed padded with radio edits and greatest-hits repackages is the reason this gate exists.
        _queue.GetLikedArtistNames(User).Returns(new[] { "Ben Howard" });
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(new ArtistKey("Ben Howard"), new AlbumKey("Noonday Dream"), "art1", 101,
                RecordType: "album"),
            new MissingAlbum(new ArtistKey("Ben Howard"), new AlbumKey("Variations Volume 1"), "art2", 102,
                RecordType: "ep"),
            new MissingAlbum(new ArtistKey("Ben Howard"), new AlbumKey("Heave Ho"), "art3", 103,
                RecordType: "single"),
            new MissingAlbum(new ArtistKey("Ben Howard"), new AlbumKey("Best Of"), "art4", 104,
                RecordType: "compilation"),
        });

        var page = await _sut.GetFeed(User, FeedKind.MissingAlbum, 0, 20);

        page.Items.Select(i => i.Album).Should().BeEquivalentTo("Noonday Dream", "Variations Volume 1");
    }

    [Fact]
    public async Task Missing_album_feed_offers_every_pressing()
    {
        // Deezer lists the deluxe edition and the remaster as separate releases, each with its own id
        // and its own row. The feed offers both: they are two records to acquire, and a user who wants
        // neither says so once per row — a verdict on one must never stand in for the other.
        _queue.GetLikedArtistNames(User).Returns(new[] { "Phil Collins" });
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(new ArtistKey("Phil Collins"), new AlbumKey("Both Sides (Deluxe Edition)"),
                "art1", 12194438, RecordType: "album"),
            new MissingAlbum(new ArtistKey("Phil Collins"), new AlbumKey("Both Sides (2015 Remaster)"),
                "art2", 12308830, RecordType: "album"),
        });

        var page = await _sut.GetFeed(User, FeedKind.MissingAlbum, 0, 20);

        page.Items.Select(i => i.Album).Should().BeEquivalentTo(
            "Both Sides (Deluxe Edition)", "Both Sides (2015 Remaster)");
    }

    [Fact]
    public async Task ArtistDiscography_lists_every_pressing_of_a_record()
    {
        // The drill-down is the surface that stopped merging: both pressings show, each carrying its
        // own Deezer id so either can be queued.
        _deezer.SearchArtists("Phil Collins", Arg.Any<int>())
            .Returns(new[] { new DeezerArtist { id = 186, name = "Phil Collins" } });
        _deezer.GetAlbums(186).Returns(new[]
        {
            new DeezerAlbum { id = 12194438, title = "Both Sides (Deluxe Edition)", record_type = "album" },
            new DeezerAlbum { id = 12308830, title = "Both Sides (2015 Remaster)", record_type = "album" },
        });
        _albumRatings.GetRated(User).Returns(Array.Empty<AlbumRating>());

        var listed = await _sut.ArtistDiscography(User, "Phil Collins");

        listed.Select(a => (a.Album, a.DeezerAlbumId)).Should().BeEquivalentTo(new[]
        {
            ("Both Sides (Deluxe Edition)", (long?)12194438), ("Both Sides (2015 Remaster)", 12308830),
        });
    }

    [Fact]
    public async Task Missing_album_feed_still_surfaces_rows_written_before_record_types()
    {
        // Rows persisted before record-type tracking carry no type. They predate singles being synced at
        // all, so they can only be the LPs/EPs the old sync filter admitted — dropping them would blank
        // every user's feed until the next nightly sweep rewrote the collection.
        _queue.GetLikedArtistNames(User).Returns(new[] { "Big Thief" });
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(new ArtistKey("Big Thief"), new AlbumKey("Capacity"), "art1", 101),
        });

        var page = await _sut.GetFeed(User, FeedKind.MissingAlbum, 0, 20);

        page.Items.Select(i => i.Album).Should().Equal("Capacity");
    }

    [Fact]
    public async Task ArtistAlbums_offers_records_not_a_wall_of_singles()
    {
        // The cards shown inline under a freshly-liked artist follow the same rule as the main feed —
        // otherwise liking an artist buries their albums under every B-side they ever released.
        _deezer.SearchArtists("Ben Howard", Arg.Any<int>())
            .Returns(new[] { new DeezerArtist { id = 8, name = "Ben Howard" } });
        _deezer.GetAlbums(8).Returns(new[]
        {
            new DeezerAlbum { id = 301, title = "Noonday Dream", record_type = "album" },
            new DeezerAlbum { id = 302, title = "Heave Ho", record_type = "single" },
        });

        var items = await _sut.ArtistAlbums(User, "Ben Howard");

        items.Select(i => i.Album).Should().Equal("Noonday Dream");
    }

    [Fact]
    public async Task Missing_album_feed_only_surfaces_albums_from_liked_artists()
    {
        // A fresh user with no thumbs-up sees no missing albums, even though the global store has gaps
        // for every owned artist; once they like an artist, only that artist's gaps appear.
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(new ArtistKey("Big Thief"), new AlbumKey("Capacity"), "art1", 101),
            new MissingAlbum(new ArtistKey("Wilco"), new AlbumKey("Yankee Hotel Foxtrot"), "art2", 102),
        });

        _queue.GetLikedArtistNames(User).Returns(Array.Empty<string>());
        var fresh = await _sut.GetFeed(User, FeedKind.MissingAlbum, 0, 20);
        fresh.Items.Should().BeEmpty();

        _queue.GetLikedArtistNames(User).Returns(new[] { "Big Thief" });
        var afterLike = await _sut.GetFeed(User, FeedKind.MissingAlbum, 0, 20);
        afterLike.Items.Select(i => i.Album).Should().Equal("Capacity");
    }

    [Fact]
    public async Task A_meh_is_personal_and_leaves_the_album_offerable_to_everyone_else()
    {
        // The point of the "meh" (a thumbs-down on an album): it's stored per user, so another user
        // who likes the same band is still offered the album. Nothing global is written.
        const string Other = "user-2";
        _queue.GetLikedArtistNames(User).Returns(new[] { "Stevie Wonder" });
        _queue.GetLikedArtistNames(Other).Returns(new[] { "Stevie Wonder" });
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(new ArtistKey("Stevie Wonder"), new AlbumKey("Talking Book"), "art1", 101),
        });
        _albumRatings.GetDecidedKeys(Other).Returns(new HashSet<string>());

        await _sut.RateAlbum(User, "Stevie Wonder", "Talking Book", "art1", DiscoveryStatus.Disliked);

        await _albumRatings.Received(1)
            .Rate(User, "Stevie Wonder", "Talking Book", "art1", DiscoveryStatus.Disliked);
        await _blocks.DidNotReceive().Add(Arg.Any<AlbumBlock>());
        var otherUser = await _sut.GetFeed(Other, FeedKind.MissingAlbum, 0, 20);
        otherUser.Items.Select(i => i.Album).Should().Equal("Talking Book");
    }

    [Fact]
    public async Task A_blocked_album_leaves_every_users_missing_album_feed()
    {
        const string Other = "user-2";
        _queue.GetLikedArtistNames(Other).Returns(new[] { "Stevie Wonder" });
        _albumRatings.GetDecidedKeys(Other).Returns(new HashSet<string>());
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(new ArtistKey("Stevie Wonder"), new AlbumKey("Talking Book"), "art1", 101),
            new MissingAlbum(new ArtistKey("Stevie Wonder"), new AlbumKey("Innervisions"), "art2", 102),
        });
        _blocks.GetAll().Returns(new[] { new AlbumBlock("Stevie Wonder", "Talking Book", User) });

        var page = await _sut.GetFeed(Other, FeedKind.MissingAlbum, 0, 20);

        page.Items.Select(i => i.Album).Should().Equal("Innervisions");
    }

    [Fact]
    public async Task Blocking_records_the_album_under_the_collaboration_act_too()
    {
        // The album is reachable through either member's discography, so a block recorded only under
        // the listing artist would let it resurface via the other. Both acts get a row.
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(
                new ArtistKey("Milo"), new AlbumKey("Nostrum Grocers"), "art1", 101,
                new ArtistKey("Nostrum Grocers")),
        });

        await _sut.BlockAlbum(User, "Milo", "Nostrum Grocers");

        await _blocks.Received(1).Add(new AlbumBlock("Milo", "Nostrum Grocers", User));
        await _blocks.Received(1).Add(new AlbumBlock("Nostrum Grocers", "Nostrum Grocers", User));
    }

    [Fact]
    public async Task A_block_matches_across_title_typography()
    {
        // The block is keyed canonically (like a match override), so a curly-vs-straight apostrophe
        // between the stored block and Deezer's title can't let the album slip back into the feed.
        _queue.GetLikedArtistNames(User).Returns(new[] { "Big Thief" });
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(new ArtistKey("Big Thief"), new AlbumKey("Masterpiece’s Edge"), "art1", 101),
        });
        _blocks.GetAll().Returns(new[] { new AlbumBlock("Big Thief", "Masterpiece's Edge", User) });

        var page = await _sut.GetFeed(User, FeedKind.MissingAlbum, 0, 20);

        page.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_block_on_one_pressing_covers_the_record()
    {
        // Saying no to an album is saying no to the album: blocking "A Color Map of the Sun (Deluxe
        // Version)" takes the record off the feed, decoration or not. A genuinely different reading of
        // it — "(Remixes)" — is a different record and stays on offer.
        _queue.GetLikedArtistNames(User).Returns(new[] { "Pretty Lights" });
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(
                new ArtistKey("Pretty Lights"), new AlbumKey("A Color Map of the Sun"), "art1", 101),
            new MissingAlbum(
                new ArtistKey("Pretty Lights"), new AlbumKey("A Color Map of the Sun (Deluxe Version)"),
                "art2", 102),
            new MissingAlbum(
                new ArtistKey("Pretty Lights"), new AlbumKey("A Color Map of the Sun (Remixes)"),
                "art3", 103),
        });
        _blocks.GetAll().Returns(new[]
        {
            new AlbumBlock("Pretty Lights", "A Color Map of the Sun (Deluxe Version)", User),
        });

        var page = await _sut.GetFeed(User, FeedKind.MissingAlbum, 0, 20);

        page.Items.Select(i => i.Album).Should().BeEquivalentTo("A Color Map of the Sun (Remixes)");
    }

    [Fact]
    public async Task ArtistDiscography_marks_every_pressing_of_a_blocked_record()
    {
        // Same rule on the drill-down, where blocks are reviewed and lifted: the record is blocked, so
        // every edition of it reads as blocked and one click there lifts the lot.
        _deezer.SearchArtists("Pretty Lights", Arg.Any<int>())
            .Returns(new[] { new DeezerArtist { id = 55, name = "Pretty Lights" } });
        _deezer.GetAlbums(55).Returns(new[]
        {
            new DeezerAlbum { id = 101, title = "A Color Map of the Sun", record_type = "album" },
            new DeezerAlbum
            {
                id = 102, title = "A Color Map of the Sun (Deluxe Version)", record_type = "album",
            },
        });
        _albumRatings.GetRated(User).Returns(Array.Empty<AlbumRating>());
        _blocks.GetAll().Returns(new[]
        {
            new AlbumBlock("Pretty Lights", "A Color Map of the Sun (Deluxe Version)", User),
        });

        var listed = await _sut.ArtistDiscography(User, "Pretty Lights");

        listed.Where(a => a.Blocked).Select(a => a.Album)
            .Should().BeEquivalentTo(
                "A Color Map of the Sun", "A Color Map of the Sun (Deluxe Version)");
    }

    [Fact]
    public async Task ArtistAlbums_surfaces_a_new_artists_discography_excluding_decided()
    {
        // Liking a brand-new artist pulls their Deezer discography as ratable missing-album items,
        // each carrying the Deezer id so a thumbs-up can flow to the downloader.
        _deezer.SearchArtists("Phoebe Bridgers", Arg.Any<int>())
            .Returns(new[] { new DeezerArtist { id = 7, name = "Phoebe Bridgers" } });
        _deezer.GetAlbums(7).Returns(new[]
        {
            new DeezerAlbum { id = 201, title = "Stranger in the Alps", record_type = "album" },
            new DeezerAlbum { id = 202, title = "Punisher", record_type = "album" },
        });
        _albumRatings.GetDecidedKeys(User)
            .Returns(new HashSet<string> { AlbumRatingKey.For("Phoebe Bridgers", "Punisher") });

        var items = await _sut.ArtistAlbums(User, "Phoebe Bridgers");

        items.Select(i => i.Album).Should().Equal("Stranger in the Alps");
        items.Single().Kind.Should().Be(FeedKind.MissingAlbum);
        items.Single().DeezerAlbumId.Should().Be(201);
    }

    [Fact]
    public async Task Ratings_review_hides_albums_that_now_exist_in_the_library()
    {
        _albumRatings.GetRated(User).Returns(new[]
        {
            new AlbumRating(new ArtistKey("Big Thief"), new AlbumKey("Capacity"), "art", DiscoveryStatus.Liked),
            new AlbumRating(new ArtistKey("Big Thief"), new AlbumKey("U.F.O.F."), "art", DiscoveryStatus.Liked),
        });
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Big Thief"] = new(StringComparer.OrdinalIgnoreCase) { ["Capacity"] = null }, // now owned -> hidden
        });

        var ratings = await _sut.GetRatings(User);

        ratings.Where(r => r.Kind == FeedKind.MissingAlbum).Select(r => r.Album).Should().Equal("U.F.O.F.");
    }

    // ---- Reconsider: serving what the weekly sweep already flagged ----

    private Task<DiscoveryFeedPage> Reconsidered() => _sut.GetFeed(User, FeedKind.ReconsiderArtist, 0, 20);

    [Fact]
    public async Task Reconsider_serves_the_flagged_rows_with_their_stored_evidence()
    {
        // The engine does no judging here — the sweep decided, and the row carries both the verdict and
        // the numbers behind it, so the card needs no Plex round-trip to explain itself.
        _queue.GetReconsiderable(User, DiscoveryStatus.Disliked).Returns(new[]
        {
            new ReconsiderCandidate(new ArtistKey("Low"), "low-img", new ReconsiderSignal(4.0, 4, 6)),
        });

        var item = (await Reconsidered()).Items.Single();

        item.Kind.Should().Be(FeedKind.ReconsiderArtist);
        item.Artist.ArtistName.Should().Be("Low");
        item.ImageUrl.Should().Be("low-img");
        item.Reconsider.Should().Be(new ReconsiderSignal(4.0, 4, 6));
    }

    [Fact]
    public async Task Reconsider_ranks_the_strongest_contradiction_first()
    {
        _queue.GetReconsiderable(User, DiscoveryStatus.Disliked).Returns(new[]
        {
            new ReconsiderCandidate(new ArtistKey("Slint"), null, new ReconsiderSignal(3.25, 4, 4)),
            new ReconsiderCandidate(new ArtistKey("Low"), null, new ReconsiderSignal(4.0, 4, 4)),
        });

        var page = await Reconsidered();

        page.Items.Select(i => i.Artist.ArtistName).Should().Equal("Low", "Slint");
    }

    [Fact]
    public async Task Reconsider_is_empty_until_the_sweep_has_flagged_something()
    {
        (await Reconsidered()).Items.Should().BeEmpty();
    }

    // ---- Second thoughts: the same evidence read against a like ----

    private Task<DiscoveryFeedPage> SecondThoughts() =>
        _sut.GetFeed(User, FeedKind.SecondThoughtsArtist, 0, 20);

    [Fact]
    public async Task Second_thoughts_serves_the_flagged_likes_with_their_stored_evidence()
    {
        _queue.GetReconsiderable(User, DiscoveryStatus.Liked).Returns(new[]
        {
            new ReconsiderCandidate(new ArtistKey("Nickelback"), "nb-img", new ReconsiderSignal(1.5, 4, 6)),
        });

        var item = (await SecondThoughts()).Items.Single();

        item.Kind.Should().Be(FeedKind.SecondThoughtsArtist);
        item.Artist.ArtistName.Should().Be("Nickelback");
        item.ImageUrl.Should().Be("nb-img");
        item.Reconsider.Should().Be(new ReconsiderSignal(1.5, 4, 6));
    }

    [Fact]
    public async Task Second_thoughts_ranks_the_worst_rated_first()
    {
        // The opposite order to the second-chance section: down there a high average is the strongest
        // argument, here it's a low one.
        _queue.GetReconsiderable(User, DiscoveryStatus.Liked).Returns(new[]
        {
            new ReconsiderCandidate(new ArtistKey("Creed"), null, new ReconsiderSignal(2.0, 4, 4)),
            new ReconsiderCandidate(new ArtistKey("Nickelback"), null, new ReconsiderSignal(1.25, 4, 4)),
        });

        var page = await SecondThoughts();

        page.Items.Select(i => i.Artist.ArtistName).Should().Equal("Nickelback", "Creed");
    }

    // ---- Indifference: one flagged set, split by which way the ratings argue ----

    private Task<DiscoveryFeedPage> IndifferentUp() =>
        _sut.GetFeed(User, FeedKind.IndifferentLikeArtist, 0, 20);

    private Task<DiscoveryFeedPage> IndifferentDown() =>
        _sut.GetFeed(User, FeedKind.IndifferentDislikeArtist, 0, 20);

    /// <summary>
    /// A shrug is the only verdict contradicted from both sides, so unlike the other two sections these
    /// share a single flagged set and are told apart by the stored average against the policy's
    /// threshold — not by the status they were fetched at.
    /// </summary>
    [Fact]
    public async Task A_flagged_shrug_lands_in_the_section_its_ratings_argue_for()
    {
        _queue.GetReconsiderable(User, DiscoveryStatus.Indifferent).Returns(new[]
        {
            new ReconsiderCandidate(new ArtistKey("Slowdive"), "sd-img", new ReconsiderSignal(4.0, 4, 6)),
            new ReconsiderCandidate(new ArtistKey("Creed"), "cr-img", new ReconsiderSignal(1.5, 4, 6)),
        });

        var up = (await IndifferentUp()).Items.Single();
        up.Kind.Should().Be(FeedKind.IndifferentLikeArtist);
        up.Artist.ArtistName.Should().Be("Slowdive");
        up.ImageUrl.Should().Be("sd-img");
        up.Reconsider.Should().Be(new ReconsiderSignal(4.0, 4, 6));

        var down = (await IndifferentDown()).Items.Single();
        down.Kind.Should().Be(FeedKind.IndifferentDislikeArtist);
        down.Artist.ArtistName.Should().Be("Creed");
    }

    [Fact]
    public async Task Each_indifferent_section_ranks_its_own_most_contradicted_first()
    {
        _queue.GetReconsiderable(User, DiscoveryStatus.Indifferent).Returns(new[]
        {
            new ReconsiderCandidate(new ArtistKey("Ride"), null, new ReconsiderSignal(3.2, 4, 4)),
            new ReconsiderCandidate(new ArtistKey("Slowdive"), null, new ReconsiderSignal(4.5, 4, 4)),
            new ReconsiderCandidate(new ArtistKey("Creed"), null, new ReconsiderSignal(1.9, 4, 4)),
            new ReconsiderCandidate(new ArtistKey("Nickelback"), null, new ReconsiderSignal(0.5, 4, 4)),
        });

        // Highest first on the way up, lowest first on the way down: opposite ends of one scale.
        (await IndifferentUp()).Items.Select(i => i.Artist.ArtistName).Should().Equal("Slowdive", "Ride");
        (await IndifferentDown()).Items.Select(i => i.Artist.ArtistName)
            .Should().Equal("Nickelback", "Creed");
    }

    /// <summary>
    /// The split is one boolean, not two predicates, so every flagged row lands in exactly one section
    /// — including one stranded in the dead band by a threshold retuned after it was flagged. Two
    /// predicates would drop it from both, leaving a row flagged in Mongo that no page can show and no
    /// click can clear.
    /// </summary>
    [Fact]
    public async Task Every_flagged_shrug_lands_in_exactly_one_section()
    {
        _queue.GetReconsiderable(User, DiscoveryStatus.Indifferent).Returns(new[]
        {
            // 2.5★ is the dead band: the sweep would not flag this today, but a row flagged under
            // different thresholds still has to be servable.
            new ReconsiderCandidate(new ArtistKey("Editors"), null, new ReconsiderSignal(2.5, 4, 6)),
        });

        var up = (await IndifferentUp()).Items.Count;
        var down = (await IndifferentDown()).Items.Count;

        (up + down).Should().Be(1);
    }

    [Fact]
    public async Task An_indifferent_row_never_shows_up_as_a_thumb_being_second_guessed()
    {
        _queue.GetReconsiderable(User, DiscoveryStatus.Indifferent).Returns(new[]
        {
            new ReconsiderCandidate(new ArtistKey("Slowdive"), null, new ReconsiderSignal(4.0, 4, 6)),
        });

        (await Reconsidered()).Items.Should().BeEmpty();
        (await SecondThoughts()).Items.Should().BeEmpty();
    }

    // ---- Indifference: what a shrug does to the queue ----

    /// <summary>
    /// The Liked&#8594;Indifferent case, and the reason indifference prunes at all. The like seeded
    /// pending rows naming this artist in their sources; withdrawing it to a shrug leaves them standing
    /// as recommendations grown from taste the user no longer claims — exactly what a clear describes,
    /// so it gets the same cleanup. Without this the chain simply falls through and everything looks
    /// correct.
    /// </summary>
    [Fact]
    public async Task Shrugging_at_an_artist_prunes_what_it_seeded_and_does_not_expand()
    {
        await _sut.RateArtist(User, "Phoebe Bridgers", DiscoveryStatus.Indifferent);

        await _queue.Received(1).Rate(User, "Phoebe Bridgers", DiscoveryStatus.Indifferent, null);
        await _queue.Received(1).PruneBySource(User, "Phoebe Bridgers");
        await _related.DidNotReceive().GetRelated(Arg.Any<ArtistKey>(), Arg.Any<bool>(), Arg.Any<bool>());
        await _queue.DidNotReceive().UpsertCandidates(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<DiscoveryCandidate>>());
    }

    [Fact]
    public async Task Deferred_follow_up_for_a_shrug_prunes_like_a_dislike()
    {
        await _sut.ApplyVerdictFollowUp(User, "boygenius", DiscoveryStatus.Indifferent, depth: 0);

        await _queue.Received(1).PruneBySource(User, "boygenius");
        await _queue.DidNotReceive().UpsertCandidates(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<DiscoveryCandidate>>());
    }

    /// <summary>
    /// A shrug confirms on the second pass exactly as a thumb does. It matters more here: indifference
    /// is contradicted from both sides, so without a terminal state a band with polarised song ratings
    /// would be offered back every week for good.
    /// </summary>
    [Fact]
    public async Task A_second_shrug_confirms_the_verdict()
    {
        _queue.TryConfirmVerdict(User, "Editors", DiscoveryStatus.Indifferent).Returns(true);

        await _sut.RecordArtistVerdict(User, "Editors", DiscoveryStatus.Indifferent, confirm: true);

        await _queue.Received(1).TryConfirmVerdict(User, "Editors", DiscoveryStatus.Indifferent);
    }

    [Fact]
    public async Task The_two_second_guessing_sections_never_bleed_into_each_other()
    {
        // Each reads only the rows sitting at its own verdict, so a flagged like can't show up as a
        // second chance (or vice versa) just because both use the same stored signal.
        _queue.GetReconsiderable(User, DiscoveryStatus.Liked).Returns(new[]
        {
            new ReconsiderCandidate(new ArtistKey("Nickelback"), null, new ReconsiderSignal(1.5, 4, 6)),
        });

        (await Reconsidered()).Items.Should().BeEmpty();
        (await SecondThoughts()).Items.Single().Artist.ArtistName.Should().Be("Nickelback");
    }

    [Fact]
    public async Task First_dislike_records_the_verdict_without_confirming_it()
    {
        // An ordinary thumb doesn't ask to confirm, so the engine never reaches for it. The repo's own
        // "only when the row already held this verdict" guard still stands behind that, but it is no
        // longer the only thing standing between a repeat and a permanent verdict.
        await _sut.RateArtist(User, "Low", DiscoveryStatus.Disliked);

        await _queue.Received(1).Rate(User, "Low", DiscoveryStatus.Disliked, null);
        await _queue.DidNotReceive().TryConfirmVerdict(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DiscoveryStatus>());
    }

    [Fact]
    public async Task Second_dislike_confirms_the_rejection_before_recording_it()
    {
        // The confirm must land while the row still holds the previous verdict, i.e. before Rate
        // overwrites it — otherwise every dislike would look like a repeat.
        _queue.TryConfirmVerdict(User, "Low", DiscoveryStatus.Disliked).Returns(true);

        await _sut.RateArtist(User, "Low", DiscoveryStatus.Disliked, confirm: true);

        Received.InOrder(() =>
        {
            _queue.TryConfirmVerdict(User, "Low", DiscoveryStatus.Disliked);
            _queue.Rate(User, "Low", DiscoveryStatus.Disliked, null);
        });
    }

    [Fact]
    public async Task Second_like_confirms_the_like_before_recording_it()
    {
        // The mirror: standing by a thumbs-up the "second thoughts" section questioned settles it, so
        // the sweep stops weighing that artist for good.
        Relates("Low", ("Codeine", null, 1)); // a like expands the frontier, which reads the graph
        _queue.TryConfirmVerdict(User, "Low", DiscoveryStatus.Liked).Returns(true);

        await _sut.RateArtist(User, "Low", DiscoveryStatus.Liked, confirm: true);

        Received.InOrder(() =>
        {
            _queue.TryConfirmVerdict(User, "Low", DiscoveryStatus.Liked);
            _queue.Rate(User, "Low", DiscoveryStatus.Liked, null);
        });
    }

    /// <summary>
    /// The regression this whole flag exists for. A bulk script re-likes an artist it already liked
    /// every time that artist turns up on another playlist; inferring confirmation from the row alone
    /// read each of those as "stood by it twice" and retired the artist from the sweep permanently. On
    /// a library where 44% of artists span two or more playlists that silently disabled second-guessing
    /// for nearly half of it, with no UI anywhere that showed it had happened.
    /// </summary>
    [Fact]
    public async Task Re_rating_an_artist_without_asking_to_confirm_never_confirms()
    {
        Relates("Low", ("Codeine", null, 1));
        // The repo would happily confirm: the row already holds this verdict.
        _queue.TryConfirmVerdict(User, "Low", DiscoveryStatus.Liked).Returns(true);

        await _sut.RateArtist(User, "Low", DiscoveryStatus.Liked);

        await _queue.DidNotReceive().TryConfirmVerdict(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DiscoveryStatus>());
        await _queue.Received(1).Rate(User, "Low", DiscoveryStatus.Liked, null);
    }

    /// <summary>
    /// Confirmation is gated on the verdict being one that *can* be confirmed, not just on the caller
    /// asking. A snooze is a deferred decision, so there is nothing to stand by.
    /// </summary>
    [Fact]
    public async Task Asking_to_confirm_a_snooze_confirms_nothing()
    {
        await _sut.RecordArtistVerdict(User, "Low", DiscoveryStatus.Snoozed, confirm: true);

        await _queue.DidNotReceive().TryConfirmVerdict(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DiscoveryStatus>());
    }

    [Fact]
    public async Task Liking_an_artist_never_confirms_a_dislike()
    {
        Relates("Low", ("Codeine", null, 1)); // a like expands the frontier, which reads the graph

        await _sut.RateArtist(User, "Low", DiscoveryStatus.Liked, confirm: true);

        await _queue.DidNotReceive().TryConfirmVerdict(
            Arg.Any<string>(), Arg.Any<string>(), DiscoveryStatus.Disliked);
    }

    [Fact]
    public async Task Disliking_an_artist_never_confirms_a_like()
    {
        await _sut.RateArtist(User, "Low", DiscoveryStatus.Disliked, confirm: true);

        await _queue.DidNotReceive().TryConfirmVerdict(
            Arg.Any<string>(), Arg.Any<string>(), DiscoveryStatus.Liked);
    }

    // ---- Upgrades: same rows as the missing feed, narrowed per user ----

    private Task<DiscoveryFeedPage> Upgrades() => _sut.GetFeed(User, FeedKind.UpgradeAlbum, 0, 20);
    private Task<DiscoveryFeedPage> Missing() => _sut.GetFeed(User, FeedKind.MissingAlbum, 0, 20);

    /// <summary>The signed-in user's own ceiling.</summary>
    private void UserTier(AudioQuality quality) =>
        _users.Get(User).Returns(new AppUser(User, User, null, null, default, default, quality));

    /// <summary>One row in the global missing set. A quality makes it an upgrade; null a gap.</summary>
    private void MissingRow(string artist, string album, AudioQuality? ownedQuality)
    {
        _queue.GetLikedArtistNames(User).Returns(new[] { artist });
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(
                new ArtistKey(artist), new AlbumKey(album), null, 7, null, null, "album", ownedQuality),
        });
    }

    [Fact]
    public async Task An_upgrade_row_reaches_a_user_who_out_ranks_the_copy_on_disk()
    {
        UserTier(AudioQuality.Lossless);
        MissingRow("boygenius", "the record", AudioQuality.Lossy);

        var page = (await Upgrades()).Items;

        page.Should().ContainSingle();
        page[0].Kind.Should().Be(FeedKind.UpgradeAlbum);
        page[0].OwnedQuality.Should().Be(AudioQuality.Lossy);
    }

    [Fact]
    public async Task The_same_row_is_hidden_from_a_user_who_does_not()
    {
        // The sync diffs against the best tier *anyone* here holds, so a row can exist that belongs
        // to someone else's feed. A 320 user is not shown a 320 album they already have.
        UserTier(AudioQuality.Lossy);
        MissingRow("boygenius", "the record", AudioQuality.Lossy);

        (await Upgrades()).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Upgrades_do_not_appear_in_the_missing_feed()
    {
        // They are separately toggleable precisely so someone can fill gaps without being offered
        // replacements for records they already own.
        UserTier(AudioQuality.Lossless);
        MissingRow("boygenius", "the record", AudioQuality.Lossy);

        (await Missing()).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Gaps_do_not_appear_in_the_upgrade_feed()
    {
        UserTier(AudioQuality.Lossless);
        MissingRow("boygenius", "the record", null);

        (await Upgrades()).Items.Should().BeEmpty();
        (await Missing()).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task A_skipped_upgrade_stops_being_offered()
    {
        UserTier(AudioQuality.Lossless);
        MissingRow("boygenius", "the record", AudioQuality.Lossy);
        _blocks.GetAll().Returns(new[]
        {
            new AlbumBlock("boygenius", "the record", User, AlbumBlockScope.Upgrade),
        });

        (await Upgrades()).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task An_upgrade_skip_does_not_hide_the_album_from_anything_else()
    {
        // The user owns and likes this record; declining to replace it must not read as "don't carry
        // this release", which is what a Release-scoped block means.
        UserTier(AudioQuality.Lossless);
        MissingRow("boygenius", "the record", null); // a gap this time
        _blocks.GetAll().Returns(new[]
        {
            new AlbumBlock("boygenius", "the record", User, AlbumBlockScope.Upgrade),
        });

        (await Missing()).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task An_expired_snooze_puts_the_upgrade_back_on_offer()
    {
        // "Deezer had no lossless" is recorded with a deadline, not forever: a catalogue can gain a
        // lossless master later, and foreclosing on that permanently would be wrong.
        UserTier(AudioQuality.Lossless);
        MissingRow("boygenius", "the record", AudioQuality.Lossy);
        _blocks.GetAll().Returns(new[]
        {
            new AlbumBlock(
                "boygenius", "the record", null, AlbumBlockScope.Upgrade,
                DateTimeOffset.UtcNow.AddDays(-1)),
        });

        (await Upgrades()).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task A_live_snooze_still_holds()
    {
        UserTier(AudioQuality.Lossless);
        MissingRow("boygenius", "the record", AudioQuality.Lossy);
        _blocks.GetAll().Returns(new[]
        {
            new AlbumBlock(
                "boygenius", "the record", null, AlbumBlockScope.Upgrade,
                DateTimeOffset.UtcNow.AddDays(30)),
        });

        (await Upgrades()).Items.Should().BeEmpty();
    }

    // ---- The thumbs-down on an upgrade must not become a dislike ----

    [Fact]
    public async Task Declining_an_upgrade_records_a_skip_not_an_album_dislike()
    {
        await _sut.RateUpgrade(User, "noggog", "boygenius", "the record", null, DiscoveryStatus.Disliked);

        // The user owns and likes this album. A dislike would show it as rejected on their Ratings
        // page and drop it out of the liked set the frontier grows from.
        await _albumRatings.DidNotReceive().Rate(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<DiscoveryStatus>());
        // Attributed by username, not by the OIDC subject that keys the rating store: nothing matches
        // on this field, so the only readers are a person and the export.
        await _blocks.Received().Add(Arg.Is<AlbumBlock>(b =>
            b.Scope == AlbumBlockScope.Upgrade && b.RetryAfter == null && b.BlockedBy == "noggog"));
    }

    [Fact]
    public async Task Accepting_an_upgrade_rates_it_like_any_other_acquisition()
    {
        // The like is what puts it on the shared to-buy list, same as a missing album.
        await _sut.RateUpgrade(User, "noggog", "boygenius", "the record", "art", DiscoveryStatus.Liked);

        await _albumRatings.Received().Rate(User, "boygenius", "the record", "art", DiscoveryStatus.Liked);
        await _blocks.DidNotReceive().Add(Arg.Any<AlbumBlock>());
    }

    [Fact]
    public async Task A_no_lossless_verdict_is_snoozed_rather_than_permanent()
    {
        var until = DateTimeOffset.UtcNow.AddDays(90);

        await _sut.SkipUpgrade("noggog", "boygenius", "the record", until);

        await _blocks.Received().Add(Arg.Is<AlbumBlock>(b =>
            b.Scope == AlbumBlockScope.Upgrade && b.RetryAfter == until));
    }
}
