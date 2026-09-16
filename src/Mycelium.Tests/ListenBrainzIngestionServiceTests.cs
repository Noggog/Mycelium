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

public class ListenBrainzIngestionServiceTests
{
    private readonly IMusicBrainzApi _musicBrainz = Substitute.For<IMusicBrainzApi>();
    private readonly IListenBrainzApi _listenBrainz = Substitute.For<IListenBrainzApi>();
    private readonly IRelatedArtistRepo _repo = Substitute.For<IRelatedArtistRepo>();

    private static readonly ArtistKey Radiohead = new("Radiohead");
    private const string RadioheadMbid = "a74b1b7f-71a5-4011-9441-d0b5e4122711";

    private ListenBrainzIngestionService Build(bool enabled = true)
    {
        // Real resolver over the mocked MusicBrainz API + an in-memory cache, mirroring how the
        // Deezer ingestion test wires its concrete resolver.
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var resolver = new MusicBrainzArtistResolver(_musicBrainz, cache, Substitute.For<IArtistCatalogRepo>());
        var endpoint = new ListenBrainzEndpointInfo("https://mb", "https://lb", "contact", "algo", Enabled: enabled);
        return new ListenBrainzIngestionService(
            _listenBrainz, resolver, _repo, endpoint,
            new RelatedStalenessPolicy(TimeSpan.FromDays(30)),
            NullLogger<ListenBrainzIngestionService>.Instance);
    }

    private static MusicBrainzArtist MbArtist(string mbid, string name) =>
        new() { Id = mbid, Name = name, Score = 100 };

    private static ListenBrainzSimilarArtist Similar(string mbid, string? name, double score = 1) =>
        new() { ArtistMbid = mbid, Name = name, Score = score };

    [Fact]
    public async Task Fresh_cache_is_served_without_touching_the_network_or_writing()
    {
        var fresh = new ArtistRelations(Radiohead, "listenbrainz",
            new[] { new RelatedArtist(new ArtistKey("Muse"), null) }, DateTimeOffset.UtcNow);
        _repo.Get(Radiohead, "listenbrainz").Returns(fresh);

        var result = await Build().EnsureRelated(Radiohead);

        result.Should().BeSameAs(fresh);
        await _musicBrainz.DidNotReceive().SearchArtists(Arg.Any<string>(), Arg.Any<int>());
        await _listenBrainz.DidNotReceive().GetSimilarArtists(Arg.Any<string>());
        await _repo.DidNotReceive().Upsert(Arg.Any<ArtistRelations>());
    }

    [Fact]
    public async Task Missing_entry_resolves_mbid_fetches_and_persists_tagged_listenbrainz()
    {
        _repo.Get(Radiohead, "listenbrainz").Returns((ArtistRelations?)null);
        _musicBrainz.SearchArtists("Radiohead", Arg.Any<int>()).Returns(new[] { MbArtist(RadioheadMbid, "Radiohead") });
        _listenBrainz.GetSimilarArtists(RadioheadMbid).Returns(new[]
        {
            Similar("mbid-muse", "Muse", 9000),
            Similar("mbid-coldplay", "Coldplay", 4000),
        });

        ArtistRelations? upserted = null;
        await _repo.Upsert(Arg.Do<ArtistRelations>(x => upserted = x));

        var result = await Build().EnsureRelated(Radiohead);

        upserted.Should().NotBeNull();
        upserted!.Source.Should().Be("listenbrainz");
        upserted.Related.Select(r => r.ArtistKey.ArtistName).Should().Equal("Muse", "Coldplay");
        upserted.Related.Should().OnlyContain(r => r.ImageUrl == null); // ListenBrainz has no artwork
        result.Should().BeSameAs(upserted);
    }

    [Fact]
    public async Task The_seed_itself_and_blank_names_are_filtered_out()
    {
        _repo.Get(Radiohead, "listenbrainz").Returns((ArtistRelations?)null);
        _musicBrainz.SearchArtists("Radiohead", Arg.Any<int>()).Returns(new[] { MbArtist(RadioheadMbid, "Radiohead") });
        _listenBrainz.GetSimilarArtists(RadioheadMbid).Returns(new[]
        {
            Similar(RadioheadMbid, "Radiohead"), // the seed is included in the response — drop it
            Similar("mbid-muse", "Muse"),
            Similar("mbid-blank", "   "),
            Similar("mbid-null", null),
        });

        ArtistRelations? upserted = null;
        await _repo.Upsert(Arg.Do<ArtistRelations>(x => upserted = x));

        await Build().EnsureRelated(Radiohead);

        upserted!.Related.Select(r => r.ArtistKey.ArtistName).Should().Equal("Muse");
    }

    [Fact]
    public async Task Unreachable_musicbrainz_does_not_persist_and_returns_existing()
    {
        var existing = new ArtistRelations(Radiohead, "listenbrainz",
            new[] { new RelatedArtist(new ArtistKey("Muse"), null) },
            DateTimeOffset.UtcNow - TimeSpan.FromDays(99));
        _repo.Get(Radiohead, "listenbrainz").Returns(existing);
        _musicBrainz.SearchArtists("Radiohead", Arg.Any<int>()).Returns((MusicBrainzArtist[]?)null);

        var result = await Build().EnsureRelated(Radiohead);

        result.Should().BeSameAs(existing);
        await _listenBrainz.DidNotReceive().GetSimilarArtists(Arg.Any<string>());
        await _repo.DidNotReceive().Upsert(Arg.Any<ArtistRelations>());
    }

    /// <summary>
    /// A confirmed no-match replaces whatever edges are stored with none: they came from an MBID the
    /// artist no longer resolves to — the "Noel Brass Jr." inheriting Canadian Brass's neighbours case.
    /// </summary>
    [Fact]
    public async Task Confirmed_no_match_replaces_stale_edges_with_none()
    {
        var existing = new ArtistRelations(new ArtistKey("Noel Brass Jr."), "listenbrainz",
            new[] { new RelatedArtist(new ArtistKey("*NSYNC"), null) },
            DateTimeOffset.UtcNow - TimeSpan.FromDays(99));
        _repo.Get(existing.Artist, "listenbrainz").Returns(existing);
        _musicBrainz.SearchArtists("Noel Brass Jr.", Arg.Any<int>())
            .Returns(new[] { MbArtist("mbid-canadian-brass", "Canadian Brass") });

        ArtistRelations? upserted = null;
        await _repo.Upsert(Arg.Do<ArtistRelations>(x => upserted = x));

        var result = await Build().EnsureRelated(existing.Artist);

        upserted.Should().NotBeNull();
        upserted!.Related.Should().BeEmpty();
        result.Related.Should().BeEmpty();
        await _listenBrainz.DidNotReceive().GetSimilarArtists(Arg.Any<string>());
    }

    [Fact]
    public async Task Disabled_source_never_touches_the_network()
    {
        _repo.Get(Radiohead, "listenbrainz").Returns((ArtistRelations?)null);

        var result = await Build(enabled: false).EnsureRelated(Radiohead);

        result.Source.Should().Be("listenbrainz");
        result.Related.Should().BeEmpty();
        await _musicBrainz.DidNotReceive().SearchArtists(Arg.Any<string>(), Arg.Any<int>());
        await _listenBrainz.DidNotReceive().GetSimilarArtists(Arg.Any<string>());
    }

    [Fact]
    public async Task Force_refresh_refetches_even_when_cache_is_fresh()
    {
        var fresh = new ArtistRelations(Radiohead, "listenbrainz", Array.Empty<RelatedArtist>(), DateTimeOffset.UtcNow);
        _repo.Get(Radiohead, "listenbrainz").Returns(fresh);
        _musicBrainz.SearchArtists("Radiohead", Arg.Any<int>()).Returns(new[] { MbArtist(RadioheadMbid, "Radiohead") });
        _listenBrainz.GetSimilarArtists(RadioheadMbid).Returns(Array.Empty<ListenBrainzSimilarArtist>());

        await Build().EnsureRelated(Radiohead, forceRefresh: true);

        await _musicBrainz.Received(1).SearchArtists("Radiohead", Arg.Any<int>());
        await _repo.Received(1).Upsert(Arg.Any<ArtistRelations>());
    }
}
