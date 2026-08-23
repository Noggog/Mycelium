using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mycelium.Backend;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Deezer.Models;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

public class SimilarityIngestionServiceTests
{
    private readonly IDeezerApi _deezer = Substitute.For<IDeezerApi>();
    private readonly IRelatedArtistRepo _repo = Substitute.For<IRelatedArtistRepo>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly SimilarityIngestionService _sut;

    private static readonly ArtistKey Radiohead = new("Radiohead");

    public SimilarityIngestionServiceTests()
    {
        // Use a real resolver (concrete class) wired to the mocked Deezer API + an in-memory cache;
        // ingestion now resolves through it so a pinned override is honored and the id is captured.
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var resolver = new DeezerArtistResolver(_deezer, cache, _catalog);
        _sut = new SimilarityIngestionService(
            _deezer, resolver, _repo, _catalog,
            new RelatedStalenessPolicy(TimeSpan.FromDays(30)),
            NullLogger<SimilarityIngestionService>.Instance);
    }

    private static DeezerArtist Artist(long id, string name, string? image = null) =>
        new() { id = id, name = name, picture_xl = image };

    // Name resolution runs through the candidate search, not the single-result convenience — it needs
    // to tell "Deezer says no such artist" (empty) from "Deezer didn't answer" (null).
    private void DeezerFinds(string name, DeezerArtist artist) =>
        _deezer.SearchArtists(name, Arg.Any<int>()).Returns(new[] { artist });

    private void DeezerFindsNothing(string name) =>
        _deezer.SearchArtists(name, Arg.Any<int>()).Returns(Array.Empty<DeezerArtist>());

    [Fact]
    public async Task Fresh_cache_is_served_without_touching_Deezer_or_writing()
    {
        var fresh = new ArtistRelations(Radiohead, "deezer",
            new[] { new RelatedArtist(new ArtistKey("Beach House"), "img") }, DateTimeOffset.UtcNow);
        _repo.Get(Radiohead, "deezer").Returns(fresh);

        var result = await _sut.EnsureRelated(Radiohead);

        result.Should().BeSameAs(fresh);
        await _deezer.DidNotReceive().SearchArtists(Arg.Any<string>(), Arg.Any<int>());
        await _repo.DidNotReceive().Upsert(Arg.Any<ArtistRelations>());
    }

    [Fact]
    public async Task Stale_entry_triggers_a_refetch_and_upsert()
    {
        var stale = new ArtistRelations(Radiohead, "deezer", Array.Empty<RelatedArtist>(),
            DateTimeOffset.UtcNow - TimeSpan.FromDays(31));
        _repo.Get(Radiohead, "deezer").Returns(stale);
        DeezerFinds("Radiohead", Artist(399, "Radiohead"));
        _deezer.GetRelated(399).Returns(new[] { Artist(1, "Beach House", "img") });

        await _sut.EnsureRelated(Radiohead);

        await _deezer.Received(1).SearchArtists("Radiohead", Arg.Any<int>());
        await _repo.Received(1).Upsert(Arg.Any<ArtistRelations>());
    }

    [Fact]
    public async Task Missing_entry_fetches_maps_and_persists_related_with_images()
    {
        _repo.Get(Radiohead, "deezer").Returns((ArtistRelations?)null);
        DeezerFinds("Radiohead", Artist(399, "Radiohead", "seed-img"));
        _deezer.GetRelated(399).Returns(new[]
        {
            Artist(1, "Beach House", "bh-img"),
            Artist(2, "Björk"), // no image
        });

        ArtistRelations? upserted = null;
        await _repo.Upsert(Arg.Do<ArtistRelations>(x => upserted = x));

        var result = await _sut.EnsureRelated(Radiohead);

        upserted.Should().NotBeNull();
        upserted!.Source.Should().Be("deezer");
        upserted.Related.Select(r => r.ArtistKey.ArtistName).Should().Equal("Beach House", "Björk");
        upserted.Related.Single(r => r.ArtistKey.ArtistName == "Beach House").ImageUrl.Should().Be("bh-img");
        upserted.Related.Single(r => r.ArtistKey.ArtistName == "Björk").ImageUrl.Should().BeNull();
        result.Should().BeSameAs(upserted);
    }

    [Fact]
    public async Task Force_refresh_refetches_even_when_cache_is_fresh()
    {
        var fresh = new ArtistRelations(Radiohead, "deezer", Array.Empty<RelatedArtist>(), DateTimeOffset.UtcNow);
        _repo.Get(Radiohead, "deezer").Returns(fresh);
        DeezerFinds("Radiohead", Artist(399, "Radiohead"));
        _deezer.GetRelated(399).Returns(Array.Empty<DeezerArtist>());

        await _sut.EnsureRelated(Radiohead, forceRefresh: true);

        await _deezer.Received(1).SearchArtists("Radiohead", Arg.Any<int>());
        await _repo.Received(1).Upsert(Arg.Any<ArtistRelations>());
    }

    [Fact]
    public async Task Blank_related_names_are_filtered_out()
    {
        _repo.Get(Radiohead, "deezer").Returns((ArtistRelations?)null);
        DeezerFinds("Radiohead", Artist(399, "Radiohead"));
        _deezer.GetRelated(399).Returns(new[]
        {
            Artist(1, "Beach House"),
            Artist(2, "   "),
            Artist(3, ""),
        });

        ArtistRelations? upserted = null;
        await _repo.Upsert(Arg.Do<ArtistRelations>(x => upserted = x));

        await _sut.EnsureRelated(Radiohead);

        upserted!.Related.Select(r => r.ArtistKey.ArtistName).Should().Equal("Beach House");
    }

    [Fact]
    public async Task No_Deezer_match_does_not_persist_and_returns_existing()
    {
        var existing = new ArtistRelations(Radiohead, "deezer",
            new[] { new RelatedArtist(new ArtistKey("Beach House"), "img") },
            DateTimeOffset.UtcNow - TimeSpan.FromDays(99));
        _repo.Get(Radiohead, "deezer").Returns(existing);
        DeezerFindsNothing("Radiohead"); // Deezer answered, with nothing

        var result = await _sut.EnsureRelated(Radiohead);

        result.Should().BeSameAs(existing);
        await _repo.DidNotReceive().Upsert(Arg.Any<ArtistRelations>());
        await _catalog.DidNotReceive().BackfillImages(Arg.Any<IReadOnlyList<ArtistMetadata>>());
    }

    [Fact]
    public async Task No_Deezer_match_with_nothing_cached_returns_empty_edge_set()
    {
        _repo.Get(Radiohead, "deezer").Returns((ArtistRelations?)null);
        DeezerFindsNothing("Radiohead");

        var result = await _sut.EnsureRelated(Radiohead);

        result.Artist.Should().Be(Radiohead);
        result.Source.Should().Be("deezer");
        result.Related.Should().BeEmpty();
    }

    [Fact]
    public async Task Image_backfill_includes_seed_and_related_with_images_only()
    {
        _repo.Get(Radiohead, "deezer").Returns((ArtistRelations?)null);
        DeezerFinds("Radiohead", Artist(399, "Radiohead", "seed-img"));
        _deezer.GetRelated(399).Returns(new[]
        {
            Artist(1, "Beach House", "bh-img"),
            Artist(2, "Björk"), // no image -> excluded from backfill
        });

        IReadOnlyList<ArtistMetadata>? backfilled = null;
        _catalog.BackfillImages(Arg.Do<IReadOnlyList<ArtistMetadata>>(x => backfilled = x)).Returns(1);

        await _sut.EnsureRelated(Radiohead);

        backfilled.Should().NotBeNull();
        backfilled!.Should().BeEquivalentTo(new[]
        {
            new ArtistMetadata(Radiohead, "seed-img"),
            new ArtistMetadata(new ArtistKey("Beach House"), "bh-img"),
        });
    }
}
