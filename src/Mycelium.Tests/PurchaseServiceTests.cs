using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Download;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Deezer.Models;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

public class PurchaseServiceTests
{
    private readonly FakePurchaseRepo _purchases = new();
    private readonly IUserQueueRepo _queue = Substitute.For<IUserQueueRepo>();
    private readonly IUserAlbumRatingRepo _albumRatings = Substitute.For<IUserAlbumRatingRepo>();
    private readonly IUserRepo _users = Substitute.For<IUserRepo>();
    private readonly ILibraryProvider _library = Substitute.For<ILibraryProvider>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly IMissingAlbumRepo _missing = Substitute.For<IMissingAlbumRepo>();
    private readonly FakeAlbumMatchOverrideRepo _overrides = new();
    private readonly IAlbumBlockRepo _blocks = Substitute.For<IAlbumBlockRepo>();
    private readonly IDownloader _downloader = Substitute.For<IDownloader>();
    private readonly IDeezerApi _deezer = Substitute.For<IDeezerApi>();
    private readonly IAlbumTagger _albumTagger = Substitute.For<IAlbumTagger>();
    private readonly DownloadSchedule _schedule = new();
    private readonly PurchaseService _sut;

    private static readonly DownloaderConfig Config = new(
        DownloadDir: "", RipBinary: "rip", Quality: "2", FallbackQualities: new[] { "1", "0" },
        Codec: "", BatchSize: 3, ItemDelay: TimeSpan.Zero, BatchInterval: TimeSpan.Zero,
        DownloadTimeout: TimeSpan.FromMinutes(15), SettleInterval: TimeSpan.FromMinutes(15),
        SettleWindow: TimeSpan.FromHours(6), FastSettleInterval: TimeSpan.Zero,
        FastSettleWindow: TimeSpan.Zero);

    public PurchaseServiceTests()
    {
        var settings = new DownloadSettings(
            new FakeAppSettingsRepo(), NullLogger<DownloadSettings>.Instance);
        _sut = new PurchaseService(
            _purchases, _queue, _albumRatings, _library, _catalog, _missing, _overrides, _blocks,
            _downloader,
            _deezer, _albumTagger, Config, settings,
            new UserQualityService(_users, AudioQuality.Lossless),
            new JitterPolicy(0.3), _schedule,
            NullLogger<PurchaseService>.Instance);

        _queue.GetAllLiked().Returns(Array.Empty<DiscoveryCandidate>());
        _albumRatings.GetAllLiked().Returns(Array.Empty<AlbumRating>());
        _users.GetAll().Returns(Array.Empty<AppUser>());
        _albumRatings.GetAllLikedByUser().Returns(Array.Empty<LikedAlbum>());
        _library.GetAllArtistMetadata().Returns(Array.Empty<ArtistMetadata>());
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase));
        _missing.GetAll().Returns(Array.Empty<MissingAlbum>());
        _blocks.GetAll().Returns(Array.Empty<AlbumBlock>());
        _downloader.Name.Returns("test-backend");
        _downloader.Request(Arg.Any<PurchaseItem>()).Returns(DownloadOutcome.Success());
    }

    /// <summary>
    /// Stubs both reads of the liked albums at once. Reconcile goes through GetAllLikedByUser (it
    /// needs whose entitlement each like carries) while other callers use GetAllLiked, and a test
    /// that set only one would exercise a state the repo can never actually be in. Everything is
    /// attributed to one user; the cases that care about who liked what use LikedBy instead.
    /// </summary>
    private void AllLiked(AlbumRating[] liked)
    {
        _albumRatings.GetAllLiked().Returns(liked);
        _albumRatings.GetAllLikedByUser().Returns(
            liked.Select(r => new LikedAlbum("test-user", r)).ToArray());
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
        AllLiked(new[]
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
        AllLiked(new[]
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

        AllLiked(new[]
        {
            new AlbumRating(new ArtistKey("Milo"), new AlbumKey(likedTitle), "art", DiscoveryStatus.Liked),
        });
        await _sut.Reconcile();
        await _purchases.SetStatus(PurchaseKey.ForAlbum("Milo", likedTitle), PurchaseStatus.Sent);

        // It has since landed in Plex under the typographically-different title.
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Milo"] = new(StringComparer.OrdinalIgnoreCase) { [plexTitle] = null },
        });

        var active = await _sut.GetActive();

        active.Should().BeEmpty();
        _purchases.Items.Single().Status.Should().Be(PurchaseStatus.InLibrary);
    }

    [Fact]
    public async Task A_sent_edition_closes_out_when_plex_files_it_under_the_plain_title()
    {
        // Plex names an album from its own metadata match, which drops the edition decoration the
        // release was fetched under. Ownership is asked at record granularity precisely so the row can
        // see its own download arrive instead of sitting in Sent for ever.
        const string deezerTitle = "Light Upon the Lake (10th Anniversary Edition)";
        AllLiked(new[]
        {
            new AlbumRating(new ArtistKey("Whitney"), new AlbumKey(deezerTitle), "art", DiscoveryStatus.Liked),
        });
        await _sut.Reconcile();
        await _purchases.SetStatus(PurchaseKey.ForAlbum("Whitney", deezerTitle), PurchaseStatus.Sent);

        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Whitney"] = new(StringComparer.OrdinalIgnoreCase) { ["Light Upon the Lake"] = null },
        });

        var active = await _sut.GetActive();

        active.Should().BeEmpty();
        _purchases.Items.Single().Status.Should().Be(PurchaseStatus.InLibrary);
        // No merge override needed: the diff asks the same question with the same key, so it already
        // agrees the record is owned. Overrides are for titles that differ beyond the decoration.
        _overrides.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sent_album_closes_out_when_plex_spells_the_artist_with_a_different_hyphen()
    {
        // The real case, 2026-08-27: Plex files Sophie Ellis-Bextor with U+2010 HYPHEN, Deezer writes
        // U+002D HYPHEN-MINUS. The two are indistinguishable on screen, which is why this went unspotted
        // for so long and kept being read as an album-title problem.
        //
        // It is not: the artist is looked up FIRST, so an unfolded name short-circuits the title
        // comparison before the (perfectly working) edition trim ever runs. The row sat in Sent, the
        // Download page offered "Match Existing Album", and a human had to click it.
        const string deezerArtist = "Sophie Ellis-Bextor";          // U+002D
        const string plexArtist = "Sophie Ellis\u2010Bextor";       // U+2010
        const string deezerTitle = "Read My Lips (Deluxe Version)";

        AllLiked(new[]
        {
            new AlbumRating(new ArtistKey(deezerArtist), new AlbumKey(deezerTitle), "art", DiscoveryStatus.Liked),
        });
        await _sut.Reconcile();
        await _purchases.SetStatus(PurchaseKey.ForAlbum(deezerArtist, deezerTitle), PurchaseStatus.Sent);

        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            [plexArtist] = new(StringComparer.OrdinalIgnoreCase) { ["Read My Lips"] = null },
        });

        var active = await _sut.GetActive();

        active.Should().BeEmpty();
        _purchases.Items.Single().Status.Should().Be(PurchaseStatus.InLibrary);
        // Nothing to record: the fold makes the two spellings one act, so the diff already agrees.
        // A merge override here would mean we had papered over the mismatch instead of fixing it.
        _overrides.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sent_album_closes_out_when_plex_spells_the_artist_with_a_curly_apostrophe()
    {
        // Same shape, the other spelling both sources disagree on. 17 acts in the real library carry
        // a curly apostrophe where Deezer writes a straight one.
        const string deezerArtist = "Keston Cobblers' Club";
        const string plexArtist = "Keston Cobblers\u2019 Club";

        AllLiked(new[]
        {
            new AlbumRating(new ArtistKey(deezerArtist), new AlbumKey("Almost Home"), "art", DiscoveryStatus.Liked),
        });
        await _sut.Reconcile();
        await _purchases.SetStatus(PurchaseKey.ForAlbum(deezerArtist, "Almost Home"), PurchaseStatus.Sent);

        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            [plexArtist] = new(StringComparer.OrdinalIgnoreCase) { ["Almost Home"] = null },
        });

        (await _sut.GetActive()).Should().BeEmpty();
        _purchases.Items.Single().Status.Should().Be(PurchaseStatus.InLibrary);
    }

    [Fact]
    public async Task A_sent_remix_closes_out_when_deezer_abbreviates_the_artist_with_a_period()
    {
        // The second real case: title matched all along ("mi gente (steve aoki remix)" both sides —
        // the remix bracket is kept, and kept identically), but Deezer writes "J. Balvin" where Plex
        // writes "J Balvin". Same short-circuit as the hyphen: the artist gate fails first.
        const string deezerArtist = "J. Balvin";
        const string plexArtist = "J Balvin";
        const string deezerTitle = "Mi Gente (Steve Aoki Remix)";

        AllLiked(new[]
        {
            new AlbumRating(new ArtistKey(deezerArtist), new AlbumKey(deezerTitle), "art", DiscoveryStatus.Liked),
        });
        await _sut.Reconcile();
        await _purchases.SetStatus(PurchaseKey.ForAlbum(deezerArtist, deezerTitle), PurchaseStatus.Sent);

        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            [plexArtist] = new(StringComparer.OrdinalIgnoreCase) { ["Mi gente (Steve Aoki remix)"] = null },
        });

        (await _sut.GetActive()).Should().BeEmpty();
        _purchases.Items.Single().Status.Should().Be(PurchaseStatus.InLibrary);
        _overrides.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_acts_that_fold_together_keep_both_their_albums()
    {
        // The one way this fold could make matching worse: if the owned map overwrote one act with its
        // near-twin, albums would vanish and read as gaps. Both spellings' albums have to survive.
        AllLiked(new[]
        {
            new AlbumRating(new ArtistKey("Sophie Ellis-Bextor"), new AlbumKey("Trip the Light Fantastic"), "art", DiscoveryStatus.Liked),
        });
        await _sut.Reconcile();
        await _purchases.SetStatus(
            PurchaseKey.ForAlbum("Sophie Ellis-Bextor", "Trip the Light Fantastic"), PurchaseStatus.Sent);

        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Sophie Ellis\u2010Bextor"] = new(StringComparer.OrdinalIgnoreCase) { ["Read My Lips"] = null },
            ["Sophie Ellis-Bextor"] = new(StringComparer.OrdinalIgnoreCase) { ["Trip the Light Fantastic"] = null },
        });

        (await _sut.GetActive()).Should().BeEmpty();
        _purchases.Items.Single().Status.Should().Be(PurchaseStatus.InLibrary);
    }

    [Fact]
    public async Task Owning_the_plain_release_closes_out_a_want_for_the_edition()
    {
        // The library holds one copy of a record, under whatever title Plex gave it, and that copy is
        // the answer to "do we have this album?" whichever pressing Deezer listed. Queuing the deluxe
        // when the record is already on the shelf would download what we have.
        const string deezerTitle = "Light Upon the Lake (10th Anniversary Edition)";
        AllLiked(new[]
        {
            new AlbumRating(new ArtistKey("Whitney"), new AlbumKey(deezerTitle), "art", DiscoveryStatus.Liked),
        });
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Whitney"] = new(StringComparer.OrdinalIgnoreCase) { ["Light Upon the Lake"] = null },
        });

        var active = await _sut.GetActive();

        // Never queued at all: the want is satisfied by the copy already on the shelf.
        active.Should().BeEmpty();
        _purchases.Items.Should().BeEmpty();
        _overrides.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sent_row_stays_open_when_only_a_different_record_is_in_the_library()
    {
        // The record fold is not a fuzzy match: a tail that carries meaning ("(Remixes)") is a
        // different record, and its download has not landed just because the original is on the shelf.
        AllLiked(new[]
        {
            new AlbumRating(
                new ArtistKey("Pretty Lights"), new AlbumKey("A Color Map of the Sun (Remixes)"),
                "art", DiscoveryStatus.Liked),
        });
        await _sut.Reconcile();
        await _purchases.SetStatus(
            PurchaseKey.ForAlbum("Pretty Lights", "A Color Map of the Sun (Remixes)"),
            PurchaseStatus.Sent);

        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Pretty Lights"] = new(StringComparer.OrdinalIgnoreCase) { ["A Color Map of the Sun"] = null },
        });

        var active = await _sut.GetActive();

        active.Single().Status.Should().Be(PurchaseStatus.Sent);
        _overrides.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Album_owned_under_a_different_album_artist_closes_out_to_in_library()
    {
        // A collaboration surfaced/liked under "Milo", but the library files it under the duo
        // "Nostrum Grocers" (Deezer's album-artist, carried on the missing record). Reconcile must
        // match ownership under the album-artist, not the display artist, and close the row out.
        AllLiked(new[]
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
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Nostrum Grocers"] = new(StringComparer.OrdinalIgnoreCase) { ["Nostrum Grocers"] = null },
        });

        var active = await _sut.GetActive();

        active.Should().BeEmpty();
        _purchases.Items.Single().Status.Should().Be(PurchaseStatus.InLibrary);
    }

    [Fact]
    public async Task Album_items_carry_the_deezer_album_id_from_the_missing_set()
    {
        AllLiked(new[]
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

    // --- The ids filter ---------------------------------------------------------------------------

    /// <summary>
    /// Seeds three liked albums with Deezer ids, plus a liked artist row (which has no id at all), and
    /// returns the whole active list. The queue is shared, so this stands in for the maintainer's list
    /// that a client asking about its own albums has to see past.
    /// </summary>
    private async Task SeedQueue()
    {
        AllLiked(new[]
        {
            new AlbumRating(new ArtistKey("Big Thief"), new AlbumKey("Capacity"), "art", DiscoveryStatus.Liked),
            new AlbumRating(new ArtistKey("Milo"), new AlbumKey("Nostrum Grocers"), "art", DiscoveryStatus.Liked),
            new AlbumRating(new ArtistKey("Autechre"), new AlbumKey("Amber"), "art", DiscoveryStatus.Liked),
        });
        _queue.GetAllLiked().Returns(new[]
        {
            new DiscoveryCandidate(new ArtistKey("Phoebe Bridgers"), null, 3, Array.Empty<string>(), 1),
        });
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(new ArtistKey("Big Thief"), new AlbumKey("Capacity"), "art", 11),
            new MissingAlbum(new ArtistKey("Milo"), new AlbumKey("Nostrum Grocers"), "art", 22),
            new MissingAlbum(new ArtistKey("Autechre"), new AlbumKey("Amber"), "art", 33),
        });
        await _sut.Reconcile();
    }

    /// <summary>
    /// The point of the filter. A client that queued two albums asks about those two and gets those
    /// two — not the rest of the shared queue, which it has no interest in and which only grows.
    /// </summary>
    [Fact]
    public async Task The_ids_filter_returns_only_the_albums_asked_about()
    {
        await SeedQueue();

        var active = await _sut.GetActive(new long[] { 11, 33 });

        active.Select(p => p.DeezerAlbumId).Should().BeEquivalentTo(new long?[] { 11, 33 });
        // The artist row has no Deezer id, so it can never match — which is right: the filter is asked
        // in ids, and an artist has none to be asked about by.
        active.Should().OnlyContain(p => p.Kind == FeedKind.MissingAlbum);
    }

    /// <summary>
    /// Without ids nothing changes: the whole active queue, artist rows included. This is what the
    /// Download page reads, and it must not have been narrowed by the filter's arrival.
    /// </summary>
    [Fact]
    public async Task No_ids_still_returns_the_whole_active_queue()
    {
        await SeedQueue();

        var active = await _sut.GetActive();

        active.Should().HaveCount(4);
        active.Should().Contain(p => p.Kind == FeedKind.RecommendedArtist && p.DeezerAlbumId == null);
    }

    /// <summary>
    /// An id nothing on the queue carries is simply absent from the answer rather than an error: the
    /// caller is polling, and "not on the list yet" is a normal state on the way to "landed".
    /// </summary>
    [Fact]
    public async Task An_unknown_id_is_absent_rather_than_an_error()
    {
        await SeedQueue();

        var active = await _sut.GetActive(new long[] { 11, 9999 });

        active.Should().ContainSingle().Which.DeezerAlbumId.Should().Be(11);
    }

    /// <summary>
    /// Asking about no ids is answered with nothing, not with everything. "Tell me about these" with an
    /// empty set requests nothing, and the whole shared queue is the one reading no caller could mean —
    /// it is also the reading that would quietly turn a filtered poll into a full one.
    /// </summary>
    [Fact]
    public async Task An_empty_ids_filter_returns_nothing_rather_than_everything()
    {
        await SeedQueue();

        (await _sut.GetActive(Array.Empty<long>())).Should().BeEmpty();
    }

    [Fact]
    public async Task Failed_items_stay_on_the_active_list_for_retry()
    {
        AllLiked(new[]
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
        AllLiked(new[]
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
        AllLiked(new[]
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
        AllLiked(new[]
        {
            new AlbumRating(new ArtistKey("Mick Gordon"), new AlbumKey(deezerTitle), "art", DiscoveryStatus.Liked),
        });
        await _sut.Reconcile();
        var id = PurchaseKey.ForAlbum("Mick Gordon", deezerTitle);
        (await _sut.GetActive()).Single(p => p.Id == id).Status.Should().Be(PurchaseStatus.Pending);

        // The library owns it under the near-miss title; the user merges the two by hand.
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mick Gordon"] = new(StringComparer.OrdinalIgnoreCase) { [plexTitle] = null },
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
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Matthewdavid"] = new(StringComparer.OrdinalIgnoreCase) { ["Outmind"] = null },
            ["Matthewdavid's Mindflight"] = new(StringComparer.OrdinalIgnoreCase) { ["Care Tracts"] = null },
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

    // ---- Hand-added albums (pasted Deezer link) ----------------------------------------------

    /// <summary>The compilation from the Deezer docs example: credited to the "Various Artists"
    /// placeholder, in no contributor's discography, so nothing but a paste can reach it.</summary>
    private void DeezerAlbum(long id, string title, string? artist)
    {
        _deezer.GetAlbum(id).Returns(new Mycelium.Deezer.Models.DeezerAlbum
        {
            id = id,
            title = title,
            record_type = "album",
            cover_big = "http://art/cover.jpg",
            artist = artist is null ? null : new DeezerArtist { name = artist },
        });
    }

    [Fact]
    public async Task Manual_add_queues_a_various_artists_compilation_with_its_deezer_id()
    {
        DeezerAlbum(225323002, "Cluster Flies", "Various Artists");

        var outcome = await _sut.AddManual("https://www.deezer.com/en/album/225323002");

        outcome.Result.Should().Be(ManualAddResult.Added);
        var row = _purchases.Items.Should().ContainSingle().Subject;
        row.Kind.Should().Be(FeedKind.MissingAlbum);
        row.Artist.ArtistName.Should().Be("Various Artists");
        row.Album.Should().Be("Cluster Flies");
        // The id is the whole point: without it the downloader has nothing to fetch.
        row.DeezerAlbumId.Should().Be(225323002);
        row.Manual.Should().BeTrue();
        row.Status.Should().Be(PurchaseStatus.Pending);
        // Filed under the act the library will file it under, so the arrival check can close it out.
        row.AlbumArtist.Should().Be("Various Artists");
    }

    [Fact]
    public async Task Manual_row_survives_reconcile_even_though_nothing_rated_it()
    {
        // The regression this flag exists for. Nothing likes a pasted album, so it can never appear in
        // the desired set — and the page reconciles on every read, so an unguarded row would be deleted
        // before the user who pasted it could press Download.
        DeezerAlbum(225323002, "Cluster Flies", "Various Artists");
        await _sut.AddManual("https://www.deezer.com/en/album/225323002");

        var active = await _sut.GetActive();

        active.Should().ContainSingle().Which.Album.Should().Be("Cluster Flies");
        // And its Deezer id survives the reconcile's upsert, which supplies none for a row the missing
        // set doesn't know about.
        active[0].DeezerAlbumId.Should().Be(225323002);
    }

    [Fact]
    public async Task Manual_row_closes_out_once_the_album_lands_in_the_library()
    {
        DeezerAlbum(225323002, "Cluster Flies", "Various Artists");
        await _sut.AddManual("https://www.deezer.com/en/album/225323002");

        // Plex files a compilation under its "Various Artists" bucket, which is exactly the act the row
        // was filed under — so the ordinary ownership check closes the loop, with no special case.
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Various Artists"] = new(StringComparer.OrdinalIgnoreCase) { ["Cluster Flies"] = null },
        });

        (await _sut.GetActive()).Should().BeEmpty();
        _purchases.Items.Should().ContainSingle()
            .Which.Status.Should().Be(PurchaseStatus.InLibrary);
    }

    [Fact]
    public async Task Added_credit_rides_the_row_and_is_stamped_on_the_album_when_it_lands()
    {
        // The whole point of carrying AddedBy: at the moment of the paste there is no Plex album to
        // write to. The credit waits on the row for however long the download takes, and is written
        // exactly once — when the record finally shows up in the library.
        DeezerAlbum(225323002, "Cluster Flies", "Various Artists");
        await _sut.AddManual("https://www.deezer.com/en/album/225323002", "noggog");

        _purchases.Items.Should().ContainSingle().Which.AddedBy.Should().Be("noggog");
        await _albumTagger.DidNotReceiveWithAnyArgs().SetTags(default!, default!, default, default!);

        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Various Artists"] = new(StringComparer.OrdinalIgnoreCase) { ["Cluster Flies"] = null },
        });
        await _sut.GetActive();

        await _albumTagger.Received(1).SetTags(
            "Various Artists", "Cluster Flies", "noggog_added",
            Arg.Is<IReadOnlyCollection<string>>(r => r.Count == 0));
    }

    // --- The terminal marker -----------------------------------------------------------------------

    /// <summary>
    /// The row saying it is finished, rather than a caller inferring it. Status flips to InLibrary at
    /// the same moment, but a client polling the list only has an enum to read — and every other value
    /// in it is non-terminal.
    /// </summary>
    [Fact]
    public async Task An_album_that_arrives_is_stamped_with_when_it_landed()
    {
        var before = DateTimeOffset.UtcNow;
        AllLiked(new[]
        {
            new AlbumRating(new ArtistKey("Big Thief"), new AlbumKey("Capacity"), "art", DiscoveryStatus.Liked),
        });
        await _sut.GetActive();

        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Big Thief"] = new(StringComparer.OrdinalIgnoreCase) { ["Capacity"] = null },
        });
        await _sut.GetActive();

        var row = _purchases.Items.Should().ContainSingle().Subject;
        row.Status.Should().Be(PurchaseStatus.InLibrary);
        row.InLibraryAt.Should().NotBeNull().And.BeOnOrAfter(before);
    }

    /// <summary>
    /// The other half of the contract: an unfinished row must not carry the stamp, or it says nothing.
    /// Sent is the case that matters — downloaded, but not yet scanned into the library, which is
    /// exactly the state a caller would otherwise mistake for done.
    /// </summary>
    [Fact]
    public async Task A_row_that_has_not_arrived_carries_no_stamp()
    {
        AllLiked(new[]
        {
            new AlbumRating(new ArtistKey("Big Thief"), new AlbumKey("Capacity"), "art", DiscoveryStatus.Liked),
        });
        await _sut.GetActive();
        await _purchases.SetStatus(PurchaseKey.ForAlbum("Big Thief", "Capacity"), PurchaseStatus.Sent);

        var active = await _sut.GetActive();

        var row = active.Should().ContainSingle().Subject;
        row.Status.Should().Be(PurchaseStatus.Sent);
        row.SentAt.Should().NotBeNull();
        row.InLibraryAt.Should().BeNull();
    }

    /// <summary>
    /// The stamp names the arrival, not the most recent reconcile. GetActive reconciles on every read
    /// of the page and an InLibrary row stays in the store for ever, so a stamp rewritten each pass
    /// would report "landed just now" indefinitely.
    /// </summary>
    [Fact]
    public async Task The_arrival_stamp_is_not_rewritten_by_the_reconciles_that_follow()
    {
        AllLiked(new[]
        {
            new AlbumRating(new ArtistKey("Big Thief"), new AlbumKey("Capacity"), "art", DiscoveryStatus.Liked),
        });
        await _sut.GetActive();
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Big Thief"] = new(StringComparer.OrdinalIgnoreCase) { ["Capacity"] = null },
        });
        await _sut.GetActive();
        var landed = _purchases.Items.Single().InLibraryAt;

        await _sut.GetActive();
        await _sut.GetActive();

        _purchases.Items.Single().InLibraryAt.Should().Be(landed);
    }

    /// <summary>
    /// A manual row has no rating behind it, so it closes out purely on the library seeing the record —
    /// which is the case the marker was asked for: a script pastes a link and polls for the answer.
    /// </summary>
    [Fact]
    public async Task A_hand_added_album_is_stamped_when_the_library_finally_has_it()
    {
        DeezerAlbum(225323002, "Cluster Flies", "Various Artists");
        await _sut.AddManual("https://www.deezer.com/en/album/225323002", "noggog");
        _purchases.Items.Should().ContainSingle().Which.InLibraryAt.Should().BeNull();

        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Various Artists"] = new(StringComparer.OrdinalIgnoreCase) { ["Cluster Flies"] = null },
        });
        await _sut.GetActive();

        _purchases.Items.Should().ContainSingle().Which.InLibraryAt.Should().NotBeNull();
    }

    /// <summary>
    /// The stamp is only worth writing if something can read it, and the active list is exactly the
    /// view that drops the rows carrying it. Without this the caller sees the row vanish — which looks
    /// identical to the row being pruned because nobody wants the album any more.
    /// </summary>
    [Fact]
    public async Task An_arrived_row_comes_back_only_when_completed_ones_are_asked_for()
    {
        DeezerAlbum(225323002, "Cluster Flies", "Various Artists");
        await _sut.AddManual("https://www.deezer.com/en/album/225323002", "noggog");
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Various Artists"] = new(StringComparer.OrdinalIgnoreCase) { ["Cluster Flies"] = null },
        });

        (await _sut.GetActive()).Should().BeEmpty();

        var completed = await _sut.GetActive(includeCompleted: true);
        var row = completed.Should().ContainSingle().Subject;
        row.Status.Should().Be(PurchaseStatus.InLibrary);
        row.InLibraryAt.Should().NotBeNull();
    }

    /// <summary>
    /// The two narrowings compose, which is the shape an automation client actually asks for: "these
    /// ids, landed ones included". Either alone answers the wrong question — ids alone drops the very
    /// arrival it is waiting for, completed alone hands back the whole shared queue.
    /// </summary>
    [Fact]
    public async Task Asking_by_id_for_completed_rows_returns_that_albums_arrival()
    {
        DeezerAlbum(225323002, "Cluster Flies", "Various Artists");
        DeezerAlbum(111, "Someone Elses Record", "Big Thief");
        await _sut.AddManual("https://www.deezer.com/en/album/225323002", "noggog");
        await _sut.AddManual("https://www.deezer.com/en/album/111", "someone-else");
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Various Artists"] = new(StringComparer.OrdinalIgnoreCase) { ["Cluster Flies"] = null },
        });

        var mine = await _sut.GetActive(new long[] { 225323002 }, includeCompleted: true);

        var row = mine.Should().ContainSingle().Subject;
        row.DeezerAlbumId.Should().Be(225323002);
        row.InLibraryAt.Should().NotBeNull();
    }

    /// <summary>
    /// The default is unchanged, because the Download page reads it: an arrived row must not come back
    /// just because the caller narrowed by id.
    /// </summary>
    [Fact]
    public async Task Asking_by_id_still_hides_arrived_rows_by_default()
    {
        DeezerAlbum(225323002, "Cluster Flies", "Various Artists");
        await _sut.AddManual("https://www.deezer.com/en/album/225323002", "noggog");
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Various Artists"] = new(StringComparer.OrdinalIgnoreCase) { ["Cluster Flies"] = null },
        });

        (await _sut.GetActive(new long[] { 225323002 })).Should().BeEmpty();
    }

    [Fact]
    public async Task Added_credit_is_written_once_not_on_every_later_reconcile()
    {
        // GetActive reconciles on every read of the page, and an InLibrary row stays in the store for
        // ever. Re-stamping would be a Plex round trip per owned row per page load.
        DeezerAlbum(225323002, "Cluster Flies", "Various Artists");
        await _sut.AddManual("https://www.deezer.com/en/album/225323002", "noggog");
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Various Artists"] = new(StringComparer.OrdinalIgnoreCase) { ["Cluster Flies"] = null },
        });

        await _sut.GetActive();
        await _sut.GetActive();
        await _sut.GetActive();

        await _albumTagger.Received(1).SetTags(
            "Various Artists", "Cluster Flies", "noggog_added", Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task An_album_that_arrived_with_nobody_asking_for_it_is_credited_to_nobody()
    {
        // Downloaded automatically off a like: nobody pressed anything, so there is no one to credit
        // and no tag to write. A "_added" mood must mean a person, or it means nothing.
        AllLiked(new[]
        {
            new AlbumRating(new ArtistKey("Big Thief"), new AlbumKey("Capacity"), "art", DiscoveryStatus.Liked),
        });
        await _sut.GetActive();
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Big Thief"] = new(StringComparer.OrdinalIgnoreCase) { ["Capacity"] = null },
        });

        await _sut.GetActive();

        _purchases.Items.Should().ContainSingle().Which.Status.Should().Be(PurchaseStatus.InLibrary);
        await _albumTagger.DidNotReceiveWithAnyArgs().SetTags(default!, default!, default, default!);
    }

    [Fact]
    public async Task Added_credit_survives_the_reconciles_that_happen_before_the_album_lands()
    {
        // The reconcile re-upserts every row it still wants, refreshing display fields. That upsert
        // carries no AddedBy — a manual row is in nobody's liked set — and must not wipe the credit.
        DeezerAlbum(225323002, "Cluster Flies", "Various Artists");
        await _sut.AddManual("https://www.deezer.com/en/album/225323002", "noggog");

        await _sut.GetActive();
        await _sut.GetActive();

        _purchases.Items.Should().ContainSingle().Which.AddedBy.Should().Be("noggog");
    }

    [Fact]
    public async Task Manual_add_without_a_signed_in_username_simply_has_no_credit()
    {
        DeezerAlbum(225323002, "Cluster Flies", "Various Artists");

        await _sut.AddManual("https://www.deezer.com/en/album/225323002");

        _purchases.Items.Should().ContainSingle().Which.AddedBy.Should().BeNull();
    }

    [Fact]
    public async Task Manual_add_rejects_a_paste_it_cannot_read_without_calling_deezer()
    {
        var outcome = await _sut.AddManual("https://www.deezer.com/en/artist/5080");

        outcome.Result.Should().Be(ManualAddResult.BadLink);
        _purchases.Items.Should().BeEmpty();
        await _deezer.DidNotReceive().GetAlbum(Arg.Any<long>());
    }

    [Fact]
    public async Task Manual_add_reports_an_album_deezer_does_not_know()
    {
        _deezer.GetAlbum(1).Returns((Mycelium.Deezer.Models.DeezerAlbum?)null);

        (await _sut.AddManual("1")).Result.Should().Be(ManualAddResult.NotFound);
        _purchases.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Manual_add_of_something_already_queued_reports_it_rather_than_duplicating()
    {
        DeezerAlbum(225323002, "Cluster Flies", "Various Artists");
        await _sut.AddManual("225323002");

        var outcome = await _sut.AddManual("https://www.deezer.com/album/225323002?utm_source=deezer");

        outcome.Result.Should().Be(ManualAddResult.AlreadyQueued);
        outcome.Item!.Album.Should().Be("Cluster Flies");
        _purchases.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Manual_add_of_an_owned_album_reports_it_rather_than_queueing_a_redundant_grab()
    {
        DeezerAlbum(99, "Farmhouse", "Phish");
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Phish"] = new(StringComparer.OrdinalIgnoreCase) { ["Farmhouse"] = null },
        });

        (await _sut.AddManual("99")).Result.Should().Be(ManualAddResult.AlreadyOwned);
        _purchases.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Manual_add_files_an_uncredited_album_under_the_placeholder_the_library_uses()
    {
        // Deezer occasionally returns an album with no credited act at all. "Various Artists" is the
        // honest reading and, more usefully, the bucket a library will file it under — so the row can
        // still close itself out on arrival.
        DeezerAlbum(7, "Untitled Comp", null);

        (await _sut.AddManual("7")).Result.Should().Be(ManualAddResult.Added);
        _purchases.Items.Should().ContainSingle()
            .Which.Artist.ArtistName.Should().Be("Various Artists");
    }

    [Fact]
    public async Task Removing_a_manual_row_deletes_it_outright()
    {
        // It has no rating to clear, so the ordinary "nevermind" path (drop the like, let the
        // reconcile prune) can't reach it — a direct delete is the only way it leaves.
        DeezerAlbum(225323002, "Cluster Flies", "Various Artists");
        await _sut.AddManual("225323002");

        (await _sut.RemoveManual(PurchaseKey.ForAlbum("Various Artists", "Cluster Flies")))
            .Should().BeTrue();
        _purchases.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Removing_a_rating_derived_row_this_way_is_refused()
    {
        // Deleting one directly would only have the next reconcile re-add it from the still-standing
        // like, which would read as a button that does nothing.
        AllLiked(new[]
        {
            new AlbumRating(new ArtistKey("Phish"), new AlbumKey("Farmhouse"), null, DiscoveryStatus.Liked),
        });
        await _sut.Reconcile();
        var id = PurchaseKey.ForAlbum("Phish", "Farmhouse");

        (await _sut.RemoveManual(id)).Should().BeFalse();
        _purchases.Items.Should().ContainSingle().Which.Id.Should().Be(id);
    }

    [Fact]
    public async Task Removing_an_unknown_row_is_refused()
    {
        (await _sut.RemoveManual("album:nobody nothing")).Should().BeFalse();
    }

    // ---- Per-row target quality: whose entitlement a shared album is fetched at ----

    /// <summary>Stubs the liked-albums read with explicit per-user attribution.</summary>
    private void LikedBy(params (string UserId, string Artist, string Album)[] likes)
    {
        var rows = likes
            .Select(l => new LikedAlbum(
                l.UserId,
                new AlbumRating(new ArtistKey(l.Artist), new AlbumKey(l.Album), null, DiscoveryStatus.Liked)))
            .ToArray();
        _albumRatings.GetAllLikedByUser().Returns(rows);
        _albumRatings.GetAllLiked().Returns(rows.Select(r => r.Rating).ToArray());
    }

    private void UserTier(string subject, AudioQuality quality) =>
        _users.Get(subject).Returns(new AppUser(subject, subject, null, null, default, default, quality));

    [Fact]
    public async Task An_album_only_a_lossy_user_wants_is_queued_at_the_lossy_tier()
    {
        UserTier("kelsey", AudioQuality.Lossy);
        LikedBy(("kelsey", "Alvvays", "Blue Rev"));

        var active = await _sut.GetActive();

        active.Single(p => p.Kind == FeedKind.MissingAlbum).TargetQuality
            .Should().Be(AudioQuality.Lossy);
    }

    [Fact]
    public async Task An_album_two_users_want_is_queued_at_the_better_of_their_tiers()
    {
        UserTier("kelsey", AudioQuality.Lossy);
        UserTier("justin", AudioQuality.Lossless);
        LikedBy(
            ("kelsey", "Alvvays", "Blue Rev"),
            ("justin", "Alvvays", "Blue Rev"));

        var active = await _sut.GetActive();

        // One global list, so the album is fetched once. Taking the best entitlement lets the lossy
        // user ride along for free; taking the worst would quietly cheat the lossless one.
        active.Single(p => p.Kind == FeedKind.MissingAlbum).TargetQuality
            .Should().Be(AudioQuality.Lossless);
    }

    [Fact]
    public async Task A_later_liker_with_a_better_tier_raises_the_target_before_it_downloads()
    {
        UserTier("kelsey", AudioQuality.Lossy);
        UserTier("justin", AudioQuality.Lossless);
        LikedBy(("kelsey", "Alvvays", "Blue Rev"));
        await _sut.GetActive();

        // Justin likes the same album afterwards. The row already exists as Pending, and Upsert
        // deliberately doesn't touch status — but the target is derived from who currently wants it,
        // so it has to be re-Set, or his request would be silently satisfied by her 320.
        LikedBy(
            ("kelsey", "Alvvays", "Blue Rev"),
            ("justin", "Alvvays", "Blue Rev"));
        var active = await _sut.GetActive();

        active.Single(p => p.Kind == FeedKind.MissingAlbum).TargetQuality
            .Should().Be(AudioQuality.Lossless);
    }

    [Fact]
    public async Task A_user_with_no_tier_falls_to_the_deployment_default()
    {
        // _users.Get returns null for an unstubbed subject — a user who has never been given a tier.
        LikedBy(("someone", "Alvvays", "Blue Rev"));

        var active = await _sut.GetActive();

        // These tests construct UserQualityService with a Lossless default.
        active.Single(p => p.Kind == FeedKind.MissingAlbum).TargetQuality
            .Should().Be(AudioQuality.Lossless);
    }

    // ---- Re-fetching when the target outgrows what was actually downloaded ----

    [Fact]
    public async Task A_row_downloaded_below_what_is_now_wanted_is_sent_back_to_be_refetched()
    {
        // Kelsey liked it first, so it came down as 320 and closed out. Justin then likes the same
        // album. Ownership is a boolean, so without this the row simply reads as "we have it" and his
        // request is silently satisfied by her copy.
        UserTier("kelsey", AudioQuality.Lossy);
        UserTier("justin", AudioQuality.Lossless);
        _purchases.Seed(new PurchaseItem(
            PurchaseKey.ForAlbum("Alvvays", "Blue Rev"), FeedKind.MissingAlbum,
            new ArtistKey("Alvvays"), "Blue Rev", null, 0, Array.Empty<string>(),
            PurchaseStatus.Sent, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1,
            AcquiredQuality: AudioQuality.Lossy));
        LikedBy(("kelsey", "Alvvays", "Blue Rev"), ("justin", "Alvvays", "Blue Rev"));

        var active = await _sut.GetActive();

        var row = active.Single(p => p.Kind == FeedKind.MissingAlbum);
        row.Status.Should().Be(PurchaseStatus.Pending);
        row.TargetQuality.Should().Be(AudioQuality.Lossless);
    }

    [Fact]
    public async Task A_row_that_already_got_what_is_wanted_is_left_alone()
    {
        UserTier("justin", AudioQuality.Lossless);
        _purchases.Seed(new PurchaseItem(
            PurchaseKey.ForAlbum("Alvvays", "Blue Rev"), FeedKind.MissingAlbum,
            new ArtistKey("Alvvays"), "Blue Rev", null, 0, Array.Empty<string>(),
            PurchaseStatus.Sent, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1,
            AcquiredQuality: AudioQuality.Lossless));
        LikedBy(("justin", "Alvvays", "Blue Rev"));

        var active = await _sut.GetActive();

        active.Single(p => p.Kind == FeedKind.MissingAlbum).Status.Should().Be(PurchaseStatus.Sent);
    }

    [Fact]
    public async Task A_row_that_never_recorded_what_it_got_is_not_refetched()
    {
        // Every row written before quality tracking looks like this. Treating "we don't know" as
        // "it fell short" would re-queue the entire back catalogue on the first reconcile.
        UserTier("justin", AudioQuality.Lossless);
        _purchases.Seed(new PurchaseItem(
            PurchaseKey.ForAlbum("Alvvays", "Blue Rev"), FeedKind.MissingAlbum,
            new ArtistKey("Alvvays"), "Blue Rev", null, 0, Array.Empty<string>(),
            PurchaseStatus.Sent, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1));
        LikedBy(("justin", "Alvvays", "Blue Rev"));

        var active = await _sut.GetActive();

        active.Single(p => p.Kind == FeedKind.MissingAlbum).Status.Should().Be(PurchaseStatus.Sent);
    }

    /// <summary>
    /// Owned at <paramref name="quality"/> — the shape that makes a liked album an upgrade rather
    /// than a gap.
    /// </summary>
    private void OwnedAlbum(string artist, string album, AudioQuality quality)
    {
        _library.GetAllArtistMetadata().Returns(new[] { new ArtistMetadata(new ArtistKey(artist), null) });
        _catalog.GetOwnedAlbums().Returns(
            new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase)
            {
                [artist] = new(StringComparer.OrdinalIgnoreCase) { [album] = quality },
            });
    }

    [Fact]
    public async Task An_owned_album_below_the_asked_tier_is_an_upgrade_row()
    {
        UserTier("justin", AudioQuality.Lossless);
        OwnedAlbum("José Peixoto", "As Vozes Dos Passos", AudioQuality.Lossy);
        LikedBy(("justin", "José Peixoto", "As Vozes Dos Passos"));

        var active = await _sut.GetActive();

        active.Single().Kind.Should().Be(FeedKind.UpgradeAlbum);
    }

    [Fact]
    public async Task An_upgrade_the_downloader_wrote_off_stops_being_wanted_and_its_failed_row_is_pruned()
    {
        // The stuck-row case: streamrip found nothing better than the copy on disk, so DownloadService
        // snoozed the upgrade and left the row Failed. Reconcile has to agree with that verdict —
        // otherwise the album is re-wanted every pass, the prune can never reach the row, and it sits
        // in the Failed list for ever offering a Retry that is guaranteed to fail the same way.
        UserTier("justin", AudioQuality.Lossless);
        OwnedAlbum("José Peixoto", "As Vozes Dos Passos", AudioQuality.Lossy);
        LikedBy(("justin", "José Peixoto", "As Vozes Dos Passos"));
        _purchases.Seed(new PurchaseItem(
            PurchaseKey.ForAlbum("José Peixoto", "As Vozes Dos Passos"), FeedKind.UpgradeAlbum,
            new ArtistKey("José Peixoto"), "As Vozes Dos Passos", null, 0, Array.Empty<string>(),
            PurchaseStatus.Failed, DateTimeOffset.UtcNow, null, 1, "José Peixoto",
            DownloadFailure.NoBetterQualityAvailable,
            TargetQuality: AudioQuality.Lossless, OwnedQuality: AudioQuality.Lossy));
        _blocks.GetAll().Returns(new[]
        {
            new AlbumBlock(
                "José Peixoto", "As Vozes Dos Passos", BlockedBy: null, AlbumBlockScope.Upgrade,
                DateTimeOffset.UtcNow.AddDays(180)),
        });

        var active = await _sut.GetActive();

        active.Should().BeEmpty();
        _purchases.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_lapsed_upgrade_snooze_makes_the_album_a_candidate_again()
    {
        // The stamp is a snooze, not a foreclosure: a catalogue can gain a lossless master later.
        UserTier("justin", AudioQuality.Lossless);
        OwnedAlbum("José Peixoto", "As Vozes Dos Passos", AudioQuality.Lossy);
        LikedBy(("justin", "José Peixoto", "As Vozes Dos Passos"));
        _blocks.GetAll().Returns(new[]
        {
            new AlbumBlock(
                "José Peixoto", "As Vozes Dos Passos", BlockedBy: null, AlbumBlockScope.Upgrade,
                DateTimeOffset.UtcNow.AddDays(-1)),
        });

        var active = await _sut.GetActive();

        active.Single().Kind.Should().Be(FeedKind.UpgradeAlbum);
    }

    [Fact]
    public async Task An_upgrade_verdict_does_not_touch_an_album_the_library_is_missing()
    {
        // Scope-Upgrade says "keep the copy we have". With no copy to keep it has nothing to say, and
        // reading it as a block would silently drop a genuine gap off the queue.
        UserTier("justin", AudioQuality.Lossless);
        LikedBy(("justin", "José Peixoto", "As Vozes Dos Passos"));
        _blocks.GetAll().Returns(new[]
        {
            new AlbumBlock(
                "José Peixoto", "As Vozes Dos Passos", BlockedBy: null, AlbumBlockScope.Upgrade, null),
        });

        var active = await _sut.GetActive();

        active.Single().Kind.Should().Be(FeedKind.MissingAlbum);
    }

    [Fact]
    public async Task An_upgrade_snoozed_under_the_album_artist_is_not_re_offered_via_the_listing_artist()
    {
        // A collaboration: the row was surfaced through one member and Deezer credits it to another.
        // DownloadService records the snooze under both acts, so either spelling has to find it.
        UserTier("justin", AudioQuality.Lossless);
        OwnedAlbum("Duo Credit", "Split Record", AudioQuality.Lossy);
        LikedBy(("justin", "One Member", "Split Record"));
        _missing.GetAll().Returns(new[]
        {
            new MissingAlbum(
                new ArtistKey("One Member"), new AlbumKey("Split Record"), null, 7,
                new ArtistKey("Duo Credit")),
        });
        _blocks.GetAll().Returns(new[]
        {
            new AlbumBlock("Duo Credit", "Split Record", BlockedBy: null, AlbumBlockScope.Upgrade, null),
        });

        var active = await _sut.GetActive();

        active.Should().BeEmpty();
    }
}
