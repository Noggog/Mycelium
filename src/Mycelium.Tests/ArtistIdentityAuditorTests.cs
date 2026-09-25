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

    private readonly Dictionary<string, IReadOnlyList<OwnedAlbumArtist>> _owned = new();

    public ArtistIdentityAuditorTests()
    {
        var cache = new SourceCache(new FakeSourceCacheStore(), NullLogger<SourceCache>.Instance, _clock);
        _sut = new ArtistIdentityAuditor(
            _musicBrainz, cache, _catalog, _resolutions, NullLogger<ArtistIdentityAuditor>.Instance, _clock);

        _catalog.GetOwnedAlbumArtists().Returns(_ => _owned);
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
        _owned[artist] = albums.Select(a => new OwnedAlbumArtist(a, null)).ToList();

    private void OwnUnder(string artist, params (string Title, int PlexArtist)[] albums) =>
        _owned[artist] = albums.Select(a => new OwnedAlbumArtist(a.Title, a.PlexArtist)).ToList();

    private static MusicBrainzArtist Artist(string mbid) => new() { Id = mbid, Name = "Vulture" };

    private static MusicBrainzReleaseGroup[] Groups(params string[] titles) =>
        titles.Select((t, i) => new MusicBrainzReleaseGroup { Id = $"rg-{t}-{i}", Title = t }).ToArray();

    [Fact]
    public async Task The_act_with_the_librarys_albums_wins_among_same_named_ones()
    {
        var result = await _sut.Check("Vulture", null, fresh: false);

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

        var result = await _sut.Check("Vulture", null, fresh: false);

        result!.Candidates.Single(c => c.Mbid == GermanMetal).AlbumOverlap.Should().Be(1);
    }

    [Fact]
    public async Task An_unanswered_search_stores_nothing()
    {
        _musicBrainz.SearchArtists("Vulture", Arg.Any<int>()).Returns((MusicBrainzArtist[]?)null);

        (await _sut.Check("Vulture", null, fresh: false)).Should().BeNull();
        _resolutions.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unanswered_discography_stores_nothing()
    {
        _musicBrainz.BrowseReleaseGroups(DanishIndie).Returns((MusicBrainzReleaseGroup[]?)null);

        (await _sut.Check("Vulture", null, fresh: false)).Should().BeNull();
        _resolutions.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_albums_owned_no_discography_is_fetched()
    {
        _owned.Clear();

        var result = await _sut.Check("Vulture", null, fresh: false);

        await _musicBrainz.DidNotReceive().BrowseReleaseGroups(Arg.Any<string>());
        result!.Mbid.Should().Be(GermanMetal);
        result.Confidence.Should().Be(ResolutionConfidence.Medium);
    }

    [Fact]
    public async Task A_pinned_artist_costs_no_MusicBrainz_calls()
    {
        _catalog.GetMusicBrainz(Vulture).Returns((new MusicBrainzIdentity(GermanMetal, "Vulture"), true));

        var result = await _sut.Check("Vulture", null, fresh: false);

        result!.Status.Should().Be(ArtistResolutionStatus.Pinned);
        await _musicBrainz.DidNotReceive().SearchArtists(Arg.Any<string>(), Arg.Any<int>());
        await _musicBrainz.DidNotReceive().LookupUrl(Arg.Any<string>());
    }

    [Fact]
    public async Task A_detached_Deezer_link_is_not_evidence()
    {
        _catalog.IsDeezerUnlinked(Vulture).Returns(true);

        var result = await _sut.Check("Vulture", null, fresh: false);

        await _musicBrainz.DidNotReceive().LookupUrl(Arg.Any<string>());
        result!.Candidates.SelectMany(c => c.Evidence).Should().NotContain(ResolutionEvidence.Deezer);
    }

    [Fact]
    public async Task Discographies_are_cached_between_checks_unless_fresh()
    {
        await _sut.Check("Vulture", null, fresh: false);
        await _sut.Check("Vulture", null, fresh: false);
        await _musicBrainz.Received(1).BrowseReleaseGroups(GermanMetal);

        await _sut.Check("Vulture", null, fresh: true);
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

    // Two unrelated bands called Doldrums, each its own Plex artist — the case that needed Plex ids.
    private const string CanadianDoldrums = "5ee481a8-7ca1-4f34-a6a0-fd7b126cb8a8";
    private const string SpaceRockDoldrums = "6853f967-f91b-48cc-85e7-afd8c74f87cf";
    private static readonly ArtistKey Doldrums = new("Doldrums");

    private void TwoDoldrums()
    {
        _catalog.GetAllPresent().Returns([Present("Doldrums")]);
        _musicBrainz.SearchArtists("Doldrums", Arg.Any<int>()).Returns(
        [
            new MusicBrainzArtist { Id = CanadianDoldrums, Name = "Doldrums", Disambiguation = "Canadian electronic artist" },
            new MusicBrainzArtist { Id = SpaceRockDoldrums, Name = "Doldrums", Disambiguation = "US space rock/psychedelic rock band" },
        ]);
        _musicBrainz.BrowseReleaseGroups(CanadianDoldrums).Returns(
            Groups("Empire Sound", "Egypt", "Lesser Evil", "The Air Conditioned Nightmare", "Esc", "Flipbook"));
        _musicBrainz.BrowseReleaseGroups(SpaceRockDoldrums).Returns(
            Groups("Acupuncture", "Feng Shui", "Desk Trickery", "Secret Life of Machines"));
    }

    [Fact]
    public async Task A_name_shared_by_two_Plex_artists_is_checked_as_two_library_artists()
    {
        TwoDoldrums();
        OwnUnder("Doldrums",
            ("Lesser Evil", 101), ("Egypt", 101), ("Empire Sound", 101), ("The Air Conditioned Nightmare", 101),
            ("Acupuncture", 202), ("Feng Shui", 202), ("Desk Trickery", 202), ("Secret Life of Machines", 202));

        await _sut.RunPass(all: false);

        _resolutions.Items.Keys.Should().BeEquivalentTo("Doldrums#plex:101", "Doldrums#plex:202");
        var canadian = _resolutions.Items["Doldrums#plex:101"];
        canadian.Mbid.Should().Be(CanadianDoldrums);
        canadian.Confidence.Should().Be(ResolutionConfidence.High);
        canadian.PlexArtistKey.Should().Be(101);
        canadian.Albums.Should().HaveCount(4);
        _resolutions.Items["Doldrums#plex:202"].Mbid.Should().Be(SpaceRockDoldrums);

        var report = await _sut.Report();
        report.LibraryArtists.Should().Be(2);
        report.SharedNames.Should().Be(1);
        report.High.Should().Be(2);
    }

    [Fact]
    public async Task One_Plex_artist_holding_two_acts_albums_is_mixed()
    {
        TwoDoldrums();
        OwnUnder("Doldrums",
            ("Lesser Evil", 101), ("Egypt", 101), ("Acupuncture", 101), ("Feng Shui", 101));

        var result = await _sut.Check("Doldrums", null, fresh: false);

        result!.Status.Should().Be(ArtistResolutionStatus.Mixed);
        result.NeedsAttention.Should().BeTrue();
        result.Reason.Should().Contain("Lesser Evil").And.Contain("Acupuncture");
    }

    [Fact]
    public async Task Candidates_carry_the_albums_they_matched_and_their_size()
    {
        var result = await _sut.Check("Vulture", null, fresh: false);

        var german = result!.Candidates.Single(c => c.Mbid == GermanMetal);
        german.MatchedAlbums.Should().BeEquivalentTo("Sentinels", "The Guillotine");
        german.ReleaseGroups.Should().Be(3);
    }

    [Fact]
    public async Task Pinning_one_Plex_artist_of_a_shared_name_leaves_the_name_and_the_other_alone()
    {
        TwoDoldrums();
        OwnUnder("Doldrums", ("Lesser Evil", 101), ("Acupuncture", 202));
        _musicBrainz.GetArtist(SpaceRockDoldrums).Returns(
            new MusicBrainzArtist { Id = SpaceRockDoldrums, Name = "Doldrums", Disambiguation = "US space rock" });

        var pinned = await _sut.PinPlexArtist("Doldrums", 202, SpaceRockDoldrums);

        pinned!.Status.Should().Be(ArtistResolutionStatus.Pinned);
        pinned.Mbid.Should().Be(SpaceRockDoldrums);
        pinned.PlexArtistKey.Should().Be(202);
        await _catalog.DidNotReceive().SetMusicBrainzIdentity(Arg.Any<ArtistKey>(), Arg.Any<MusicBrainzIdentity>(), Arg.Any<bool>());

        // The pin survives a re-check, and the other Doldrums is still judged on its own albums.
        (await _sut.Check("Doldrums", 202, fresh: true))!.Status.Should().Be(ArtistResolutionStatus.Pinned);
        (await _sut.Check("Doldrums", 101, fresh: false))!.Mbid.Should().Be(CanadianDoldrums);
    }

    [Fact]
    public async Task A_name_level_pin_does_not_settle_a_shared_names_Plex_artists()
    {
        TwoDoldrums();
        OwnUnder("Doldrums", ("Lesser Evil", 101), ("Acupuncture", 202));
        _catalog.GetMusicBrainz(Doldrums).Returns((new MusicBrainzIdentity(CanadianDoldrums, "Doldrums"), true));

        var other = await _sut.Check("Doldrums", 202, fresh: false);

        other!.Status.Should().Be(ArtistResolutionStatus.Resolved);
        other.Mbid.Should().Be(SpaceRockDoldrums);
        other.CurrentMbid.Should().Be(CanadianDoldrums);
    }

    private static ArtistResolution Resolution(string artist, DateTimeOffset checkedAt) =>
        new(artist, ArtistResolutionStatus.Resolved, ResolutionConfidence.High, "x", artist, null, null, 0, [],
            "", checkedAt);
}
