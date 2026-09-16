using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mycelium.Backend;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.ListenBrainz.Inputs;
using Mycelium.ListenBrainz.Models;
using Mycelium.ListenBrainz.Services;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

public class MusicBrainzRelinkerTests
{
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly IRelatedArtistRepo _related = Substitute.For<IRelatedArtistRepo>();
    private readonly IMusicBrainzApi _musicBrainz = Substitute.For<IMusicBrainzApi>();
    private readonly IListenBrainzApi _listenBrainz = Substitute.For<IListenBrainzApi>();
    private readonly IDiscoveryQueueRebuilder _queues = Substitute.For<IDiscoveryQueueRebuilder>();
    private readonly MusicBrainzRelinker _sut;

    public MusicBrainzRelinkerTests()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var resolver = new MusicBrainzArtistResolver(_musicBrainz, cache, _catalog);
        var ingestion = new ListenBrainzIngestionService(
            _listenBrainz, resolver, _related,
            new ListenBrainzEndpointInfo("https://mb", "https://lb", "contact", "algo", Enabled: true),
            new RelatedStalenessPolicy(TimeSpan.FromDays(30)),
            NullLogger<ListenBrainzIngestionService>.Instance);
        _sut = new MusicBrainzRelinker(
            _catalog, _related, resolver, ingestion, _queues, NullLogger<MusicBrainzRelinker>.Instance);

        _related.GetArtistNamesWithEdges("listenbrainz").Returns(Array.Empty<string>());
        _listenBrainz.GetSimilarArtists(Arg.Any<string>()).Returns(Array.Empty<ListenBrainzSimilarArtist>());
    }

    private void Catalog(params CatalogArtist[] artists)
    {
        _catalog.GetAllPresent().Returns(artists);
        foreach (var a in artists)
        {
            _catalog.GetMusicBrainz(a.ArtistKey).Returns((a.MusicBrainz!, a.MusicBrainzOverride));
        }
    }

    private static CatalogArtist Linked(string name, string mbid, string mbName, bool pinned = false) =>
        new(new ArtistKey(name), null, DateTimeOffset.UtcNow,
            MusicBrainz: new MusicBrainzIdentity(mbid, mbName), MusicBrainzOverride: pinned);

    [Fact]
    public async Task Matching_links_are_kept_without_a_search_and_pins_are_never_touched()
    {
        Catalog(
            Linked("Radiohead", "mbid-rh", "Radiohead"),
            Linked("ALEX", "mbid-alex-warren", "Alex Warren", pinned: true));

        await _sut.RunAsync();

        var status = _sut.GetStatus();
        status.Kept.Should().Be(1);
        status.Skipped.Should().Be(1);
        status.QueuesRebuilt.Should().BeFalse();
        await _musicBrainz.DidNotReceive().SearchArtists(Arg.Any<string>(), Arg.Any<int>());
        await _queues.DidNotReceive().RebuildAll();
    }

    [Fact]
    public async Task A_wrong_link_with_no_real_match_is_cleared_with_its_edges_and_queues_rebuild()
    {
        Catalog(Linked("Noel Brass Jr.", "mbid-canadian-brass", "Canadian Brass"));
        _musicBrainz.SearchArtists("Noel Brass Jr.", Arg.Any<int>())
            .Returns(new[] { new MusicBrainzArtist { Id = "mbid-canadian-brass", Name = "Canadian Brass" } });

        ArtistRelations? upserted = null;
        await _related.Upsert(Arg.Do<ArtistRelations>(x => upserted = x));

        await _sut.RunAsync();

        _sut.GetStatus().Cleared.Should().Be(1);
        await _catalog.Received().ClearMusicBrainzOverride(new ArtistKey("Noel Brass Jr."));
        upserted!.Related.Should().BeEmpty();
        await _queues.Received(1).RebuildAll();
    }

    [Fact]
    public async Task A_mismatched_name_that_rechecks_to_the_same_mbid_is_unchanged()
    {
        Catalog(Linked("Ryuichi Sakamoto", "mbid-rs", "坂本龍一"));
        _musicBrainz.SearchArtists("Ryuichi Sakamoto", Arg.Any<int>()).Returns(new[]
        {
            new MusicBrainzArtist
            {
                Id = "mbid-rs", Name = "坂本龍一",
                Aliases = new() { new MusicBrainzAlias { Name = "Ryuichi Sakamoto" } },
            },
        });

        await _sut.RunAsync();

        _sut.GetStatus().Unchanged.Should().Be(1);
        await _listenBrainz.DidNotReceive().GetSimilarArtists(Arg.Any<string>());
        await _queues.DidNotReceive().RebuildAll();
    }

    [Fact]
    public async Task Unowned_artists_with_edges_are_rechecked_unless_the_user_decided()
    {
        Catalog();
        _related.GetArtistNamesWithEdges("listenbrainz").Returns(new[] { "Big Thief", "Pinned Act", "Detached Act" });
        _catalog.GetMusicBrainz(new ArtistKey("Pinned Act"))
            .Returns((new MusicBrainzIdentity("mbid-pinned", "Whatever"), true));
        _catalog.IsMusicBrainzUnlinked(new ArtistKey("Detached Act")).Returns(true);
        _musicBrainz.SearchArtists("Big Thief", Arg.Any<int>())
            .Returns(new[] { new MusicBrainzArtist { Id = "mbid-bt", Name = "Big Thief" } });

        await _sut.RunAsync();

        var status = _sut.GetStatus();
        status.Relinked.Should().Be(1);
        status.Skipped.Should().Be(2);
        await _listenBrainz.Received(1).GetSimilarArtists("mbid-bt");
        await _musicBrainz.DidNotReceive().SearchArtists("Pinned Act", Arg.Any<int>());
        await _musicBrainz.DidNotReceive().SearchArtists("Detached Act", Arg.Any<int>());
    }

    [Fact]
    public async Task An_unanswered_search_is_an_error_that_changes_nothing()
    {
        Catalog(Linked("Noel Brass Jr.", "mbid-canadian-brass", "Canadian Brass"));
        _musicBrainz.SearchArtists("Noel Brass Jr.", Arg.Any<int>()).Returns((MusicBrainzArtist[]?)null);

        await _sut.RunAsync();

        _sut.GetStatus().Errors.Should().Be(1);
        await _catalog.DidNotReceive().ClearMusicBrainzOverride(Arg.Any<ArtistKey>());
        await _related.DidNotReceive().Upsert(Arg.Any<ArtistRelations>());
        await _queues.DidNotReceive().RebuildAll();
    }
}
