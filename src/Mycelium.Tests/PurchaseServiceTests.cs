using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Download;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

public class PurchaseServiceTests
{
    private readonly FakePurchaseRepo _purchases = new();
    private readonly IUserQueueRepo _queue = Substitute.For<IUserQueueRepo>();
    private readonly IUserAlbumRatingRepo _albumRatings = Substitute.For<IUserAlbumRatingRepo>();
    private readonly ILibraryProvider _library = Substitute.For<ILibraryProvider>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly IMissingAlbumRepo _missing = Substitute.For<IMissingAlbumRepo>();
    private readonly FakeAlbumMatchOverrideRepo _overrides = new();
    private readonly IDownloader _downloader = Substitute.For<IDownloader>();
    private readonly DownloadSchedule _schedule = new();
    private readonly PurchaseService _sut;

    private static readonly DownloaderConfig Config = new(
        DownloadDir: "", RipBinary: "rip", Quality: "2", FallbackQualities: new[] { "1", "0" },
        Codec: "", BatchSize: 3, ItemDelay: TimeSpan.Zero, BatchInterval: TimeSpan.Zero,
        DownloadTimeout: TimeSpan.FromMinutes(15), SettleInterval: TimeSpan.FromMinutes(15),
        SettleWindow: TimeSpan.FromHours(6));

    public PurchaseServiceTests()
    {
        var settings = new DownloadSettings(
            new FakeAppSettingsRepo(), NullLogger<DownloadSettings>.Instance);
        _sut = new PurchaseService(
            _purchases, _queue, _albumRatings, _library, _catalog, _missing, _overrides, _downloader, Config,
            settings, new JitterPolicy(0.3), _schedule, NullLogger<PurchaseService>.Instance);

        _queue.GetAllLiked().Returns(Array.Empty<DiscoveryCandidate>());
        _albumRatings.GetAllLiked().Returns(Array.Empty<AlbumRating>());
        _library.GetAllArtistMetadata().Returns(Array.Empty<ArtistMetadata>());
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));
        _missing.GetAll().Returns(Array.Empty<MissingAlbum>());
        _downloader.Name.Returns("test-backend");
        _downloader.Request(Arg.Any<PurchaseItem>()).Returns(DownloadOutcome.Success());
    }

    private void Owned(params string[] artists) =>
        _library.GetAllArtistMetadata().Returns(artists.Select(a => new ArtistMetadata(new ArtistKey(a), null)).ToArray());

    [Fact]
    public async Task Active_lists_liked_non_owned_artists_and_still_missing_liked_albums()
    {
        Owned("Owned Band");
        _queue.GetAllLiked().Returns(new[]
        {
            new DiscoveryCandidate(new ArtistKey("Phoebe Bridgers"), null, 3, new[] { "boygenius" }, 1),
            new DiscoveryCandidate(new ArtistKey("Owned Band"), null, 1, Array.Empty<string>(), 0), // owned -> excluded
        });
        _albumRatings.GetAllLiked().Returns(new[]
        {
            new AlbumRating(new ArtistKey("Owned Band"), new AlbumKey("New One"), "art", DiscoveryStatus.Liked),
        });

        var active = await _sut.GetActive();

        active.Where(p => p.Kind == FeedKind.RecommendedArtist).Select(p => p.Artist.ArtistName)
            .Should().Equal("Phoebe Bridgers");
        active.Where(p => p.Kind == FeedKind.MissingAlbum).Select(p => p.Album).Should().Equal("New One");
        active.Should().OnlyContain(p => p.Status == PurchaseStatus.Pending);
    }

    [Fact]
    public async Task Active_dedups_items_liked_by_multiple_users()
    {
        // Same artist liked by two users: one occurrence, strongest score, unioned sources.
        _queue.GetAllLiked().Returns(new[]
        {
            new DiscoveryCandidate(new ArtistKey("Phoebe Bridgers"), null, 3, new[] { "boygenius" }, 1),
            new DiscoveryCandidate(new ArtistKey("Phoebe Bridgers"), "img", 5, new[] { "Bright Eyes" }, 1),
        });
        _albumRatings.GetAllLiked().Returns(new[]
        {
            new AlbumRating(new ArtistKey("Big Thief"), new AlbumKey("Capacity"), "art", DiscoveryStatus.Liked),
            new AlbumRating(new ArtistKey("Big Thief"), new AlbumKey("Capacity"), "art2", DiscoveryStatus.Liked),
        });

        var active = await _sut.GetActive();

        var artist = active.Single(p => p.Kind == FeedKind.RecommendedArtist);
        artist.Artist.ArtistName.Should().Be("Phoebe Bridgers");
        artist.Score.Should().Be(5);
        artist.ImageUrl.Should().Be("img");
        artist.Sources.Should().BeEquivalentTo("boygenius", "Bright Eyes");

        active.Where(p => p.Kind == FeedKind.MissingAlbum).Select(p => p.Album).Should().Equal("Capacity");
    }

    [Fact]
    public async Task Pending_item_no_longer_liked_is_pruned_but_an_in_flight_one_is_kept()
    {
        var liked = new[]
        {
            new DiscoveryCandidate(new ArtistKey("Pending Band"), null, 1, Array.Empty<string>(), 1),
            new DiscoveryCandidate(new ArtistKey("Sent Band"), null, 1, Array.Empty<string>(), 1),
        };
        _queue.GetAllLiked().Returns(liked);
        await _sut.Reconcile();
        // "Sent Band" has been downloaded (in flight, awaiting the library).
        await _purchases.SetStatus(PurchaseKey.ForArtist("Sent Band"), PurchaseStatus.Sent);

        // Both un-liked now (nobody wants them via ratings any more).
        _queue.GetAllLiked().Returns(Array.Empty<DiscoveryCandidate>());
        var active = await _sut.GetActive();

        // The pending one is dropped; the in-flight one survives.
        active.Select(p => p.Artist.ArtistName).Should().Equal("Sent Band");
        active.Single().Status.Should().Be(PurchaseStatus.Sent);
    }

    [Fact]
    public async Task Acquired_artist_closes_out_to_in_library_and_drops_off()
    {
        _queue.GetAllLiked().Returns(new[]
        {
            new DiscoveryCandidate(new ArtistKey("Phoebe Bridgers"), null, 3, Array.Empty<string>(), 1),
        });
        await _sut.Reconcile();

        // It's now in the library (and still liked).
        Owned("Phoebe Bridgers");
        var active = await _sut.GetActive();

        active.Should().BeEmpty();
        _purchases.Items.Single().Status.Should().Be(PurchaseStatus.InLibrary);
    }

    [Fact]
    public async Task Album_owned_under_a_typographically_different_title_closes_out_to_in_library()
    {
        const string likedTitle = "who told you to think??!!?!?!?!";
        // Plex stored the same album with a zero-width space and extra whitespace. A case-only
        // compare misses it, leaving an already-downloaded album stuck on the queue forever;
        // reconcile must match it the same canonical way the missing-album diff does.
        const string plexTitle = "Who told you to ​think??!!?!?!?!";

        _albumRatings.GetAllLiked().Returns(new[]
        {
            new AlbumRating(new ArtistKey("Milo"), new AlbumKey(likedTitle), "art", DiscoveryStatus.Liked),
        });
        await _sut.Reconcile();
        await _purchases.SetStatus(PurchaseKey.ForAlbum("Milo", likedTitle), PurchaseStatus.Sent);

        // It has since landed in Plex under the typographically-different title.
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Milo"] = new(StringComparer.OrdinalIgnoreCase) { plexTitle },
        });

        var active = await _sut.GetActive();

        active.Should().BeEmpty();
        _purchases.Items.Single().Status.Should().Be(PurchaseStatus.InLibrary);
    }

    [Fact]
    public async Task Album_owned_under_a_different_album_artist_closes_out_to_in_library()
    {
        // A collaboration surfaced/liked under "Milo", but the library files it under the duo
        // "Nostrum Grocers" (Deezer's album-artist, carried on the missing record). Reconcile must
        // match ownership under the album-artist, not the display artist, and close the row out.
        _albumRatings.GetAllLiked().Returns(new[]
        {
            new AlbumRating(new ArtistKey("Milo"), new AlbumKey("Nostrum Grocers"), "art", DiscoveryStatus.Liked),
        });
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(new ArtistKey("Milo"), new AlbumKey("Nostrum Grocers"), "art", 456880775,
                new ArtistKey("Nostrum Grocers")),
        });
        await _sut.Reconcile();
        await _purchases.SetStatus(PurchaseKey.ForAlbum("Milo", "Nostrum Grocers"), PurchaseStatus.Sent);

        // It has since landed in Plex, filed under the album-artist "Nostrum Grocers" — not "Milo".
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Nostrum Grocers"] = new(StringComparer.OrdinalIgnoreCase) { "Nostrum Grocers" },
        });

        var active = await _sut.GetActive();

        active.Should().BeEmpty();
        _purchases.Items.Single().Status.Should().Be(PurchaseStatus.InLibrary);
    }

    [Fact]
    public async Task Album_items_carry_the_deezer_album_id_from_the_missing_set()
    {
        _albumRatings.GetAllLiked().Returns(new[]
        {
            new AlbumRating(new ArtistKey("Big Thief"), new AlbumKey("Capacity"), "art", DiscoveryStatus.Liked),
        });
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(new ArtistKey("Big Thief"), new AlbumKey("Capacity"), "art", 12345),
        });

        var item = (await _sut.GetActive()).Single(p => p.Kind == FeedKind.MissingAlbum);

        item.DeezerAlbumId.Should().Be(12345);
    }

    [Fact]
    public async Task Failed_items_stay_on_the_active_list_for_retry()
    {
        _albumRatings.GetAllLiked().Returns(new[]
        {
            new AlbumRating(new ArtistKey("Big Thief"), new AlbumKey("Capacity"), "art", DiscoveryStatus.Liked),
        });
        await _sut.Reconcile();
        var id = PurchaseKey.ForAlbum("Big Thief", "Capacity");
        await _purchases.SetStatus(id, PurchaseStatus.Failed);

        // Failed items remain visible on the active list (so they can be retried), not dropped.
        (await _sut.GetActive()).Single(p => p.Id == id).Status.Should().Be(PurchaseStatus.Failed);
    }

    [Fact]
    public async Task Snapshot_reports_backend_and_counts_by_stage()
    {
        _albumRatings.GetAllLiked().Returns(new[]
        {
            new AlbumRating(new ArtistKey("A"), new AlbumKey("queued"), null, DiscoveryStatus.Liked),
            new AlbumRating(new ArtistKey("B"), new AlbumKey("sent"), null, DiscoveryStatus.Liked),
        });
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(new ArtistKey("A"), new AlbumKey("queued"), null, 11),
            new MissingAlbum(new ArtistKey("B"), new AlbumKey("sent"), null, 22),
        });
        await _sut.Reconcile();
        await _purchases.SetStatus(PurchaseKey.ForAlbum("B", "sent"), PurchaseStatus.Sent);

        var snap = await _sut.GetDownloadSnapshot();

        snap.Automatic.Should().BeTrue();
        snap.Backend.Should().Be(_downloader.Name);
        snap.Queued.Should().Be(1); // only the downloadable pending album with a Deezer id
        snap.Complete.Should().Be(1);
        snap.BatchSize.Should().Be(3);
        snap.JitterPercent.Should().Be(30);
    }

    [Fact]
    public async Task Snapshot_raises_a_blocking_flag_only_for_failures_no_retry_can_fix()
    {
        _albumRatings.GetAllLiked().Returns(new[]
        {
            new AlbumRating(new ArtistKey("A"), new AlbumKey("geo blocked"), null, DiscoveryStatus.Liked),
            new AlbumRating(new ArtistKey("B"), new AlbumKey("bad login"), null, DiscoveryStatus.Liked),
        });
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(new ArtistKey("A"), new AlbumKey("geo blocked"), null, 11),
            new MissingAlbum(new ArtistKey("B"), new AlbumKey("bad login"), null, 22),
        });
        await _sut.Reconcile();

        // An album Deezer wouldn't serve is this row's own problem — the queue is otherwise healthy,
        // so the panel must not claim downloads are blocked.
        await _purchases.SetStatus(
            PurchaseKey.ForAlbum("A", "geo blocked"), PurchaseStatus.Failed,
            DownloadFailure.NoTracksAvailable);
        (await _sut.GetDownloadSnapshot()).Blocking.Should().Be(DownloadFailure.None);

        // A rejected credential is everyone's problem: it will fail every other row identically.
        await _purchases.SetStatus(
            PurchaseKey.ForAlbum("B", "bad login"), PurchaseStatus.Failed, DownloadFailure.DeezerAuth);
        (await _sut.GetDownloadSnapshot()).Blocking.Should().Be(DownloadFailure.DeezerAuth);

        // Re-queueing clears the row's reason, so a fixed ARL doesn't leave the banner stuck up.
        await _purchases.SetStatus(PurchaseKey.ForAlbum("B", "bad login"), PurchaseStatus.Queued);
        (await _sut.GetDownloadSnapshot()).Blocking.Should().Be(DownloadFailure.None);
    }

    [Fact]
    public async Task Snapshot_surfaces_when_the_drainer_next_acts()
    {
        // Nothing scheduled yet (the drainer hasn't run a pass) -> nothing for the UI to count down.
        (await _sut.GetDownloadSnapshot()).NextBatchAt.Should().BeNull();

        _schedule.BatchWait(TimeSpan.FromMinutes(30));
        _schedule.ItemWait(TimeSpan.FromSeconds(60));
        var snap = await _sut.GetDownloadSnapshot();

        snap.NextBatchAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(30), TimeSpan.FromSeconds(5));
        snap.NextItemAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(60), TimeSpan.FromSeconds(5));

        // The inter-album wait ends when the next download starts; only the batch time remains.
        _schedule.ClearItemWait();
        (await _sut.GetDownloadSnapshot()).NextItemAt.Should().BeNull();
    }

    [Fact]
    public async Task Merge_records_an_override_and_closes_the_row_out_to_in_library()
    {
        // A structural title mismatch the normalizer can't fold: Deezer's "DOOM (Original Game
        // Soundtrack)" vs. the copy already in Plex, "Doom: Original Game Soundtrack". It sits stuck
        // as Pending because reconcile can't see it's owned.
        const string deezerTitle = "DOOM (Original Game Soundtrack)";
        const string plexTitle = "Doom: Original Game Soundtrack";
        _albumRatings.GetAllLiked().Returns(new[]
        {
            new AlbumRating(new ArtistKey("Mick Gordon"), new AlbumKey(deezerTitle), "art", DiscoveryStatus.Liked),
        });
        await _sut.Reconcile();
        var id = PurchaseKey.ForAlbum("Mick Gordon", deezerTitle);
        (await _sut.GetActive()).Single(p => p.Id == id).Status.Should().Be(PurchaseStatus.Pending);

        // The library owns it under the near-miss title; the user merges the two by hand.
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mick Gordon"] = new(StringComparer.OrdinalIgnoreCase) { plexTitle },
        });
        (await _sut.MergeCandidates("Mick Gordon", deezerTitle, null))
            .Should().Equal(new LibraryAlbumOption("Mick Gordon", plexTitle));

        (await _sut.MergeAlbum("Mick Gordon", deezerTitle, plexTitle)).Should().BeTrue();

        // The override is recorded, and the row closes out — and stays closed across a fresh reconcile
        // (the override is honoured, not just a one-off status flip).
        _overrides.Items.Should().ContainSingle(o =>
            o.MatchArtist == "Mick Gordon" && o.DeezerTitle == deezerTitle && o.LibraryTitle == plexTitle);
        (await _sut.GetActive()).Should().BeEmpty();
        _purchases.Items.Single().Status.Should().Be(PurchaseStatus.InLibrary);
    }

    [Fact]
    public async Task Merge_returns_false_for_blank_input()
    {
        (await _sut.MergeAlbum("Nobody", "Nothing", "  ")).Should().BeFalse();
    }

    [Fact]
    public async Task Merge_candidates_offer_the_same_title_owned_under_a_different_act()
    {
        // The library files "Care Tracts" under "Matthewdavid's Mindflight"; Deezer lists it under the
        // plain "Matthewdavid", which the library also has (with an unrelated album). Nothing owned
        // under the listing artist matches, so the suggestion has to come from the same-title sweep.
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Matthewdavid"] = new(StringComparer.OrdinalIgnoreCase) { "Outmind" },
            ["Matthewdavid's Mindflight"] = new(StringComparer.OrdinalIgnoreCase) { "Care Tracts" },
        });

        var candidates = await _sut.MergeCandidates("Matthewdavid", "Care Tracts", null);

        // Same-title first (it's the one they mean), then what the listing artist owns.
        candidates.Should().Equal(
            new LibraryAlbumOption("Matthewdavid's Mindflight", "Care Tracts"),
            new LibraryAlbumOption("Matthewdavid", "Outmind"));
    }

    [Fact]
    public async Task Merge_of_an_unrated_album_records_the_override_without_a_queued_row()
    {
        // Merging straight from the Browse discography / Discover feed: no one has thumbed the album,
        // so there is no purchase row to close out — the override still has to land, keyed under both
        // the listing artist and the act Deezer credits, so neither the diff nor the reconcile
        // resurfaces it.
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(
                new ArtistKey("Matthewdavid"), new AlbumKey("Care Tracts"), null, 42,
                new ArtistKey("Matthewdavid's Mindflight")),
        });

        (await _sut.MergeAlbum("Matthewdavid", "Care Tracts", "Care Tracts")).Should().BeTrue();

        _overrides.Items.Select(o => o.MatchArtist)
            .Should().BeEquivalentTo("Matthewdavid", "Matthewdavid's Mindflight");
        _purchases.Items.Should().BeEmpty();
        // And it leaves the missing set at once, rather than lingering in the feed until the next sweep.
        await _missing.Received(1).ReplaceForArtist("Matthewdavid", Arg.Is<IReadOnlyList<MissingAlbum>>(l => l.Count == 0));
    }
}
