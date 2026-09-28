using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Deezer.Models;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;
using Mycelium.ListenBrainz.Models;
using Mycelium.ListenBrainz.Services;
using Mycelium.MongoDB.Services.Data;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// Gathering around <see cref="DeezerEditionMatcher"/>: which artists get a discography, what is asked of
/// MusicBrainz and Deezer, what a missing answer does, and when a discography comes due again.
/// </summary>
public class ArtistDiscographyBuilderTests
{
    private const string Maiden = "ca891d65-d9b0-4258-89f7-e6ba29d83767";
    private const long DeezerMaiden = 931;
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);

    private readonly IMusicBrainzApi _musicBrainz = Substitute.For<IMusicBrainzApi>();
    private readonly IDeezerApi _deezer = Substitute.For<IDeezerApi>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly FakeArtistResolutionRepo _resolutions = new();
    private readonly FakeArtistDiscographyRepo _discographies = new();
    private readonly ManualTimeProvider _clock = new(Start);
    private readonly ArtistDiscographyBuilder _sut;

    public ArtistDiscographyBuilderTests()
    {
        var cache = new SourceCache(new FakeSourceCacheStore(), NullLogger<SourceCache>.Instance, _clock);
        _sut = new ArtistDiscographyBuilder(
            _musicBrainz, _deezer, cache, _catalog, _resolutions, _discographies,
            NullLogger<ArtistDiscographyBuilder>.Instance, _clock);

        Resolve("Iron Maiden", Maiden);
        _catalog.GetDeezer(new ArtistKey("Iron Maiden"))
            .Returns((new DeezerIdentity(DeezerMaiden, "Iron Maiden", 1000, null, null), false));

        _musicBrainz.BrowseReleaseGroups(Maiden).Returns(
        [
            new MusicBrainzReleaseGroup { Id = "rg-sit", Title = "Somewhere in Time", PrimaryType = "Album", FirstReleaseDate = "1986-09-29" },
            new MusicBrainzReleaseGroup { Id = "rg-powerslave", Title = "Powerslave", PrimaryType = "Album", FirstReleaseDate = "1984-09-03" },
        ]);
        _musicBrainz.BrowseReleases(Maiden).Returns(
        [
            new MusicBrainzRelease
            {
                Id = "r-sit", Title = "Somewhere in Time", Barcode = "190295851910",
                ReleaseGroup = new MusicBrainzReleaseGroup { Id = "rg-sit" },
                Relations =
                [
                    new MusicBrainzRelation
                    {
                        TargetType = "url", Type = "free streaming",
                        Url = new MusicBrainzUrlTarget { Resource = "https://www.deezer.com/album/100" },
                    },
                ],
            },
        ]);
        _deezer.GetAlbums(DeezerMaiden).Returns([Album(200, "Powerslave (2015 Remaster)")]);
        _deezer.SearchArtistAlbums("Iron Maiden").Returns(
        [
            Album(201, "Powerslave (Live)", artist: DeezerMaiden),
            Album(300, "Somewhere in Time", artist: 42),
        ]);
        // Deezer renumbered the album MusicBrainz links to.
        _deezer.GetAlbum(100).Returns(Album(101, "Somewhere in Time (2015 Remaster)"));
        _deezer.GetAlbumByUpc(Arg.Any<string>()).Returns(new DeezerUpcLookup(null));
    }

    private void Resolve(
        string artist, string mbid, ArtistResolutionStatus status = ArtistResolutionStatus.Resolved,
        ResolutionConfidence? confidence = ResolutionConfidence.High, int? plexArtistKey = null) =>
        _resolutions.Seed(new ArtistResolution(
            artist, status, confidence, mbid, artist, null, null, 2, [], "", Start, plexArtistKey));

    private static DeezerAlbum Album(long id, string title, long? artist = null) =>
        new() { id = id, title = title, record_type = "album", artist = artist is { } a ? new DeezerArtist { id = a } : null };

    private async Task<ArtistDiscography> BuildMaiden() =>
        (await _sut.Build((await _sut.Targets()).Single(), fresh: false))!;

    [Fact]
    public async Task Editions_are_placed_and_the_rest_listed_as_unmatched()
    {
        var discography = await BuildMaiden();

        discography.DeezerArtistIds.Should().Equal(DeezerMaiden);
        discography.ReleaseGroups.Single(g => g.Mbid == "rg-sit").Editions
            .Should().ContainSingle().Which.Should().Match<DeezerEdition>(e =>
                e.AlbumId == 101 && e.Method == EditionMatchMethod.MbLink);
        discography.ReleaseGroups.Single(g => g.Mbid == "rg-powerslave").Editions
            .Should().ContainSingle().Which.AlbumId.Should().Be(200);
        // Search's other-artist row is dropped; its own live album fits no group.
        discography.UnmatchedDeezer.Select(u => u.AlbumId).Should().Equal(201);
        _discographies.Items.Should().ContainKey(Maiden);
    }

    [Fact]
    public async Task A_linked_album_on_the_listing_is_not_looked_up()
    {
        _deezer.GetAlbums(DeezerMaiden).Returns([Album(100, "Somewhere in Time")]);

        await BuildMaiden();

        await _deezer.DidNotReceive().GetAlbum(Arg.Any<long>());
    }

    [Fact]
    public async Task A_barcode_Deezer_knows_places_its_album()
    {
        _musicBrainz.BrowseReleases(Maiden).Returns(
        [
            new MusicBrainzRelease
            {
                Id = "r-sit", Title = "Somewhere in Time", Barcode = "190295851910",
                ReleaseGroup = new MusicBrainzReleaseGroup { Id = "rg-sit" },
            },
        ]);
        _deezer.GetAlbumByUpc("190295851910").Returns(new DeezerUpcLookup(Album(102, "Somewhere in Time")));

        var discography = await BuildMaiden();

        discography.ReleaseGroups.Single(g => g.Mbid == "rg-sit").Editions
            .Should().ContainSingle().Which.Method.Should().Be(EditionMatchMethod.Upc);
    }

    [Fact]
    public async Task An_unanswered_source_stores_nothing()
    {
        _deezer.GetAlbumByUpc(Arg.Any<string>()).Returns((DeezerUpcLookup?)null);

        var result = await _sut.Build((await _sut.Targets()).Single(), fresh: false);

        result.Should().BeNull();
        _discographies.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unanswered_Deezer_listing_stores_nothing()
    {
        _deezer.GetAlbums(DeezerMaiden).Returns((DeezerAlbum[]?)null);

        (await _sut.Build((await _sut.Targets()).Single(), fresh: false)).Should().BeNull();
    }

    [Fact]
    public async Task Only_settled_artists_get_a_discography_and_each_mbid_once()
    {
        Resolve("Iron Maiden (UK)", Maiden, ArtistResolutionStatus.Pinned, null);
        Resolve("Vulture", "vulture-mbid", confidence: ResolutionConfidence.Low);
        Resolve("Doldrums", "doldrums-mbid", ArtistResolutionStatus.Ambiguous, null);

        var targets = await _sut.Targets();

        targets.Should().ContainSingle().Which.Mbid.Should().Be(Maiden);
    }

    [Fact]
    public async Task A_shared_name_leaves_its_Deezer_page_out()
    {
        Resolve("Doldrums", "doldrums-mbid", plexArtistKey: 7);
        _catalog.GetDeezer(new ArtistKey("Doldrums"))
            .Returns((new DeezerIdentity(5, "Doldrums", 10, null, null), false));

        var targets = await _sut.Targets();

        targets.Single(t => t.Mbid == "doldrums-mbid").Deezer.Should().BeEmpty();
    }

    [Fact]
    public async Task A_quiet_artist_comes_due_in_60_to_90_days()
    {
        var discography = await BuildMaiden();

        (discography.ExpiresAt - Start).Should().BeGreaterThanOrEqualTo(TimeSpan.FromDays(60))
            .And.BeLessThanOrEqualTo(TimeSpan.FromDays(90));
    }

    [Fact]
    public async Task An_artist_still_releasing_comes_due_in_about_a_week()
    {
        _musicBrainz.BrowseReleaseGroups(Maiden).Returns(
        [
            new MusicBrainzReleaseGroup { Id = "rg-senjutsu", Title = "Senjutsu", PrimaryType = "Album", FirstReleaseDate = "2025-09" },
        ]);

        var discography = await BuildMaiden();

        (discography.ExpiresAt - Start).Should().BeGreaterThanOrEqualTo(TimeSpan.FromDays(6))
            .And.BeLessThanOrEqualTo(TimeSpan.FromDays(8));
    }

    [Fact]
    public async Task A_pass_builds_only_what_is_due()
    {
        await _sut.RunPass(all: false);
        _musicBrainz.ClearReceivedCalls();

        await _sut.RunPass(all: false);

        await _musicBrainz.DidNotReceive().BrowseReleaseGroups(Arg.Any<string>());
        _sut.GetStatus().Total.Should().Be(0);
    }

    [Fact]
    public async Task A_due_rebuild_asks_MusicBrainz_again()
    {
        await _sut.RunPass(all: false);
        _clock.Advance(TimeSpan.FromDays(91));
        _musicBrainz.ClearReceivedCalls();

        await _sut.RunPass(all: false);

        await _musicBrainz.Received(1).BrowseReleaseGroups(Maiden);
    }

    [Fact]
    public async Task A_rejection_survives_a_rebuild()
    {
        var first = await BuildMaiden();
        await _discographies.Put(first with
        {
            ReleaseGroups = first.ReleaseGroups
                .Select(g => g.Mbid == "rg-powerslave" ? g with { Rejected = [200] } : g)
                .ToList(),
        });

        var rebuilt = await BuildMaiden();

        var powerslave = rebuilt.ReleaseGroups.Single(g => g.Mbid == "rg-powerslave");
        powerslave.Editions.Should().BeEmpty();
        powerslave.Rejected.Should().Equal(200);
    }

    [Fact]
    public async Task The_report_counts_core_groups_by_how_they_matched()
    {
        await BuildMaiden();

        var report = await _sut.Report();

        report.Artists.Should().Be(1);
        report.Built.Should().Be(1);
        report.CoreReleaseGroups.Should().Be(2);
        report.CoreWithHighEdition.Should().Be(1);
        report.CoreWithLowEditionOnly.Should().Be(1);
        report.DeezerUnmatched.Should().Be(1);
        report.EditionsByMethod["MbLink"].Should().Be(1);
        report.EditionsByMethod["Title"].Should().Be(1);
    }

    [Fact]
    public async Task A_stored_discography_reads_back_with_method_names()
    {
        var discography = await BuildMaiden();

        var doc = ArtistDiscographyRepo.ToDocument(discography);

        ArtistDiscographyRepo.FromDocument(doc).Should().BeEquivalentTo(discography);
        JsonSerializer.Serialize(discography, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Should().Contain("\"method\":\"MbLink\"").And.Contain("\"confidence\":\"High\"");
    }
}
