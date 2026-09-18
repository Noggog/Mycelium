using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

public class ManualRecommendationsTests
{
    /// <summary>An in-memory repo, so writes and reads round-trip the way Mongo's do.</summary>
    private sealed class FakeRepo : IManualRecommendationRepo
    {
        public readonly Dictionary<string, ManualRecommendation> Rows = new();

        public Task<IReadOnlyList<ManualRecommendation>> GetAll() =>
            Task.FromResult<IReadOnlyList<ManualRecommendation>>(Rows.Values.ToArray());

        public Task Upsert(ManualRecommendation recommendation)
        {
            Rows[recommendation.Id] = recommendation;
            return Task.CompletedTask;
        }

        public Task<bool> Delete(string id) => Task.FromResult(Rows.Remove(id));
    }

    private readonly FakeRepo _repo = new();
    private readonly ManualRecommendations _sut;

    public ManualRecommendationsTests()
    {
        _sut = new ManualRecommendations(_repo);
    }

    private async Task<string[]> RelatedNames(string artist) =>
        (await _sut.RelatedTo(new ArtistKey(artist)))?.Related.Select(r => r.ArtistKey.ArtistName).ToArray()
        ?? Array.Empty<string>();

    [Fact]
    public async Task A_pair_recommends_each_artist_to_the_other()
    {
        await _sut.Add("Steve Roach", "Hearts of Space", "dev");

        (await RelatedNames("Steve Roach")).Should().Equal("Hearts of Space");
        (await RelatedNames("Hearts of Space")).Should().Equal("Steve Roach");
    }

    [Fact]
    public async Task Edges_carry_the_manual_source_tag()
    {
        await _sut.Add("Steve Roach", "Hearts of Space", "dev");

        var relations = await _sut.RelatedTo(new ArtistKey("Steve Roach"));

        relations!.Source.Should().Be(ManualRecommendations.SourceName);
    }

    [Fact]
    public async Task Lookup_ignores_case_and_accents()
    {
        await _sut.Add("steve roach", "Björk", "dev");

        (await RelatedNames("Steve Roach")).Should().Equal("Björk");
        (await RelatedNames("BJORK")).Should().Equal("steve roach");
    }

    [Fact]
    public async Task An_artist_with_no_pairs_has_no_manual_edges()
    {
        await _sut.Add("Steve Roach", "Hearts of Space", "dev");

        (await _sut.RelatedTo(new ArtistKey("Brian Eno"))).Should().BeNull();
    }

    [Fact]
    public async Task Entering_the_same_pair_reversed_replaces_rather_than_duplicates()
    {
        await _sut.Add("Steve Roach", "Hearts of Space", "dev");
        await _sut.Add("hearts of space", "Steve Roach", "dev");

        _repo.Rows.Should().HaveCount(1);
        (await RelatedNames("Steve Roach")).Should().HaveCount(1);
    }

    [Fact]
    public async Task One_artist_can_be_paired_with_several()
    {
        await _sut.Add("Hearts of Space", "Steve Roach", "dev");
        await _sut.Add("Hearts of Space", "Robert Rich", "dev");

        (await RelatedNames("Hearts of Space")).Should().BeEquivalentTo("Steve Roach", "Robert Rich");
    }

    [Fact]
    public async Task Writes_are_visible_to_the_next_read_despite_the_cache()
    {
        (await RelatedNames("Steve Roach")).Should().BeEmpty(); // warm the cache

        var added = await _sut.Add("Steve Roach", "Hearts of Space", "dev");
        (await RelatedNames("Steve Roach")).Should().Equal("Hearts of Space");

        await _sut.Remove(added.Id);
        (await RelatedNames("Steve Roach")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Steve Roach", "steve roach")]
    [InlineData("Björk", "Bjork")]
    [InlineData("  ", "Steve Roach")]
    public async Task Rejects_a_blank_name_or_an_artist_paired_with_itself(string a, string b)
    {
        var act = () => _sut.Add(a, b, "dev");

        await act.Should().ThrowAsync<ArgumentException>();
        _repo.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task The_related_read_merges_manual_edges_with_the_sources()
    {
        await _sut.Add("Steve Roach", "Hearts of Space", "dev");
        var edges = Substitute.For<IRelatedArtistRepo>();
        edges.GetAllSources(Arg.Any<ArtistKey>()).Returns(new[]
        {
            new ArtistRelations(new ArtistKey("Steve Roach"), "deezer",
                new[] { new RelatedArtist(new ArtistKey("Robert Rich"), "img") }, DateTimeOffset.UtcNow),
        });
        var interactor = new RelatedArtistInteractor(
            Array.Empty<ISimilaritySource>(), edges, _sut, NullLogger<RelatedArtistInteractor>.Instance);

        var unified = await interactor.GetRelated(new ArtistKey("Steve Roach"), readOnly: true);

        unified.Related.Select(r => (r.ArtistKey.ArtistName, r.Sources.Single()))
            .Should().BeEquivalentTo(new[] { ("Robert Rich", "deezer"), ("Hearts of Space", "manual") });
    }
}

public class PlexArtistImageTests
{
    [Fact]
    public void No_thumb_means_no_url()
    {
        PlexArtistImage.Url("Hearts of Space", null).Should().BeNull();
    }

    [Fact]
    public void Url_names_the_artist_and_versions_by_the_thumb_timestamp()
    {
        PlexArtistImage.Url("Simon & Garfunkel", "/library/metadata/123/thumb/1699999999")
            .Should().Be("/api/artists/plex-image?artist=Simon%20%26%20Garfunkel&v=1699999999");
    }
}
