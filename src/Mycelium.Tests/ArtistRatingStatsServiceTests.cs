using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The per-artist star summary. Star ratings are per-Plex-account, so the load-bearing detail here is
/// <em>whose</em> token every read is made with: the asking user's own linked one, never the server's.
/// </summary>
public class ArtistRatingStatsServiceTests
{
    private const string Artist = "Radiohead";
    private const string User = "oidc-subject";
    private const string Token = "kelseys-server-token";

    private readonly IPlexApi _plex = Substitute.For<IPlexApi>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly IPlexLinkRepo _links = Substitute.For<IPlexLinkRepo>();
    private readonly ArtistRatingStatsService _sut;

    public ArtistRatingStatsServiceTests()
    {
        _sut = new ArtistRatingStatsService(
            _catalog, _plex, _links, NullLogger<ArtistRatingStatsService>.Instance);
        _catalog.GetPlexRatingKeys(Arg.Any<ArtistKey>()).Returns(Array.Empty<int>());
        _links.Get(User).Returns(new PlexLink(
            User, "42", "kelsey", null, Token, DateTimeOffset.UnixEpoch));
    }

    private static PlexTrack Track(double? userRating) => new() { Title = "t", UserRating = userRating };

    private void StoredKeys(params int[] keys) =>
        _catalog.GetPlexRatingKeys(new ArtistKey(Artist)).Returns(keys);

    [Fact]
    public async Task NotInPlex_ReportsAbsentWithNoStats()
    {
        // No stored rating keys → the artist isn't in Plex, so there's nothing to summarise.
        var stats = await _sut.ForUser(User, new ArtistKey(Artist));

        stats.Present.Should().BeFalse();
        stats.RatedCount.Should().Be(0);
        stats.Average.Should().BeNull();
        await _plex.DidNotReceive().GetArtistTracks(Arg.Any<int>(), Arg.Any<string>());
    }

    [Fact]
    public async Task PresentButNothingRated_ReportsPresentWithZeroRated()
    {
        StoredKeys(10);
        _plex.GetArtistTracks(10, Token).Returns(new[] { Track(null), Track(0) });

        var stats = await _sut.ForUser(User, new ArtistKey(Artist));

        stats.Present.Should().BeTrue();
        stats.RatedCount.Should().Be(0);
        stats.TrackCount.Should().Be(2);
        stats.Highest.Should().BeNull();
    }

    [Fact]
    public async Task AggregatesRatedTracks_OnFiveStarScale()
    {
        StoredKeys(10);
        // Plex 0–10 scale: 10, 6, 4 (and one unrated) → 5, 3, 2 stars; unrated is excluded.
        _plex.GetArtistTracks(10, Token).Returns(new[] { Track(10), Track(6), Track(4), Track(null) });

        var stats = await _sut.ForUser(User, new ArtistKey(Artist));

        stats.Present.Should().BeTrue();
        stats.RatedCount.Should().Be(3);
        stats.TrackCount.Should().Be(4);
        stats.Highest.Should().Be(5.0);
        stats.Lowest.Should().Be(2.0);
        stats.Average.Should().BeApproximately(3.33, 0.001); // (5 + 3 + 2) / 3
    }

    [Fact]
    public async Task UnionsTracksAcrossEveryRatingKey()
    {
        // A name can map to several Plex items (split collaborators / recurring names); pool them all.
        StoredKeys(10, 20);
        _plex.GetArtistTracks(10, Token).Returns(new[] { Track(8) });  // 4 stars
        _plex.GetArtistTracks(20, Token).Returns(new[] { Track(2) });  // 1 star

        var stats = await _sut.ForUser(User, new ArtistKey(Artist));

        stats.RatedCount.Should().Be(2);
        stats.Highest.Should().Be(4.0);
        stats.Lowest.Should().Be(1.0);
    }

    [Fact]
    public async Task PlexFailureOnOneKey_DegradesGracefully()
    {
        // An unreachable Plex on one key shouldn't fail the readout: report what the other key returned.
        StoredKeys(10, 20);
        _plex.GetArtistTracks(10, Token).Returns(new[] { Track(10) });
        _plex.GetArtistTracks(20, Token).Returns<PlexTrack[]>(_ => throw new HttpRequestException("down"));

        var stats = await _sut.ForUser(User, new ArtistKey(Artist));

        stats.Present.Should().BeTrue();
        stats.RatedCount.Should().Be(1);
        stats.Highest.Should().Be(5.0);
    }

    [Fact]
    public async Task UserWithNoPlexAccount_ReportsAbsentAndNeverReadsPlex()
    {
        // The bug this whole change exists to kill: with no linked account there is no sensible token
        // to fall back on, and the one that *is* to hand — the server owner's — is precisely the wrong
        // answer. Answer "nothing to show" instead, which the UI already hides.
        StoredKeys(10);
        _links.Get("stranger").Returns((PlexLink?)null);

        var stats = await _sut.ForUser("stranger", new ArtistKey(Artist));

        stats.Present.Should().BeFalse();
        stats.RatedCount.Should().Be(0);
        await _plex.DidNotReceive().GetArtistTracks(Arg.Any<int>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ReadsWithTheAskingUsersOwnToken()
    {
        // Two users, two tokens, two different sets of stars for the same artist.
        StoredKeys(10);
        _links.Get("other").Returns(new PlexLink(
            "other", "7", "justin", null, "justins-token", DateTimeOffset.UnixEpoch));
        _plex.GetArtistTracks(10, Token).Returns(new[] { Track(10) });          // 5 stars
        _plex.GetArtistTracks(10, "justins-token").Returns(new[] { Track(2) }); // 1 star

        (await _sut.ForUser(User, new ArtistKey(Artist))).Average.Should().Be(5.0);
        (await _sut.ForUser("other", new ArtistKey(Artist))).Average.Should().Be(1.0);
    }
}
