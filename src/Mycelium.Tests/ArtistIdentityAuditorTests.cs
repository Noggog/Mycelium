using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.ListenBrainz.Models;
using Mycelium.ListenBrainz.Services;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// Evidence gathering around <see cref="ArtistIdentityJudge"/>: which sources are asked, what a missing
/// answer does, and which artists a pass visits. Modelled on the real Vulture: a Deezer page shared by
/// several acts, and a name MusicBrainz knows three bands by.
/// </summary>
public class ArtistIdentityAuditorTests
{
    private const string GermanMetal = "56cda0cf-5d98-4f34-9049-c3e744f65edd";
    private const string DanishIndie = "11111111-1111-1111-1111-111111111111";
    private const string Dubstep = "22222222-2222-2222-2222-222222222222";
    private const long DeezerVulture = 291446;

    private static readonly ArtistKey Vulture = new("Vulture");
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);

    private readonly IMusicBrainzApi _musicBrainz = Substitute.For<IMusicBrainzApi>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly FakeArtistResolutionRepo _resolutions = new();
    private readonly ManualTimeProvider _clock = new(Start);
    private readonly ArtistIdentityAuditor _sut;

    private readonly Dictionary<string, Dictionary<string, AudioQuality?>> _owned = new();

    public ArtistIdentityAuditorTests()
    {
        var cache = new SourceCache(new FakeSourceCacheStore(), NullLogger<SourceCache>.Instance, _clock);
        _sut = new ArtistIdentityAuditor(
            _musicBrainz, cache, _catalog, _resolutions, NullLogger<ArtistIdentityAuditor>.Instance, _clock);

        _catalog.GetOwnedAlbums().Returns(_ => _owned);
        _catalog.GetAllPresent().Returns([Present("Vulture")]);

        Own("Vulture", "Sentinels", "The Guillotine");
        _catalog.GetDeezer(Vulture).Returns((new DeezerIdentity(DeezerVulture, "Vulture", 1000, null, null), false));
        _musicBrainz.LookupUrl($"https://www.deezer.com/artist/{DeezerVulture}").Returns(new MusicBrainzUrl
        {
            Relations = [new MusicBrainzRelation { TargetType = "artist", Artist = Artist(GermanMetal) }],
        });
        _musicBrainz.SearchArtists("Vulture", Arg.Any<int>()).Returns(
            [Artist(DanishIndie), Artist(GermanMetal), Artist(Dubstep)]);
        _musicBrainz.BrowseReleaseGroups(GermanMetal).Returns(Groups("Sentinels", "The Guillotine", "Dealin’ Death"));
        _musicBrainz.BrowseReleaseGroups(DanishIndie).Returns(Groups("Tidevand"));
        _musicBrainz.BrowseReleaseGroups(Dubstep).Returns(Groups("Wobble"));
    }

    private static CatalogArtist Present(string name) => new(new ArtistKey(name), null, Start);

    private void Own(string artist, params string[] albums) =>
        _owned[artist] = albums.ToDictionary(a => a, _ => (AudioQuality?)null);

    private static MusicBrainzArtist Artist(string mbid) => new() { Id = mbid, Name = "Vulture" };

    private static MusicBrainzReleaseGroup[] Groups(params string[] titles) =>
        titles.Select((t, i) => new MusicBrainzReleaseGroup { Id = $"rg-{t}-{i}", Title = t }).ToArray();

    [Fact]
    public async Task The_act_with_the_librarys_albums_wins_among_same_named_ones()
    {
        var result = await _sut.Check("Vulture", fresh: false);

        result!.Status.Should().Be(ArtistResolutionStatus.Resolved);
        result.Mbid.Should().Be(GermanMetal);
        result.Confidence.Should().Be(ResolutionConfidence.High);
        result.Candidates.Should().HaveCount(3);
        result.Candidates.Single(c => c.Mbid == GermanMetal).Evidence
            .Should().BeEquivalentTo([ResolutionEvidence.Deezer, ResolutionEvidence.Name]);
        _resolutions.Items["Vulture"].Should().Be(result);
    }

    [Fact]
    public async Task Titles_are_compared_at_record_granularity()
    {
        // The library has the deluxe edition under a decorated title; MusicBrainz has the album.
        Own("Vulture", "Sentinels (Deluxe Edition)");

        var result = await _sut.Check("Vulture", fresh: false);

        result!.Candidates.Single(c => c.Mbid == GermanMetal).AlbumOverlap.Should().Be(1);
    }

    [Fact]
    public async Task An_unanswered_search_stores_nothing()
    {
        _musicBrainz.SearchArtists("Vulture", Arg.Any<int>()).Returns((MusicBrainzArtist[]?)null);

        (await _sut.Check("Vulture", fresh: false)).Should().BeNull();
        _resolutions.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unanswered_discography_stores_nothing()
    {
        _musicBrainz.BrowseReleaseGroups(DanishIndie).Returns((MusicBrainzReleaseGroup[]?)null);

        (await _sut.Check("Vulture", fresh: false)).Should().BeNull();
        _resolutions.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_albums_owned_no_discography_is_fetched()
    {
        _owned.Clear();

        var result = await _sut.Check("Vulture", fresh: false);

        await _musicBrainz.DidNotReceive().BrowseReleaseGroups(Arg.Any<string>());
        result!.Mbid.Should().Be(GermanMetal);
        result.Confidence.Should().Be(ResolutionConfidence.Medium);
    }

    [Fact]
    public async Task A_pinned_artist_costs_no_MusicBrainz_calls()
    {
        _catalog.GetMusicBrainz(Vulture).Returns((new MusicBrainzIdentity(GermanMetal, "Vulture"), true));

        var result = await _sut.Check("Vulture", fresh: false);

        result!.Status.Should().Be(ArtistResolutionStatus.Pinned);
        await _musicBrainz.DidNotReceive().SearchArtists(Arg.Any<string>(), Arg.Any<int>());
        await _musicBrainz.DidNotReceive().LookupUrl(Arg.Any<string>());
    }

    [Fact]
    public async Task A_detached_Deezer_link_is_not_evidence()
    {
        _catalog.IsDeezerUnlinked(Vulture).Returns(true);

        var result = await _sut.Check("Vulture", fresh: false);

        await _musicBrainz.DidNotReceive().LookupUrl(Arg.Any<string>());
        result!.Candidates.SelectMany(c => c.Evidence).Should().NotContain(ResolutionEvidence.Deezer);
    }

    [Fact]
    public async Task Discographies_are_cached_between_checks_unless_fresh()
    {
        await _sut.Check("Vulture", fresh: false);
        await _sut.Check("Vulture", fresh: false);
        await _musicBrainz.Received(1).BrowseReleaseGroups(GermanMetal);

        await _sut.Check("Vulture", fresh: true);
        await _musicBrainz.Received(2).BrowseReleaseGroups(GermanMetal);
    }

    [Fact]
    public async Task A_pass_checks_only_artists_never_checked_or_due_and_forgets_departed_ones()
    {
        _catalog.GetAllPresent().Returns([Present("Vulture"), Present("Recent"), Present("Stale")]);
        _musicBrainz.SearchArtists(Arg.Is<string>(n => n != "Vulture"), Arg.Any<int>()).Returns([]);
        _resolutions.Seed(Resolution("Recent", Start - TimeSpan.FromDays(1)));
        _resolutions.Seed(Resolution("Stale", Start - ArtistIdentityAuditor.RecheckAfter));
        _resolutions.Seed(Resolution("Departed", Start));

        await _sut.RunPass(all: false);

        _resolutions.Items.Keys.Should().BeEquivalentTo("Vulture", "Recent", "Stale");
        _resolutions.Items["Recent"].CheckedAt.Should().Be(Start - TimeSpan.FromDays(1));
        _resolutions.Items["Stale"].CheckedAt.Should().Be(Start);
        _sut.GetStatus().Should().Match<ArtistIdentityPassStatus>(s =>
            !s.Running && s.Processed == 2 && s.Total == 2 && s.FinishedAt != null);
    }

    [Fact]
    public async Task A_pass_counts_unanswered_artists_and_moves_on()
    {
        _catalog.GetAllPresent().Returns([Present("Vulture"), Present("Other")]);
        _musicBrainz.SearchArtists("Other", Arg.Any<int>()).Returns((MusicBrainzArtist[]?)null);

        await _sut.RunPass(all: false);

        _sut.GetStatus().Unreachable.Should().Be(1);
        _resolutions.Items.Keys.Should().BeEquivalentTo("Vulture");
    }

    [Fact]
    public async Task The_report_and_attention_list_cover_only_artists_still_in_the_library()
    {
        _catalog.GetAllPresent().Returns([Present("Vulture"), Present("Nobody")]);
        _musicBrainz.SearchArtists("Nobody", Arg.Any<int>()).Returns([]);
        await _sut.RunPass(all: false);
        _resolutions.Seed(Resolution("Gone", Start) with { Status = ArtistResolutionStatus.Missing });

        var report = await _sut.Report();
        var attention = await _sut.NeedingAttention();

        report.LibraryArtists.Should().Be(2);
        report.High.Should().Be(1);
        report.Missing.Should().Be(1);
        report.NeedsAttention.Should().Be(1);
        attention.Select(a => a.Artist).Should().Equal("Nobody");
    }

    private static ArtistResolution Resolution(string artist, DateTimeOffset checkedAt) =>
        new(artist, ArtistResolutionStatus.Resolved, ResolutionConfidence.High, "x", artist, null, null, 0, [],
            "", checkedAt);
}
