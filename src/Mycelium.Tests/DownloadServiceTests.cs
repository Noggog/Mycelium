using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Background;
using Mycelium.Backend.Services.Download;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

public class DownloadServiceTests
{
    private readonly FakePurchaseRepo _repo = new();
    private readonly FakeAppSettingsRepo _settingsRepo = new();
    private readonly IDownloader _downloader = Substitute.For<IDownloader>();

    // The catalog side of the settle pass: a real CatalogRefresher over a substituted Plex read, so a
    // settle test can say "Plex now reports this album" and watch the row close out for real.
    private readonly ILibraryQuery _libraryQuery = Substitute.For<ILibraryQuery>();
    private readonly IArtistCatalogRepo _catalogRepo = Substitute.For<IArtistCatalogRepo>();
    private readonly ILibraryProvider _library = Substitute.For<ILibraryProvider>();
    private readonly IUserQueueRepo _queue = Substitute.For<IUserQueueRepo>();
    private readonly IUserAlbumRatingRepo _albumRatings = Substitute.For<IUserAlbumRatingRepo>();
    private readonly IMissingAlbumRepo _missing = Substitute.For<IMissingAlbumRepo>();
    // The tagging side of the settle pass: a real ArtistTagBackfill over a substituted tagger, so a
    // settle test can assert the verdict mood lands once the artist finally shows up in Plex.
    private readonly IUserRepo _users = Substitute.For<IUserRepo>();
    private readonly IAlbumBlockRepo _blocks = Substitute.For<IAlbumBlockRepo>();
    private readonly IArtistTagger _tagger = Substitute.For<IArtistTagger>();
    private readonly FakeAlbumMatchOverrideRepo _overrides = new();
    // No jitter in tests: waits stay exact (and zero), so nothing sleeps.
    private readonly JitterPolicy _jitter = new(0);
    private readonly DownloadSchedule _schedule = new();
    private readonly List<AlbumRating> _liked = new();
    private readonly List<MissingAlbum> _missingAlbums = new();

    public DownloadServiceTests()
    {
        _libraryQuery.QueryAllArtistMetadata().Returns(Array.Empty<ArtistMetadata>());
        _libraryQuery.QueryAllAlbums().Returns(Array.Empty<ArtistAlbums>());
        _catalogRepo.SyncFromLibrary(Arg.Any<IReadOnlyList<ArtistMetadata>>(), Arg.Any<DateTimeOffset>())
            .Returns(new CatalogSyncResult(0, 0, 0, Array.Empty<string>()));
        _catalogRepo.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase));
        _library.GetAllArtistMetadata().Returns(Array.Empty<ArtistMetadata>());
        _queue.GetAllLiked().Returns(Array.Empty<DiscoveryCandidate>());
        _albumRatings.GetAllLiked().Returns(Array.Empty<AlbumRating>());
        _users.GetAll().Returns(Array.Empty<AppUser>());
        _albumRatings.GetAllLikedByUser().Returns(Array.Empty<LikedAlbum>());
        _missing.GetAll().Returns(Array.Empty<MissingAlbum>());
        _queue.GetAllUserIds().Returns(Array.Empty<string>());
    }

    private static DownloaderConfig Config(
        TimeSpan? settleWindow = null, int batchSize = 10, TimeSpan? batchInterval = null) =>
        new(DownloadDir: "", RipBinary: "rip", Quality: "2", FallbackQualities: new[] { "1", "0" },
            Codec: "", BatchSize: batchSize, ItemDelay: TimeSpan.Zero,
            BatchInterval: batchInterval ?? TimeSpan.Zero,
            DownloadTimeout: TimeSpan.FromMinutes(15), SettleInterval: TimeSpan.FromMinutes(15),
            SettleWindow: settleWindow ?? TimeSpan.FromHours(6));

    private DownloadService Sut(DownloaderConfig? config = null)
    {
        config ??= Config();
        var settings = new DownloadSettings(_settingsRepo, NullLogger<DownloadSettings>.Instance);
        var purchases = new PurchaseService(
            _repo, _queue, _albumRatings, _library, _catalogRepo, _missing, _overrides, _downloader,
            Substitute.For<IDeezerApi>(), config, settings,
            new UserQualityService(_users, AudioQuality.Lossless), _jitter, _schedule,
            NullLogger<PurchaseService>.Instance);
        var catalog = new CatalogRefresher(_libraryQuery, _catalogRepo, NullLogger<CatalogRefresher>.Instance);
        var tagBackfill = new ArtistTagBackfill(
            _tagger, _queue, _users, NullLogger<ArtistTagBackfill>.Instance);
        return new DownloadService(_repo, _downloader, config, settings, purchases, catalog, tagBackfill,
            _jitter, _schedule, Substitute.For<ILibraryScanner>(), _blocks,
            NullLogger<DownloadService>.Instance);
    }

    /// <summary>
    /// Marks an album as liked-but-unowned, the way the real queue gets its rows: the automatic pass
    /// reconciles first, which derives the pending list from likes — so a row merely seeded into the
    /// repo (with nobody wanting it) is pruned before the pass ever sees it. A null
    /// <paramref name="deezerId"/> leaves it un-fetchable, as if the missing-album diff never found it.
    /// </summary>
    private void Wanted(string artist, string album, long? deezerId)
    {
        _liked.Add(new AlbumRating(new ArtistKey(artist), new AlbumKey(album), null, DiscoveryStatus.Liked));
        if (deezerId is not null)
        {
            _missingAlbums.Add(new MissingAlbum(
                new ArtistKey(artist), new AlbumKey(album), null, deezerId.Value, new ArtistKey(artist)));
        }
        // Both reads must stay in step: Reconcile uses GetAllLikedByUser (it needs whose
        // entitlement each like carries), other callers use GetAllLiked. These tests don't exercise
        // entitlements, so everything is attributed to one user.
        _albumRatings.GetAllLiked().Returns(_liked.ToArray());
        _albumRatings.GetAllLikedByUser().Returns(
            _liked.Select(r => new LikedAlbum("test-user", r)).ToArray());
        _missing.GetAll().Returns(_missingAlbums.ToArray());
    }

    private static PurchaseItem Album(string artist, string album, long deezerId, PurchaseStatus status = PurchaseStatus.Pending) =>
        new(PurchaseKey.ForAlbum(artist, album), FeedKind.MissingAlbum, new ArtistKey(artist), album,
            null, 0, Array.Empty<string>(), status, DateTimeOffset.UtcNow, null, deezerId);

    private static PurchaseItem Artist(string artist, PurchaseStatus status = PurchaseStatus.Pending) =>
        new(PurchaseKey.ForArtist(artist), FeedKind.RecommendedArtist, new ArtistKey(artist), null,
            null, 0, Array.Empty<string>(), status, DateTimeOffset.UtcNow, null, null);

    // ---- ProcessOne (the consumer's per-item work) ----

    [Fact]
    public async Task Successful_download_marks_the_item_sent()
    {
        _downloader.Request(Arg.Any<PurchaseItem>()).Returns(DownloadOutcome.Success());
        var item = Album("Big Thief", "Capacity", 12345, PurchaseStatus.Queued);
        _repo.Seed(item);

        var ran = await Sut().ProcessOne(item.Id);

        ran.Should().BeTrue();
        _repo.Items.Single().Status.Should().Be(PurchaseStatus.Sent);
    }

    [Fact]
    public async Task Failed_download_marks_the_item_failed()
    {
        _downloader.Request(Arg.Any<PurchaseItem>()).Returns(DownloadOutcome.Failed());
        var item = Album("Big Thief", "Capacity", 12345, PurchaseStatus.Queued);
        _repo.Seed(item);

        await Sut().ProcessOne(item.Id);

        _repo.Items.Single().Status.Should().Be(PurchaseStatus.Failed);
    }

    [Fact]
    public async Task A_thrown_downloader_is_caught_and_the_item_marked_failed()
    {
        _downloader.Request(Arg.Any<PurchaseItem>())
            .Returns<DownloadOutcome>(_ => throw new InvalidOperationException("boom"));
        var item = Album("Big Thief", "Capacity", 12345, PurchaseStatus.Queued);
        _repo.Seed(item);

        await Sut().ProcessOne(item.Id);

        _repo.Items.Single().Status.Should().Be(PurchaseStatus.Failed);
    }

    [Fact]
    public async Task Non_queued_or_non_downloadable_items_are_skipped()
    {
        _downloader.Request(Arg.Any<PurchaseItem>()).Returns(DownloadOutcome.Success());
        _repo.Seed(Album("A", "already-sent", 1, PurchaseStatus.Sent));       // not queued
        _repo.Seed(Album("P", "still-pending", 3));                           // pending, not yet requested
        _repo.Seed(Album("B", "no-id", 0, PurchaseStatus.Queued));            // no deezer id
        _repo.Seed(Artist("Phoebe Bridgers"));                               // artist, not an album

        (await Sut().ProcessOne(PurchaseKey.ForAlbum("A", "already-sent"))).Should().BeFalse();
        (await Sut().ProcessOne(PurchaseKey.ForAlbum("P", "still-pending"))).Should().BeFalse();
        (await Sut().ProcessOne(PurchaseKey.ForAlbum("B", "no-id"))).Should().BeFalse();
        (await Sut().ProcessOne(PurchaseKey.ForArtist("Phoebe Bridgers"))).Should().BeFalse();

        await _downloader.DidNotReceive().Request(Arg.Any<PurchaseItem>());
    }

    // ---- RequestDownload (the manual "Download now" trigger) ----

    [Fact]
    public async Task Manual_request_queues_a_failed_album_for_retry()
    {
        _repo.Seed(Album("Big Thief", "Capacity", 12345, PurchaseStatus.Failed));

        (await Sut().RequestDownload(PurchaseKey.ForAlbum("Big Thief", "Capacity"))).Should().BeTrue();

        _repo.Items.Single().Status.Should().Be(PurchaseStatus.Queued);
    }

    [Fact]
    public async Task Manual_request_rejects_artists_and_unknown_ids()
    {
        _repo.Seed(Artist("Phoebe Bridgers"));

        (await Sut().RequestDownload(PurchaseKey.ForArtist("Phoebe Bridgers"))).Should().BeFalse();
        (await Sut().RequestDownload("nope")).Should().BeFalse();
    }

    // ---- The automatic/manual switch (env default, overridden by the stored value) ----

    [Fact]
    public async Task Automatic_is_on_until_the_switch_is_turned_off()
    {
        // Nothing stored: the drainer runs unattended. There is no env var to say otherwise.
        var settings = new DownloadSettings(_settingsRepo, NullLogger<DownloadSettings>.Instance);

        (await settings.Automatic()).Should().BeTrue();
    }

    [Fact]
    public async Task The_stored_switch_is_what_the_drainer_reads_in_both_directions()
    {
        var settings = new DownloadSettings(_settingsRepo, NullLogger<DownloadSettings>.Instance);

        await settings.SetAutomatic(false);
        (await settings.Automatic()).Should().BeFalse();

        await settings.SetAutomatic(true);
        (await settings.Automatic()).Should().BeTrue();
    }

    [Fact]
    public async Task Automatic_mode_enqueues_pending_downloadable_albums()
    {
        Wanted("Big Thief", "Capacity", deezerId: 12345);
        Wanted("No Id", "Unfetchable", deezerId: null);   // liked, but nothing to fetch it by

        await Sut().EnqueuePendingBatch();

        _repo.Items.Single(i => i.Album == "Capacity").Status.Should().Be(PurchaseStatus.Queued);
        _repo.Items.Single(i => i.Album == "Unfetchable").Status.Should().Be(PurchaseStatus.Pending);
    }

    [Fact]
    public async Task Manual_mode_leaves_pending_albums_alone()
    {
        // Seeded as well as wanted: a manual pass returns before it reconciles, so the row has to
        // already be there for the assertion to mean "left alone" rather than "never created".
        Wanted("Big Thief", "Capacity", deezerId: 12345);
        _repo.Seed(Album("Big Thief", "Capacity", 12345));
        await _settingsRepo.SetDownloadsAutomatic(false);

        await Sut().EnqueuePendingBatch();

        _repo.Items.Single().Status.Should().Be(PurchaseStatus.Pending);
    }

    [Fact]
    public async Task Flipping_the_switch_on_starts_the_next_pass_draining_without_a_restart()
    {
        Wanted("Big Thief", "Capacity", deezerId: 12345);
        _repo.Seed(Album("Big Thief", "Capacity", 12345));
        await _settingsRepo.SetDownloadsAutomatic(false);
        var settings = new DownloadSettings(_settingsRepo, NullLogger<DownloadSettings>.Instance);
        var sut = Sut();

        await sut.EnqueuePendingBatch();
        _repo.Items.Single().Status.Should().Be(PurchaseStatus.Pending);

        await settings.SetAutomatic(true);   // the same store the service reads through
        await sut.EnqueuePendingBatch();

        _repo.Items.Single().Status.Should().Be(PurchaseStatus.Queued);
    }

    // ---- Fast mode (the time-boxed burst that lifts the batch cap) ----

    [Fact]
    public async Task Fast_mode_queues_everything_past_the_batch_cap()
    {
        for (var i = 0; i < 5; i++)
        {
            Wanted("Big Thief", $"Album {i}", deezerId: 100 + i);
        }
        var settings = new DownloadSettings(_settingsRepo, NullLogger<DownloadSettings>.Instance);
        await settings.SetFast(true);

        await Sut(Config(batchSize: 2)).EnqueuePendingBatch();

        _repo.Items.Should().OnlyContain(i => i.Status == PurchaseStatus.Queued);
    }

    [Fact]
    public async Task Without_fast_mode_the_batch_cap_still_holds()
    {
        for (var i = 0; i < 5; i++)
        {
            Wanted("Big Thief", $"Album {i}", deezerId: 100 + i);
        }

        await Sut(Config(batchSize: 2)).EnqueuePendingBatch();

        _repo.Items.Count(i => i.Status == PurchaseStatus.Queued).Should().Be(2);
    }

    [Fact]
    public async Task An_elapsed_fast_deadline_is_over_without_anything_switching_it_off()
    {
        for (var i = 0; i < 5; i++)
        {
            Wanted("Big Thief", $"Album {i}", deezerId: 100 + i);
        }
        // The burst as it looks a minute after it lapsed: still stored, no longer in force.
        await _settingsRepo.SetDownloadsFastUntil(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));

        await Sut(Config(batchSize: 2)).EnqueuePendingBatch();

        _repo.Items.Count(i => i.Status == PurchaseStatus.Queued).Should().Be(2);
        var settings = new DownloadSettings(_settingsRepo, NullLogger<DownloadSettings>.Instance);
        (await settings.FastUntil()).Should().BeNull();
    }

    [Fact]
    public async Task Fast_mode_rechecks_for_new_albums_instead_of_waiting_for_the_next_batch_tick()
    {
        var sut = Sut(Config(batchInterval: TimeSpan.FromMinutes(30)));
        var settings = new DownloadSettings(_settingsRepo, NullLogger<DownloadSettings>.Instance);

        // Normally an album marked just after a pass waits out the whole batch interval...
        (await sut.NextEnqueueWait()).Should().Be(TimeSpan.FromMinutes(30));

        // ...which is exactly what a burst is for: the next gap is seconds, so anything marked while
        // it runs is queued almost at once. Same instance, no restart — the deadline is re-read.
        await settings.SetFast(true);
        (await sut.NextEnqueueWait()).Should().BeLessThan(TimeSpan.FromMinutes(1));

        await settings.SetFast(false);
        (await sut.NextEnqueueWait()).Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task Waking_the_drainer_cuts_the_wait_between_passes_short()
    {
        var sut = Sut(Config(batchInterval: TimeSpan.FromMinutes(30)));

        // Nothing has woken it, so a wait simply expires (zero here, so the test doesn't sleep).
        (await sut.WaitForNextPass(TimeSpan.Zero, CancellationToken.None)).Should().BeFalse();

        // Turning fast mode on wakes it mid-sleep. Without this the burst lifts the batch cap but not
        // the pace — the loop would keep sleeping out the half-hour it entered before the burst began,
        // and an album added in between would sit Pending for the rest of it.
        sut.WakeEnqueue();
        (await sut.WaitForNextPass(TimeSpan.FromMinutes(30), CancellationToken.None)).Should().BeTrue();

        // One wake, one pass: a second wake queued while a pass is already running doesn't stack up.
        sut.WakeEnqueue();
        sut.WakeEnqueue();
        (await sut.WaitForNextPass(TimeSpan.Zero, CancellationToken.None)).Should().BeTrue();
        (await sut.WaitForNextPass(TimeSpan.Zero, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Fast_mode_does_not_drain_while_the_switch_says_manual()
    {
        Wanted("Big Thief", "Capacity", deezerId: 12345);
        _repo.Seed(Album("Big Thief", "Capacity", 12345));
        await _settingsRepo.SetDownloadsAutomatic(false);
        var settings = new DownloadSettings(_settingsRepo, NullLogger<DownloadSettings>.Instance);
        await settings.SetFast(true);

        await Sut().EnqueuePendingBatch();

        _repo.Items.Single().Status.Should().Be(PurchaseStatus.Pending);
    }

    [Fact]
    public async Task Turning_fast_mode_off_ends_the_burst_before_its_hour_is_up()
    {
        var settings = new DownloadSettings(_settingsRepo, NullLogger<DownloadSettings>.Instance);

        var until = await settings.SetFast(true);
        until.Should().BeCloseTo(DateTimeOffset.UtcNow + DownloadSettings.FastDuration, TimeSpan.FromSeconds(5));
        (await settings.FastUntil()).Should().NotBeNull();

        (await settings.SetFast(false)).Should().BeNull();
        (await settings.FastUntil()).Should().BeNull();
    }

    // ---- The settle pass (a downloaded album showing up in the library) ----

    [Fact]
    public async Task Settle_closes_out_a_downloaded_album_once_the_library_reports_it()
    {
        _repo.Seed(Album("Big Thief", "Capacity", 1, PurchaseStatus.Sent) with { SentAt = DateTimeOffset.UtcNow });
        // The file has landed and Plex now lists it, so the refresh this pass triggers finds it owned.
        _catalogRepo.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Big Thief"] = new(StringComparer.Ordinal) { ["capacity"] = null },
        });

        await Sut().SettleOnce();

        await _libraryQuery.Received(1).QueryAllArtistMetadata();
        _repo.Items.Single().Status.Should().Be(PurchaseStatus.InLibrary);
    }

    [Fact]
    public async Task Settle_stamps_the_verdict_mood_on_an_artist_plex_has_only_just_picked_up()
    {
        // The whole after-the-fact case: the like was placed while Plex had no such artist, so the
        // rating wrote no mood. The download lands, the refresh reports the artist newly present, and
        // this pass finally stamps it.
        _repo.Seed(Album("Big Thief", "Capacity", 1, PurchaseStatus.Sent) with { SentAt = DateTimeOffset.UtcNow });
        _catalogRepo.SyncFromLibrary(Arg.Any<IReadOnlyList<ArtistMetadata>>(), Arg.Any<DateTimeOffset>())
            .Returns(new CatalogSyncResult(1, 0, 1, new[] { "Big Thief" }));
        _queue.GetAllUserIds().Returns(new[] { "user-1" });
        _users.Get("user-1").Returns(new AppUser(
            "user-1", "noggog", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        _queue.GetRated("user-1").Returns(new[]
        {
            new ArtistRating(new ArtistKey("Big Thief"), null, DiscoveryStatus.Liked),
        });

        await Sut().SettleOnce();

        await _tagger.Received(1).SetTags("Big Thief", "noggog_liked",
            Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(new[] { "noggog_disliked" })));
    }

    [Fact]
    public async Task Settle_leaves_a_download_that_has_not_landed_yet_alone()
    {
        _repo.Seed(Album("Big Thief", "Capacity", 1, PurchaseStatus.Sent) with { SentAt = DateTimeOffset.UtcNow });

        await Sut().SettleOnce();

        await _libraryQuery.Received(1).QueryAllArtistMetadata();
        _repo.Items.Single().Status.Should().Be(PurchaseStatus.Sent);
    }

    [Fact]
    public async Task Settle_does_not_touch_plex_when_nothing_is_waiting_to_land()
    {
        _repo.Seed(Album("Waiting", "ToDownload", 1));                        // pending, not downloaded
        _repo.Seed(Album("Already", "Home", 2, PurchaseStatus.InLibrary));    // closed out already

        await Sut().SettleOnce();

        await _libraryQuery.DidNotReceive().QueryAllArtistMetadata();
    }

    [Fact]
    public async Task Settle_gives_up_on_a_download_that_never_arrived()
    {
        // Past the settle window: an album Plex files under a title we can't match would otherwise
        // keep re-reading the whole library forever. It's left for the daily sync (or a manual merge).
        _repo.Seed(Album("Big Thief", "Capacity", 1, PurchaseStatus.Sent)
            with { SentAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(9) });

        await Sut(Config(settleWindow: TimeSpan.FromHours(6))).SettleOnce();

        await _libraryQuery.DidNotReceive().QueryAllArtistMetadata();
    }

    // ---- Crash recovery ----

    [Fact]
    public async Task Reset_returns_stranded_downloads_to_pending()
    {
        _repo.Seed(Album("Big Thief", "Capacity", 1, PurchaseStatus.Downloading));
        _repo.Seed(Album("Waiting", "InQueue", 3, PurchaseStatus.Queued));
        _repo.Seed(Album("Other", "Done", 2, PurchaseStatus.Sent));

        await Sut().ResetStuckDownloads();

        _repo.Items.Single(i => i.Album == "Capacity").Status.Should().Be(PurchaseStatus.Pending);
        _repo.Items.Single(i => i.Album == "InQueue").Status.Should().Be(PurchaseStatus.Pending);
        _repo.Items.Single(i => i.Album == "Done").Status.Should().Be(PurchaseStatus.Sent);
    }
}
