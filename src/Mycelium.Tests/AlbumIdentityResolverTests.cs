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
/// The album-identity backfill: turning owned album titles into MusicBrainz release-group MBIDs.
///
/// <para>The behaviour worth pinning down is all about <em>convergence</em>. Every lookup costs a
/// rate-limited second, so a pass that re-asks the same questions never finishes — but a pass that
/// writes down the wrong answer, or writes down "no" when the network merely hiccuped, is worse than
/// one that is slow.</para>
/// </summary>
public class AlbumIdentityResolverTests
{
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly IMusicBrainzApi _musicBrainz = Substitute.For<IMusicBrainzApi>();

    private AlbumIdentityResolver Resolver() =>
        new(_catalog, _musicBrainz, NullLogger<AlbumIdentityResolver>.Instance);

    private const string ArtistMbid = "a74b1b7f-71a5-4011-9441-d0b5e4122711";

    private void Gaps(params string[] albums) =>
        _catalog.GetAlbumsWithoutReleaseGroup(Arg.Any<int>())
            .Returns([new AlbumIdentityGap("Radiohead", ArtistMbid, albums)]);

    private static MusicBrainzReleaseGroup Group(string id, string title) =>
        new() { Id = id, Title = title };

    [Fact]
    public async Task A_resolved_album_is_stored_against_its_release_group()
    {
        Gaps("Kid A");
        _musicBrainz.SearchReleaseGroup(ArtistMbid, "Kid A").Returns(Group("rg-kid-a", "Kid A"));

        var result = await Resolver().ResolveSome(10);

        result.Resolved.Should().Be(1);
        await _catalog.Received(1).SetAlbumReleaseGroup("Radiohead", "Kid A", "rg-kid-a");
    }

    [Fact]
    public async Task A_record_MusicBrainz_does_not_have_is_written_down_as_a_miss()
    {
        // Bootlegs, DJ mixes and a library's own compilations genuinely aren't in MusicBrainz. Left
        // unrecorded they would be re-asked every pass for ever, and the albums queued behind them
        // would never come up — the backfill would run daily and converge on nothing.
        Gaps("A Mix I Made");
        _musicBrainz.SearchReleaseGroup(ArtistMbid, "A Mix I Made").Returns((MusicBrainzReleaseGroup?)null);

        var result = await Resolver().ResolveSome(10);

        result.Missed.Should().Be(1);
        await _catalog.Received(1).SetAlbumReleaseGroup("Radiohead", "A Mix I Made", null);
    }

    [Fact]
    public async Task A_lookup_that_threw_is_left_as_a_gap_rather_than_recorded_as_a_miss()
    {
        // A transport failure says nothing about whether the record exists. Writing a miss on the
        // strength of a network blip would retire the album from the backfill permanently.
        Gaps("Amnesiac");
        _musicBrainz.SearchReleaseGroup(ArtistMbid, "Amnesiac")
            .Returns<MusicBrainzReleaseGroup?>(_ => throw new HttpRequestException("upstream down"));

        var result = await Resolver().ResolveSome(10);

        result.Attempted.Should().Be(0);
        await _catalog.DidNotReceive().SetAlbumReleaseGroup(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task One_bad_album_does_not_cost_the_rest_of_the_pass()
    {
        Gaps("Amnesiac", "Kid A");
        _musicBrainz.SearchReleaseGroup(ArtistMbid, "Amnesiac")
            .Returns<MusicBrainzReleaseGroup?>(_ => throw new HttpRequestException("upstream down"));
        _musicBrainz.SearchReleaseGroup(ArtistMbid, "Kid A").Returns(Group("rg-kid-a", "Kid A"));

        var result = await Resolver().ResolveSome(10);

        result.Resolved.Should().Be(1);
    }

    [Fact]
    public async Task A_catalog_that_cannot_be_read_ends_the_pass_quietly()
    {
        // Nothing waits on this backfill, so a bad pass is a lost day rather than an outage — and an
        // exception escaping here would take the host down with it.
        _catalog.GetAlbumsWithoutReleaseGroup(Arg.Any<int>())
            .Returns<AlbumIdentityGap[]>(_ => throw new InvalidOperationException("mongo is away"));

        var result = await Resolver().ResolveSome(10);

        result.Attempted.Should().Be(0);
    }

    [Fact]
    public async Task A_cancelled_pass_keeps_what_it_already_answered()
    {
        // Shutting down mid-slice must not discard the lookups already paid for, and must not wait
        // out however many rate-limited seconds are left in the batch.
        using var cancellation = new CancellationTokenSource();
        Gaps("Kid A", "Amnesiac", "In Rainbows");
        _musicBrainz.SearchReleaseGroup(ArtistMbid, "Kid A").Returns(Group("rg-kid-a", "Kid A"));
        _musicBrainz.SearchReleaseGroup(ArtistMbid, "Amnesiac").Returns(_ =>
        {
            cancellation.Cancel();
            return Group("rg-amnesiac", "Amnesiac");
        });

        var result = await Resolver().ResolveSome(10, cancellation.Token);

        result.Resolved.Should().Be(2);
        await _catalog.DidNotReceive().SetAlbumReleaseGroup("Radiohead", "In Rainbows", Arg.Any<string?>());
    }

    [Fact]
    public async Task A_batch_of_nothing_asks_MusicBrainz_nothing()
    {
        // The steady state once the library is covered: one catalog read and no outbound traffic.
        await Resolver().ResolveSome(0);

        await _catalog.DidNotReceive().GetAlbumsWithoutReleaseGroup(Arg.Any<int>());
        await _musicBrainz.DidNotReceive().SearchReleaseGroup(Arg.Any<string>(), Arg.Any<string>());
    }
}

/// <summary>
/// Choosing which MusicBrainz hit is actually the album asked for.
///
/// <para>Its own class because it is the one place a <em>wrong</em> id could enter the archive, and a
/// wrong MBID is worse than a missing one: it is invisible, permanent, and would send a future
/// migration to the wrong record.</para>
/// </summary>
public class MusicBrainzReleaseGroupPickTests
{
    private static MusicBrainzReleaseGroup Group(string? title, string? id = "rg-1", int score = 100) =>
        new() { Id = id, Title = title, Score = score };

    [Fact]
    public void The_exact_title_wins_even_when_another_hit_scores_higher()
    {
        // Exactly the trap: searching an act's discography for "Kid A" also surfaces their other
        // records, and MusicBrainz may well score one of them above it.
        var picked = MusicBrainzApi.Pick(
            [Group("OK Computer", "rg-ok", 100), Group("Kid A", "rg-kid", 42)], "Kid A");

        picked!.Id.Should().Be("rg-kid");
    }

    [Fact]
    public void Case_and_surrounding_space_do_not_count_as_a_difference()
    {
        MusicBrainzApi.Pick([Group("  kid a ")], "Kid A")!.Id.Should().Be("rg-1");
    }

    [Fact]
    public void A_near_miss_is_a_miss()
    {
        // "Kid A Mnesia" is a different record. Storing its id under "Kid A" would be a silent lie.
        MusicBrainzApi.Pick([Group("Kid A Mnesia")], "Kid A").Should().BeNull();
        MusicBrainzApi.Pick([Group("Kid")], "Kid A").Should().BeNull();
    }

    [Fact]
    public void A_hit_with_no_id_is_no_use()
    {
        MusicBrainzApi.Pick([Group("Kid A", id: null)], "Kid A").Should().BeNull();
    }

    [Fact]
    public void Nothing_at_all_is_a_miss_rather_than_a_throw()
    {
        MusicBrainzApi.Pick(null, "Kid A").Should().BeNull();
        MusicBrainzApi.Pick([], "Kid A").Should().BeNull();
    }
}
