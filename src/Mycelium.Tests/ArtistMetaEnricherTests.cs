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

public class ArtistMetaEnricherTests
{
    private readonly IDeezerApi _deezer = Substitute.For<IDeezerApi>();
    private readonly ArtistMetaEnricher _sut;

    public ArtistMetaEnricherTests()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var resolver = new DeezerArtistResolver(_deezer, cache, Substitute.For<IArtistCatalogRepo>());
        _sut = new ArtistMetaEnricher(resolver, NullLogger<ArtistMetaEnricher>.Instance);
    }

    private static UnifiedRelatedArtist Rel(string name, string? image, params string[] sources) =>
        new(new ArtistKey(name), image, sources);

    // The enricher resolves names through DeezerArtistResolver, which searches for candidates so it
    // can tell a real miss (empty) from an unanswered call (null) — stub that, not SearchArtist.
    private void DeezerFinds(string name, DeezerArtist artist) =>
        _deezer.SearchArtists(name, Arg.Any<int>()).Returns(new[] { artist });

    [Fact]
    public async Task Fills_a_missing_image_from_deezer_regardless_of_recommending_source()
    {
        // ListenBrainz recommended Ariana Grande but carries no image; Deezer has her photo.
        DeezerFinds("Ariana Grande", new DeezerArtist { id = 1, name = "Ariana Grande", picture_xl = "ari.jpg" });

        var input = new UnifiedRelations(new ArtistKey("100 gecs"), new[]
        {
            Rel("Ariana Grande", null, "listenbrainz"),
            Rel("Alice Gas", "alice.jpg", "deezer", "listenbrainz"), // already has an image
        });

        var result = await _sut.EnrichImages(input);

        result.Related.Single(r => r.ArtistKey.ArtistName == "Ariana Grande").ImageUrl.Should().Be("ari.jpg");
        // An artist that already had an image is left untouched — no redundant Deezer lookup.
        result.Related.Single(r => r.ArtistKey.ArtistName == "Alice Gas").ImageUrl.Should().Be("alice.jpg");
        await _deezer.DidNotReceive().SearchArtists("Alice Gas", Arg.Any<int>());
    }

    [Fact]
    public async Task Leaves_an_artist_with_no_deezer_match_imageless()
    {
        _deezer.SearchArtists("Obscure LB Artist", Arg.Any<int>()).Returns(Array.Empty<DeezerArtist>());

        var input = new UnifiedRelations(new ArtistKey("seed"), new[] { Rel("Obscure LB Artist", null, "listenbrainz") });

        var result = await _sut.EnrichImages(input);

        result.Related.Single().ImageUrl.Should().BeNull();
    }

    [Fact]
    public async Task Feed_enrichment_touches_artist_items_only_not_missing_albums()
    {
        DeezerFinds("Aphex Twin", new DeezerArtist { id = 2, name = "Aphex Twin", picture_xl = "aphex.jpg" });

        var items = new FeedItem[]
        {
            new(FeedKind.RecommendedArtist, new ArtistKey("Aphex Twin"), null, null, 1, new[] { "listenbrainz" }, null),
            new(FeedKind.MissingAlbum, new ArtistKey("Aphex Twin"), "Drukqs", null, 1, Array.Empty<string>(), 99),
        };

        var result = await _sut.EnrichImages(items);

        result.Single(i => i.Kind == FeedKind.RecommendedArtist).ImageUrl.Should().Be("aphex.jpg");
        // The album item keeps its (album-art) image path — never resolved by artist name.
        result.Single(i => i.Kind == FeedKind.MissingAlbum).ImageUrl.Should().BeNull();
    }
}
