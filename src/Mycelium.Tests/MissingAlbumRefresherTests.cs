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
    private readonly MissingAlbumRefresher _sut;

    public MissingAlbumRefresherTests()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var resolver = new DeezerArtistResolver(_deezer, cache, _catalog);
        _overrides.GetAll().Returns(Array.Empty<AlbumMatchOverride>());
        _sut = new MissingAlbumRefresher(_catalog, resolver, _deezer, _missing, _overrides, NullLogger<MissingAlbumRefresher>.Instance);

        _catalog.GetAllPresent().Returns(new[] { new CatalogArtist(new ArtistKey(Artist), null, default) });
        // Resolution searches for candidates (so it can tell a miss from an unanswered call).
        _deezer.SearchArtists(Artist, Arg.Any<int>())
            .Returns(new[] { new DeezerArtist { id = DeezerId, name = Artist } });
    }

    private static DeezerAlbum Album(string title, string recordType = "album", long id = 1) =>
        new() { id = id, title = title, record_type = recordType };

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
}
