using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.ListenBrainz.Models;
using Mycelium.ListenBrainz.Services;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

public class MusicBrainzArtistResolverTests
{
    private readonly IMusicBrainzApi _musicBrainz = Substitute.For<IMusicBrainzApi>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly IDistributedCache _cache =
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
    private readonly MusicBrainzArtistResolver _sut;

    private static readonly ArtistKey Alex = new("ALEX");
    private const string PinnedMbid = "11111111-1111-1111-1111-111111111111";
    private const string SearchMbid = "22222222-2222-2222-2222-222222222222";

    public MusicBrainzArtistResolverTests()
    {
        _sut = new MusicBrainzArtistResolver(_musicBrainz, _cache, _catalog);
    }

    [Fact]
    public async Task Override_resolves_by_pinned_mbid_and_never_name_searches()
    {
        var pinned = new MusicBrainzIdentity(PinnedMbid, "ALEX", "synthwave project");
        _catalog.GetMusicBrainz(Alex).Returns((pinned, true));

        var result = await _sut.ResolveIdentity("ALEX");

        result.Should().Be(pinned);
        await _musicBrainz.DidNotReceive().SearchArtists(Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public async Task No_override_falls_back_to_name_search_and_captures_the_mbid()
    {
        _catalog.GetMusicBrainz(Alex).Returns(((MusicBrainzIdentity, bool)?)null);
        _musicBrainz.SearchArtists("ALEX", Arg.Any<int>())
            .Returns(new[] { new MusicBrainzArtist { Id = SearchMbid, Name = "Alex" } });

        var result = await _sut.ResolveIdentity("ALEX");

        result!.Mbid.Should().Be(SearchMbid);
        await _catalog.Received(1).SetMusicBrainzIdentity(Alex,
            Arg.Is<MusicBrainzIdentity>(i => i.Mbid == SearchMbid), false);
    }

    /// <summary>
    /// The top hit is not an answer unless it goes by the name: "ALEX" is not "Alex Warren". A confirmed
    /// no-match also takes down a stored automatic link, and the album release groups resolved under it.
    /// </summary>
    [Fact]
    public async Task A_top_hit_with_another_name_is_no_match_and_clears_the_stored_link()
    {
        _catalog.GetMusicBrainz(Alex).Returns((new MusicBrainzIdentity(SearchMbid, "Alex Warren"), false));
        _musicBrainz.SearchArtists("ALEX", Arg.Any<int>())
            .Returns(new[] { new MusicBrainzArtist { Id = SearchMbid, Name = "Alex Warren", Score = 100 } });

        var result = await _sut.Resolve("ALEX");

        result.Identity.Should().BeNull();
        result.Unreachable.Should().BeFalse();
        await _catalog.Received(1).ClearMusicBrainzOverride(Alex);
        await _catalog.Received(1).ClearAlbumReleaseGroups(Alex);
        (await _cache.GetStringAsync("musicbrainz:artist:v2:alex")).Should().Be("");
    }

    [Fact]
    public async Task An_alias_match_further_down_the_results_is_accepted()
    {
        _catalog.GetMusicBrainz(Arg.Any<ArtistKey>()).Returns(((MusicBrainzIdentity, bool)?)null);
        _musicBrainz.SearchArtists("NSYNC", Arg.Any<int>()).Returns(new[]
        {
            new MusicBrainzArtist { Id = "mbid-other", Name = "Sync", Score = 100 },
            new MusicBrainzArtist
            {
                Id = "mbid-nsync", Name = "*NSYNC", Score = 90,
                Aliases = new() { new MusicBrainzAlias { Name = "NSYNC" } },
            },
        });

        (await _sut.ResolveIdentity("NSYNC"))!.Mbid.Should().Be("mbid-nsync");
    }

    [Fact]
    public async Task An_unanswered_search_is_not_cached_and_moves_nothing()
    {
        _catalog.GetMusicBrainz(Alex).Returns((new MusicBrainzIdentity(SearchMbid, "Alex"), false));
        _musicBrainz.SearchArtists("ALEX", Arg.Any<int>()).Returns((MusicBrainzArtist[]?)null);

        var result = await _sut.Resolve("ALEX");

        result.Unreachable.Should().BeTrue();
        (await _cache.GetStringAsync("musicbrainz:artist:v2:alex")).Should().BeNull();
        await _catalog.DidNotReceive().ClearMusicBrainzOverride(Arg.Any<ArtistKey>());
        await _catalog.DidNotReceive().ClearAlbumReleaseGroups(Arg.Any<ArtistKey>());
    }

    [Fact]
    public async Task Fresh_bypasses_a_cached_guess()
    {
        _catalog.GetMusicBrainz(Alex).Returns(((MusicBrainzIdentity, bool)?)null);
        await _cache.SetStringAsync("musicbrainz:artist:v2:alex", $"{SearchMbid}\tAlex Warren\t");
        _musicBrainz.SearchArtists("ALEX", Arg.Any<int>())
            .Returns(new[] { new MusicBrainzArtist { Id = PinnedMbid, Name = "Alex" } });

        var result = await _sut.Resolve("ALEX", fresh: true);

        result.Identity!.Mbid.Should().Be(PinnedMbid);
    }

    [Fact]
    public async Task Cache_hit_still_captures_the_mbid_onto_an_empty_catalog()
    {
        // A warm (Redis) cache must not stop the catalog from being populated for the Sources tab.
        _catalog.GetMusicBrainz(Alex).Returns(((MusicBrainzIdentity, bool)?)null);
        // Cached value shape is "mbid\tname\tdisambiguation".
        await _cache.SetStringAsync("musicbrainz:artist:v2:alex", $"{SearchMbid}\tAlex Warren\t");

        var result = await _sut.ResolveIdentity("ALEX");

        result!.Mbid.Should().Be(SearchMbid);
        await _musicBrainz.DidNotReceive().SearchArtists(Arg.Any<string>(), Arg.Any<int>()); // served from cache
        await _catalog.Received(1).SetMusicBrainzIdentity(Alex,
            Arg.Is<MusicBrainzIdentity>(i => i.Mbid == SearchMbid), false);
    }

    [Fact]
    public async Task Cache_hit_skips_the_write_when_catalog_already_has_that_mbid()
    {
        var identity = new MusicBrainzIdentity(SearchMbid, "Alex Warren", null);
        _catalog.GetMusicBrainz(Alex).Returns((identity, false));
        await _cache.SetStringAsync("musicbrainz:artist:v2:alex", $"{SearchMbid}\tAlex Warren\t");

        await _sut.ResolveIdentity("ALEX");

        await _catalog.DidNotReceive().SetMusicBrainzIdentity(
            Arg.Any<ArtistKey>(), Arg.Any<MusicBrainzIdentity>(), false);
    }

    [Fact]
    public async Task SetOverride_looks_up_by_mbid_and_persists_a_sticky_pin()
    {
        _musicBrainz.GetArtist(PinnedMbid).Returns(new MusicBrainzArtist { Id = PinnedMbid, Name = "ALEX" });

        var result = await _sut.SetOverride("ALEX", PinnedMbid);

        result!.Mbid.Should().Be(PinnedMbid);
        await _catalog.Received(1).SetMusicBrainzIdentity(Alex,
            Arg.Is<MusicBrainzIdentity>(i => i.Mbid == PinnedMbid), true);
    }

    [Fact]
    public async Task SetOverride_returns_null_when_the_mbid_has_no_artist()
    {
        _musicBrainz.GetArtist("nope").Returns((MusicBrainzArtist?)null);

        var result = await _sut.SetOverride("ALEX", "nope");

        result.Should().BeNull();
        await _catalog.DidNotReceive().SetMusicBrainzIdentity(
            Arg.Any<ArtistKey>(), Arg.Any<MusicBrainzIdentity>(), true);
    }
}
