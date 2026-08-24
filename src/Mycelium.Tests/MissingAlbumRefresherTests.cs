using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Deezer.Models;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

public class MissingAlbumRefresherTests
{
    private const string Artist = "milo";
    private const long DeezerId = 42;

    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly IDeezerApi _deezer = Substitute.For<IDeezerApi>();
    private readonly IMissingAlbumRepo _missing = Substitute.For<IMissingAlbumRepo>();
    private readonly IAlbumMatchOverrideRepo _overrides = Substitute.For<IAlbumMatchOverrideRepo>();
    private readonly FakeDeezerAlbumArtistRepo _albumArtists = new();
    private readonly MissingAlbumRefresher _sut;

    public MissingAlbumRefresherTests()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var resolver = new DeezerArtistResolver(_deezer, cache, _catalog);
        _overrides.GetAll().Returns(Array.Empty<AlbumMatchOverride>());
        _sut = new MissingAlbumRefresher(
            _catalog, resolver, _deezer, _missing, _overrides, _albumArtists,
            NullLogger<MissingAlbumRefresher>.Instance);

        _catalog.GetAllPresent().Returns(new[] { new CatalogArtist(new ArtistKey(Artist), null, default) });
        // Resolution searches for candidates (so it can tell a miss from an unanswered call).
        _deezer.SearchArtists(Artist, Arg.Any<int>())
            .Returns(new[] { new DeezerArtist { id = DeezerId, name = Artist } });
    }

    private static DeezerAlbum Album(string title, string recordType = "album", long id = 1) =>
        new() { id = id, title = title, record_type = recordType };

    /// <summary>An album-search hit, which — unlike a discography row — names the artist it belongs to.</summary>
    private static DeezerAlbum SearchHit(
        string title, long id, long artistId = DeezerId, string recordType = "album") =>
        new()
        {
            id = id, title = title, record_type = recordType,
            artist = new DeezerArtist { id = artistId },
        };

    private static Dictionary<string, HashSet<string>> Owned(params (string Artist, string[] Albums)[] entries)
    {
        var d = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (artist, albums) in entries)
        {
            d[artist] = new HashSet<string>(albums, StringComparer.OrdinalIgnoreCase);
        }
        return d;
    }

    private IReadOnlyList<MissingAlbum> CapturedMissing()
    {
        var call = _missing.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IMissingAlbumRepo.ReplaceForArtist));
        return (IReadOnlyList<MissingAlbum>)call.GetArguments()[1]!;
    }

    [Fact]
    public async Task Owned_album_matches_despite_typographic_apostrophe_and_casing()
    {
        // Plex stored the title with a typographic apostrophe + title casing; Deezer returns a straight
        // apostrophe, all lower-case. Same album — it must not be reported as missing.
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [Artist] = new(StringComparer.OrdinalIgnoreCase) { "So the Flies Don’t Come" },
        });
        _deezer.GetAlbums(DeezerId).Returns(new[] { Album("so the flies don't come") });

        await _sut.Refresh();

        CapturedMissing().Should().BeEmpty();
    }

    [Fact]
    public async Task Genuinely_absent_album_is_still_reported_missing()
    {
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [Artist] = new(StringComparer.OrdinalIgnoreCase) { "So the Flies Don’t Come" },
        });
        _deezer.GetAlbums(DeezerId).Returns(new[]
        {
            Album("so the flies don't come"), // owned -> skipped
            Album("budding ornithologists are weary of tired analogies"), // not owned -> missing
        });

        await _sut.Refresh();

        CapturedMissing().Select(m => m.Album.AlbumName)
            .Should().Equal("budding ornithologists are weary of tired analogies");
    }

    [Fact]
    public async Task RefreshOne_with_no_owned_albums_surfaces_every_record_type_tagged()
    {
        // The brand-new-artist path: nothing is owned, so every record Deezer lists surfaces — singles
        // and compilations included, each tagged with its type. They're persisted rather than dropped
        // because the row is what carries the Deezer id to the downloader; the *feed* is what declines
        // to push them (AlbumRecordType.IsFeedEligible), so a queued single can still be fetched.
        _deezer.GetAlbums(DeezerId).Returns(new[]
        {
            Album("first lp"),
            Album("second lp"),
            Album("a single", recordType: "single"),
            Album("a comp", recordType: "compilation"),
            Album("an ep", recordType: "ep"),
        });

        var result = await _sut.RefreshOne(new ArtistKey(Artist), Owned());

        result.Select(m => (m.Album.AlbumName, m.RecordType)).Should().BeEquivalentTo(new[]
        {
            ("first lp", "album"), ("second lp", "album"), ("an ep", "ep"),
            ("a single", "single"), ("a comp", "compilation"),
        });
        // And each carries a Deezer id — the whole reason they're persisted rather than filtered here.
        result.Should().OnlyContain(m => m.DeezerAlbumId != 0);
        CapturedMissing().Select(m => m.Album.AlbumName)
            .Should().BeEquivalentTo("first lp", "second lp", "an ep", "a single", "a comp");
    }

    [Fact]
    public async Task Discography_lists_every_record_type_with_its_label()
    {
        // The drill-down shows everything Deezer lists — otherwise a release Deezer files as a single
        // (Ben Howard's 3-track "Another Friday Night / Hot Heavy Summer / Sister" is one) is invisible
        // in the app. Each row carries its type so the UI can badge it; without that a single would read
        // as an LP, since the listing no longer implies "album".
        // Titles avoid a trailing "EP"/"LP" on purpose: the title matcher treats that as a format
        // designator and folds it away, which would make these two fixtures the same album.
        _deezer.GetAlbums(DeezerId).Returns(new[]
        {
            Album("lp record", id: 1),
            Album("ep record", recordType: "ep", id: 2),
            Album("a single", recordType: "single", id: 3),
            Album("a comp", recordType: "compilation", id: 4),
        });

        var listed = await _sut.Discography(new ArtistKey(Artist), Owned());

        listed.Select(a => (a.Title, a.RecordType)).Should().BeEquivalentTo(new[]
        {
            ("lp record", "album"), ("ep record", "ep"), ("a single", "single"), ("a comp", "compilation"),
        });
    }

    // ---- The search backfill: Deezer's discography listing is not the whole catalog ----
    //
    // /artist/{id}/albums omits releases Deezer itself credits to the artist — Against Me!'s entire
    // post-2011 output, 87 of Walk Off The Earth's 154 releases. Album search reaches them.

    [Fact]
    public async Task Search_recovers_releases_the_discography_listing_omits()
    {
        _deezer.GetAlbums(DeezerId).Returns(new[] { Album("listed lp", id: 1) });
        _deezer.SearchArtistAlbums(Arg.Any<string>()).Returns(new[]
        {
            SearchHit("listed lp", id: 1),
            SearchHit("omitted lp", id: 2),
            SearchHit("omitted single", id: 3, recordType: "single"),
        });

        var listed = await _sut.Discography(new ArtistKey(Artist), Owned());

        listed.Select(a => a.Title).Should().Equal("listed lp", "omitted lp", "omitted single");
        // Recovered rows are persisted like any other, so they carry the Deezer id a queued download
        // needs — the whole point of surfacing them rather than just showing them.
        CapturedMissing().Select(m => (m.Album.AlbumName, m.DeezerAlbumId))
            .Should().Equal(("listed lp", 1L), ("omitted lp", 2L), ("omitted single", 3L));
    }

    [Fact]
    public async Task Search_hits_for_a_different_artist_are_ignored()
    {
        // Deezer's album search matches on name, so it answers for every act with a similar one. The
        // artist-id check is what makes a fuzzy search safe to merge into a discography.
        _deezer.GetAlbums(DeezerId).Returns(new[] { Album("listed lp", id: 1) });
        _deezer.SearchArtistAlbums(Arg.Any<string>()).Returns(new[]
        {
            SearchHit("someone else's lp", id: 2, artistId: 999),
            SearchHit("no artist at all", id: 3, artistId: 0),
        });

        var listed = await _sut.Discography(new ArtistKey(Artist), Owned());

        listed.Select(a => a.Title).Should().Equal("listed lp");
    }

    [Fact]
    public async Task A_release_in_both_the_listing_and_the_search_is_still_one_row()
    {
        // Two ways the same release arrives twice: the same Deezer id in both sources, and a second id
        // for what is really the same listing. Neither may double a row in the readout.
        _deezer.GetAlbums(DeezerId).Returns(new[]
        {
            new DeezerAlbum { id = 1, title = "listed lp", record_type = "album", release_date = "2004-02-02" },
        });
        _deezer.SearchArtistAlbums(Arg.Any<string>()).Returns(new[]
        {
            SearchHit("listed lp", id: 1),
            SearchHit("Listed LP", id: 77),
        });

        var listed = await _sut.Discography(new ArtistKey(Artist), Owned());

        var row = listed.Should().ContainSingle().Subject;
        // And it's the listing's row that survives: search hits carry no release date, so preferring
        // them would cost the year the UI shows beside the title.
        row.DeezerAlbumId.Should().Be(1);
        row.Year.Should().Be(2004);
    }

    [Fact]
    public async Task A_pressing_only_search_knows_about_is_listed_but_not_pushed()
    {
        // The two features meeting: the remaster is a row of its own (it's a separate pressing), and
        // search is what found it.
        _deezer.GetAlbums(DeezerId).Returns(new[] { Album("Both Sides (Deluxe Edition)", id: 1) });
        _deezer.SearchArtistAlbums(Arg.Any<string>())
            .Returns(new[] { SearchHit("Both Sides (2015 Remaster)", id: 2) });

        var listed = await _sut.Discography(new ArtistKey(Artist), Owned());

        listed.Select(a => a.Title)
            .Should().Equal("Both Sides (Deluxe Edition)", "Both Sides (2015 Remaster)");
        CapturedMissing().Select(m => m.Album.AlbumName)
            .Should().Equal("Both Sides (Deluxe Edition)", "Both Sides (2015 Remaster)");
    }

    [Fact]
    public async Task Unanswered_album_search_does_not_replace_the_stored_rows()
    {
        // The listing answered, the backfill didn't. Persisting the listing alone would drop every row
        // only search knows about — and with it the Deezer id a queued download reads — so the artist is
        // skipped instead, exactly as when the listing itself goes unanswered.
        _deezer.GetAlbums(DeezerId).Returns(new[] { Album("listed lp", id: 1) });
        _deezer.SearchArtistAlbums(Arg.Any<string>()).Returns((DeezerAlbum[]?)null);

        var act = () => _sut.Discography(new ArtistKey(Artist), Owned());

        await act.Should().ThrowAsync<DeezerUnavailableException>();
        await _missing.DidNotReceiveWithAnyArgs().ReplaceForArtist(default!, default!);
    }

    // ---- Pressings: Deezer lists the deluxe edition and the remaster as separate releases ----

    [Fact]
    public async Task Every_pressing_of_a_record_gets_its_own_discography_row()
    {
        // Collapsing pressings onto the normalized title used to hide whichever Deezer listed second —
        // Phil Collins' "Both Sides (2015 Remaster)" never appeared, because the deluxe edition had
        // already claimed the row.
        _deezer.GetAlbums(DeezerId).Returns(new[]
        {
            Album("Both Sides (Deluxe Edition)", id: 12194438),
            Album("Both Sides (2015 Remaster)", id: 12308830),
        });

        var listed = await _sut.Discography(new ArtistKey(Artist), Owned());

        listed.Select(a => a.Title)
            .Should().Equal("Both Sides (Deluxe Edition)", "Both Sides (2015 Remaster)");
        // Each with its own Deezer id, because the row is what hands the downloader an id when the
        // pressing is queued from the drill-down.
        listed.Select(a => a.DeezerAlbumId).Should().Equal(12194438L, 12308830L);
        // Both persisted and both offered — they are two releases, each declined on its own.
        CapturedMissing().Select(m => m.Album.AlbumName)
            .Should().Equal("Both Sides (Deluxe Edition)", "Both Sides (2015 Remaster)");
    }

    [Fact]
    public async Task A_title_deezer_repeats_verbatim_is_still_one_row()
    {
        // Listing pressings separately is not the same as listing everything: the same release under
        // the same name (a regional duplicate, differing only in typography) is one row, as before.
        _deezer.GetAlbums(DeezerId).Returns(new[]
        {
            Album("Don’t Look Now", id: 1),
            Album("Don't Look Now", id: 2),
        });

        var listed = await _sut.Discography(new ArtistKey(Artist), Owned());

        listed.Select(a => a.Title).Should().Equal("Don’t Look Now");
    }

    [Fact]
    public async Task Owning_one_pressing_leaves_the_others_missing()
    {
        // Ownership is per release, not per record: the library has the plain "Both Sides", so that row
        // is owned and Deezer's two other pressings stay gaps. They're separate releases with separate
        // ids — offering them is the point, and an unwanted one is dismissed or blocked on its own row.
        _deezer.GetAlbums(DeezerId).Returns(new[]
        {
            Album("Both Sides", id: 1),
            Album("Both Sides (Deluxe Edition)", id: 2),
            Album("Both Sides (2015 Remaster)", id: 3),
        });

        var listed = await _sut.Discography(
            new ArtistKey(Artist), Owned((Artist, new[] { "Both Sides" })));

        listed.Where(a => a.Owned).Select(a => a.Title).Should().Equal("Both Sides");
        CapturedMissing().Select(m => m.Album.AlbumName)
            .Should().Equal("Both Sides (Deluxe Edition)", "Both Sides (2015 Remaster)");
    }

    [Fact]
    public async Task Owned_single_is_not_reported_missing()
    {
        // Singles are diffed against the library like anything else — one already ripped must not come
        // back as acquirable just because Deezer files it as a single.
        _deezer.GetAlbums(DeezerId).Returns(new[]
        {
            Album("a single", recordType: "single"),
            Album("another single", recordType: "single", id: 2),
        });

        var result = await _sut.RefreshOne(
            new ArtistKey(Artist), Owned((Artist, new[] { "A Single" })));

        result.Select(m => m.Album.AlbumName).Should().Equal("another single");
    }

    [Fact]
    public async Task Missing_album_carries_the_deezer_release_year()
    {
        // The year is shown beside the title in the feed. Deezer sends a full date; a release it has no
        // date for must simply come through as null rather than blocking the row.
        _deezer.GetAlbums(DeezerId).Returns(new[]
        {
            new DeezerAlbum { id = 1, title = "dated lp", record_type = "album", release_date = "1997-08-19" },
            new DeezerAlbum { id = 2, title = "undated lp", record_type = "album", release_date = "" },
        });

        var result = await _sut.RefreshOne(new ArtistKey(Artist), Owned());

        result.Select(m => (m.Album.AlbumName, m.Year))
            .Should().BeEquivalentTo(new[] { ("dated lp", (int?)1997), ("undated lp", null) });
    }

    [Fact]
    public async Task Collaboration_owned_under_its_album_artist_is_not_missing()
    {
        // "milo" lists a duo record on Deezer whose real album-artist is "nostrum grocers" — which is
        // how the library filed it. Even though milo's own owned set lacks it, it must NOT surface as
        // missing: it's owned under the album-artist.
        _catalog.GetOwnedAlbums().Returns(Owned(("nostrum grocers", new[] { "Nostrum Grocers" })));
        _deezer.GetAlbums(DeezerId).Returns(new[] { Album("nostrum grocers", id: 99) });
        _deezer.GetAlbum(99).Returns(new DeezerAlbum
        {
            id = 99, title = "nostrum grocers", artist = new DeezerArtist { name = "nostrum grocers" },
        });

        await _sut.Refresh();

        CapturedMissing().Should().BeEmpty();
    }

    [Fact]
    public async Task Collaboration_not_owned_is_missing_but_carries_its_album_artist()
    {
        // Same duo record, but the library doesn't have it anywhere. It surfaces as missing under the
        // listing artist (so it stays discoverable on milo's feed), yet matches ownership under the
        // album-artist the library would file it under.
        _catalog.GetOwnedAlbums().Returns(Owned());
        _deezer.GetAlbums(DeezerId).Returns(new[] { Album("nostrum grocers", id: 99) });
        _deezer.GetAlbum(99).Returns(new DeezerAlbum
        {
            id = 99, title = "nostrum grocers", artist = new DeezerArtist { name = "Nostrum Grocers" },
        });

        await _sut.Refresh();

        var m = CapturedMissing().Single();
        m.Artist.ArtistName.Should().Be(Artist);                 // surfaces under the listing artist
        m.MatchArtist.ArtistName.Should().Be("Nostrum Grocers"); // matches ownership under the album-artist
    }

    // ---- Deezer not answering (rate-limit quota) must never be read as "this artist has nothing" ----
    //
    // Deezer serves a burst past its ~50-calls-per-5s ceiling as a 200 whose body is an error
    // envelope; the client turns that into a null. Persisting the resulting "empty discography" wiped
    // the artist's stored rows and blanked their album list in the UI until a hard reload.

    [Fact]
    public async Task Unanswered_discography_listing_does_not_replace_the_stored_rows()
    {
        _catalog.GetOwnedAlbums().Returns(Owned());
        _deezer.GetAlbums(DeezerId).Returns((DeezerAlbum[]?)null);

        var act = () => _sut.Discography(new ArtistKey(Artist), Owned());

        await act.Should().ThrowAsync<DeezerUnavailableException>();
        await _missing.DidNotReceiveWithAnyArgs().ReplaceForArtist(default!, default!);
    }

    [Fact]
    public async Task Unanswered_artist_lookup_does_not_replace_the_stored_rows()
    {
        _catalog.GetOwnedAlbums().Returns(Owned());
        // Null candidates = Deezer never answered the name search, as distinct from an empty array
        // (Deezer answered: no such artist), which legitimately clears the rows.
        _deezer.SearchArtists(Artist, Arg.Any<int>()).Returns((DeezerArtist[]?)null);

        var act = () => _sut.RefreshOne(new ArtistKey(Artist), Owned());

        await act.Should().ThrowAsync<DeezerUnavailableException>();
        await _missing.DidNotReceiveWithAnyArgs().ReplaceForArtist(default!, default!);
    }

    [Fact]
    public async Task An_artist_deezer_answers_no_match_for_still_has_its_rows_cleared()
    {
        // The other half of the distinction: an answered search that found nothing is evidence, so the
        // artist's stale rows should go.
        _catalog.GetOwnedAlbums().Returns(Owned());
        _deezer.SearchArtists(Artist, Arg.Any<int>()).Returns(Array.Empty<DeezerArtist>());

        await _sut.RefreshOne(new ArtistKey(Artist), Owned());

        CapturedMissing().Should().BeEmpty();
    }

    // ---- The album-artist backfill: one /album/{id} call each, and there can be hundreds ----
    //
    // Deezer's listing doesn't name the act a release is credited to, so the diff has to ask — and it
    // asks about every release the scanning artist doesn't already own, which for a prolific act is
    // nearly the whole discography. Paced at 40 calls per 5 seconds, that put 15+ seconds of spinner in
    // front of the drill-down. Two things bound it: answers are remembered for good, and the
    // interactive path only asks where the answer can still change something.

    [Fact]
    public async Task Discography_does_not_ask_who_a_release_nobody_owns_is_credited_to()
    {
        // The overwhelmingly common row: a gap in the library that no act, under any name, holds a
        // record by. Learning its credited act can only confirm it's missing — which it costs a
        // rate-limited call to be told.
        _deezer.GetAlbums(DeezerId).Returns(new[]
        {
            Album("owned lp", id: 1),
            Album("a gap", id: 2),
        });

        var listed = await _sut.Discography(new ArtistKey(Artist), Owned((Artist, new[] { "owned lp" })));

        listed.Where(a => a.Owned).Select(a => a.Title).Should().Equal("owned lp");
        await _deezer.DidNotReceiveWithAnyArgs().GetAlbum(default);
    }

    [Fact]
    public async Task Discography_still_asks_when_another_act_owns_a_record_by_that_title()
    {
        // The row where the answer decides something: the library holds a record by this title, just
        // under a different act. Skipping the lookup here would report an album we own as a gap.
        _deezer.GetAlbums(DeezerId).Returns(new[] { Album("nostrum grocers", id: 99) });
        _deezer.GetAlbum(99).Returns(new DeezerAlbum
        {
            id = 99, title = "nostrum grocers", artist = new DeezerArtist { name = "nostrum grocers" },
        });

        var listed = await _sut.Discography(
            new ArtistKey(Artist), Owned(("nostrum grocers", new[] { "Nostrum Grocers" })));

        listed.Should().ContainSingle().Which.Owned.Should().BeTrue();
        CapturedMissing().Should().BeEmpty();
    }

    [Fact]
    public async Task Discography_still_asks_when_a_recorded_merge_names_that_title()
    {
        // The other way an unowned-looking release turns out to be owned: the user has merged it into a
        // near-miss library title under another act. That merge is only reachable through the credited
        // act, so the lookup has to happen even though nobody's owned-album list mentions the title.
        _overrides.GetAll().Returns(new[]
        {
            new AlbumMatchOverride("nostrum grocers", "nostrum grocers", "Nostrum Grocers (2018)"),
        });
        _deezer.GetAlbums(DeezerId).Returns(new[] { Album("nostrum grocers", id: 99) });
        _deezer.GetAlbum(99).Returns(new DeezerAlbum
        {
            id = 99, title = "nostrum grocers", artist = new DeezerArtist { name = "nostrum grocers" },
        });

        var listed = await _sut.Discography(new ArtistKey(Artist), Owned());

        listed.Should().ContainSingle().Which.Owned.Should().BeTrue();
    }

    [Fact]
    public async Task A_credited_act_is_learned_once_and_remembered()
    {
        // A release's credited act never changes, so a second pass over the same album must cost
        // nothing. Held in Mongo rather than in memory precisely so a restart doesn't re-buy the answer.
        _deezer.GetAlbums(DeezerId).Returns(new[] { Album("nostrum grocers", id: 99) });
        _deezer.GetAlbum(99).Returns(new DeezerAlbum
        {
            id = 99, title = "nostrum grocers", artist = new DeezerArtist { name = "Nostrum Grocers" },
        });

        await _sut.RefreshOne(new ArtistKey(Artist), Owned());
        await _sut.RefreshOne(new ArtistKey(Artist), Owned());

        await _deezer.Received(1).GetAlbum(99);
        _albumArtists.Items.Should().Contain(new KeyValuePair<long, string>(99, "Nostrum Grocers"));
    }

    [Fact]
    public async Task The_drill_down_reads_what_the_sweep_already_learned()
    {
        // The two halves meeting: the nightly sweep pays for the lookup and files the answer, so the
        // drill-down applies the same collaboration reasoning without touching Deezer at all.
        _albumArtists.Seed(99, "nostrum grocers");
        _deezer.GetAlbums(DeezerId).Returns(new[] { Album("nostrum grocers", id: 99) });

        var listed = await _sut.Discography(
            new ArtistKey(Artist), Owned(("nostrum grocers", new[] { "Nostrum Grocers" })));

        listed.Should().ContainSingle().Which.Owned.Should().BeTrue();
        await _deezer.DidNotReceiveWithAnyArgs().GetAlbum(default);
    }

    [Fact]
    public async Task An_unresolved_release_is_still_credited_to_the_listing_artist()
    {
        // What the interactive path gives up: a release Deezer has added since the last sweep goes into
        // the store under the act whose discography surfaced it rather than its own credited act. It is
        // still offered, still carries its Deezer id, and the next sweep fills in the rest.
        _deezer.GetAlbums(DeezerId).Returns(new[] { Album("nostrum grocers", id: 99) });

        await _sut.Discography(new ArtistKey(Artist), Owned());

        var m = CapturedMissing().Should().ContainSingle().Subject;
        m.DeezerAlbumId.Should().Be(99);
        m.MatchArtist.ArtistName.Should().Be(Artist);
    }

    [Fact]
    public async Task Sweep_skips_an_artist_deezer_wont_answer_for_and_carries_on()
    {
        const string other = "open mike eagle";
        _catalog.GetAllPresent().Returns(new[]
        {
            new CatalogArtist(new ArtistKey(Artist), null, default),
            new CatalogArtist(new ArtistKey(other), null, default),
        });
        _catalog.GetOwnedAlbums().Returns(Owned());
        _deezer.GetAlbums(DeezerId).Returns((DeezerAlbum[]?)null);
        _deezer.SearchArtists(other, Arg.Any<int>())
            .Returns(new[] { new DeezerArtist { id = 7, name = other } });
        _deezer.GetAlbums(7).Returns(new[] { Album("brick body kids still daydream") });

        var result = await _sut.Refresh();

        // The unanswered artist is scanned but leaves no write behind; the other is still refreshed.
        result.ArtistsScanned.Should().Be(2);
        CapturedMissing().Should().ContainSingle().Which.Album.AlbumName
            .Should().Be("brick body kids still daydream");
    }
}
