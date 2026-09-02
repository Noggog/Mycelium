using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The after-the-fact tagging path: an artist liked while it was outside the library gets its verdict
/// mood the moment a catalog sync reports it present.
/// </summary>
public class ArtistTagBackfillTests
{
    private const string User = "user-1";

    private readonly IArtistTagger _tagger = Substitute.For<IArtistTagger>();
    private readonly IUserQueueRepo _queue = Substitute.For<IUserQueueRepo>();
    private readonly IUserRepo _users = Substitute.For<IUserRepo>();
    private readonly ArtistTagBackfill _sut;

    public ArtistTagBackfillTests()
    {
        _sut = new ArtistTagBackfill(_tagger, _queue, _users, NullLogger<ArtistTagBackfill>.Instance);
        _queue.GetAllUserIds().Returns(new[] { User });
        _users.Get(User).Returns(new AppUser(
            User, "noggog", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        _queue.GetRated(User).Returns(Array.Empty<ArtistRating>());
    }

    private void Rated(params (string Artist, DiscoveryStatus Status)[] ratings) =>
        _queue.GetRated(User).Returns(ratings
            .Select(r => new ArtistRating(new ArtistKey(r.Artist), null, r.Status))
            .ToArray());

    /// <summary>
    /// Asserts the exact strip set, not just that the right tag went on. A backfill that added the
    /// right verdict but left another one standing is the failure that shows up months later as a smart
    /// playlist matching music the user has moved on from — so the set is compared whole, and
    /// order-insensitively, since nothing depends on the order it's built in.
    /// </summary>
    private Task ReceivedTag(string artist, string add, params string[] remove) =>
        _tagger.Received(1).SetTags(artist, add,
            Arg.Is<IReadOnlyCollection<string>>(r => r.OrderBy(x => x).SequenceEqual(remove.OrderBy(x => x))));

    [Fact]
    public async Task Stamps_a_like_made_before_the_artist_existed_in_plex()
    {
        Rated(("Big Thief", DiscoveryStatus.Liked));

        var applied = await _sut.Backfill(new[] { "Big Thief" });

        applied.Should().Be(1);
        await ReceivedTag("Big Thief", "noggog_liked", "noggog_disliked", "noggog_indifferent");
    }

    [Fact]
    public async Task Stamps_a_dislike_the_same_way()
    {
        Rated(("Big Thief", DiscoveryStatus.Disliked));

        await _sut.Backfill(new[] { "Big Thief" });

        await ReceivedTag("Big Thief", "noggog_disliked", "noggog_liked", "noggog_indifferent");
    }

    [Fact]
    public async Task Ignores_ratings_for_artists_that_did_not_arrive_in_this_sync()
    {
        // The whole point of keying off arrivals: a sync that re-listed 1800 unchanged artists must not
        // re-issue 1800 Plex reads.
        Rated(("Big Thief", DiscoveryStatus.Liked), ("Radiohead", DiscoveryStatus.Liked));

        var applied = await _sut.Backfill(new[] { "Radiohead" });

        applied.Should().Be(1);
        await _tagger.DidNotReceive().SetTags("Big Thief", Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task Does_nothing_at_all_when_nothing_arrived()
    {
        Rated(("Big Thief", DiscoveryStatus.Liked));

        var applied = await _sut.Backfill(Array.Empty<string>());

        applied.Should().Be(0);
        await _queue.DidNotReceive().GetAllUserIds();   // not even a Mongo read on the common sync
    }

    [Fact]
    public async Task Skips_a_snoozed_row()
    {
        // GetRated returns everything not pending, so snoozes ride along — a deferred decision, which
        // must not be stamped as a dislike.
        Rated(("Big Thief", DiscoveryStatus.Snoozed));

        var applied = await _sut.Backfill(new[] { "Big Thief" });

        applied.Should().Be(0);
        await _tagger.DidNotReceive().SetTags(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task Skips_a_user_with_no_usable_username_to_prefix_the_tag_with()
    {
        _users.Get(User).Returns(new AppUser(
            User, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        Rated(("Big Thief", DiscoveryStatus.Liked));

        var applied = await _sut.Backfill(new[] { "Big Thief" });

        applied.Should().Be(0);
        await _tagger.DidNotReceive().SetTags(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task Matches_a_rating_against_each_name_in_a_collaborator_joined_arrival()
    {
        // Plex files the album under "Nina Simone;Hot Chip"; the like was placed on "Hot Chip".
        Rated(("Hot Chip", DiscoveryStatus.Liked));

        var applied = await _sut.Backfill(new[] { "Nina Simone;Hot Chip" });

        applied.Should().Be(1);
        await ReceivedTag("Hot Chip", "noggog_liked", "noggog_disliked", "noggog_indifferent");
    }

    [Fact]
    public async Task Matches_the_arrival_name_case_insensitively()
    {
        Rated(("big thief", DiscoveryStatus.Liked));

        (await _sut.Backfill(new[] { "Big Thief" })).Should().Be(1);
    }

    [Fact]
    public async Task Stamps_every_users_own_verdict_on_a_shared_arrival()
    {
        const string other = "user-2";
        _queue.GetAllUserIds().Returns(new[] { User, other });
        _users.Get(other).Returns(new AppUser(
            other, "kate", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        Rated(("Big Thief", DiscoveryStatus.Liked));
        _queue.GetRated(other).Returns(new[]
        {
            new ArtistRating(new ArtistKey("Big Thief"), null, DiscoveryStatus.Disliked),
        });

        var applied = await _sut.Backfill(new[] { "Big Thief" });

        applied.Should().Be(2);
        await ReceivedTag("Big Thief", "noggog_liked", "noggog_disliked", "noggog_indifferent");
        await ReceivedTag("Big Thief", "kate_disliked", "kate_liked", "kate_indifferent");
    }
}
