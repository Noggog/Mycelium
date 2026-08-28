using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The "your likes point here" marker: owned artists the frontier vouches for carry
/// "&lt;user&gt;_recommended" in Plex, and stop carrying it the moment they leave that set.
/// </summary>
public class RecommendedArtistTaggerTests
{
    private const string User = "user-1";
    private const int LibraryKey = 3;

    private readonly IRecommendedLibraryArtists _recommendations = Substitute.For<IRecommendedLibraryArtists>();
    private readonly IUserQueueRepo _queue = Substitute.For<IUserQueueRepo>();
    private readonly IUserRepo _users = Substitute.For<IUserRepo>();
    private readonly IPlexApi _plex = Substitute.For<IPlexApi>();
    private readonly RecommendedArtistTagger _sut;

    public RecommendedArtistTaggerTests()
    {
        _sut = new RecommendedArtistTagger(
            _recommendations, _queue, _users, _plex, NullLogger<RecommendedArtistTagger>.Instance);

        _queue.GetAllUserIds().Returns(new[] { User });
        _users.Get(User).Returns(new AppUser(
            User, "noggog", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        _recommendations.RecommendedLibraryArtistNames(User).Returns(Array.Empty<string>());
        _plex.ResolveLibrary().Returns(new PlexLibrary { Key = LibraryKey, Title = "Music", Type = "artist" });
        _plex.GetMusicArtists(LibraryKey).Returns(Array.Empty<PlexMusicArtist>());
    }

    private void Recommends(params string[] names) =>
        _recommendations.RecommendedLibraryArtistNames(User).Returns(names);

    private void Library(params (int Key, string Title, string[] Moods)[] artists) =>
        _plex.GetMusicArtists(LibraryKey).Returns(artists
            .Select(a => new PlexMusicArtist
            {
                RatingKey = a.Key,
                Title = a.Title,
                Mood = a.Moods.Select(m => new PlexTag { Tag = m }).ToArray(),
            })
            .ToArray());

    private Task Wrote(int ratingKey, string[] add, string[] remove) =>
        _plex.Received(1).SetArtistMoods(
            LibraryKey, ratingKey,
            Arg.Is<IReadOnlyCollection<string>>(a => a.SequenceEqual(add)),
            Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(remove)));

    [Fact]
    public async Task Marks_an_owned_artist_the_frontier_recommends()
    {
        Recommends("Big Thief");
        Library((10, "Big Thief", Array.Empty<string>()));

        var result = await _sut.Sync();

        result.Added.Should().Be(1);
        await Wrote(10, new[] { "noggog_recommended" }, Array.Empty<string>());
    }

    [Fact]
    public async Task Leaves_an_artist_that_already_carries_the_marker_alone()
    {
        // The steady state after the first pass: nothing to write, so nothing is written.
        Recommends("Big Thief");
        Library((10, "Big Thief", new[] { "noggog_recommended" }));

        var result = await _sut.Sync();

        result.Should().Be(default(RecommendedArtistTagger.SyncResult));
        await _plex.DidNotReceive().SetArtistMoods(
            Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task Takes_the_marker_off_an_artist_that_has_left_the_recommended_set()
    {
        // What makes the sweep a reconcile: thumbing an artist (or un-liking whatever pointed at it)
        // drops it out of the section, and nothing else would ever clear the tag.
        Recommends();
        Library((10, "Big Thief", new[] { "noggog_recommended" }));

        var result = await _sut.Sync();

        result.Removed.Should().Be(1);
        await Wrote(10, Array.Empty<string>(), new[] { "noggog_recommended" });
    }

    [Fact]
    public async Task Preserves_descriptor_and_verdict_moods_on_the_same_field()
    {
        // Moods are shared with hand-applied tags and the like/dislike verdicts. A delta write is the
        // only thing standing between this sweep and wiping someone's smart collections.
        Recommends("Big Thief");
        Library((10, "Big Thief", new[] { "ambient", "kate_liked" }));

        await _sut.Sync();

        await Wrote(10, new[] { "noggog_recommended" }, Array.Empty<string>());
    }

    [Fact]
    public async Task Never_strips_another_users_marker()
    {
        // "kate" has no queue in this pass, so we know nothing about what she should be recommended —
        // and a run that knows nothing must not delete on that basis.
        Recommends();
        Library((10, "Big Thief", new[] { "kate_recommended" }));

        var result = await _sut.Sync();

        result.Removed.Should().Be(0);
        await _plex.DidNotReceive().SetArtistMoods(
            Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task Matches_a_name_inside_a_collaborator_joined_plex_title()
    {
        // Plex files "Nina Simone;Hot Chip" as one item; the app rates against the constituent names.
        Recommends("Hot Chip");
        Library((10, "Nina Simone;Hot Chip", Array.Empty<string>()));

        await _sut.Sync();

        await Wrote(10, new[] { "noggog_recommended" }, Array.Empty<string>());
    }

    [Fact]
    public async Task Reconciles_each_user_independently_in_one_edit_per_artist()
    {
        const string Kate = "user-2";
        _queue.GetAllUserIds().Returns(new[] { User, Kate });
        _users.Get(Kate).Returns(new AppUser(
            Kate, "kate", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        Recommends("Big Thief");
        _recommendations.RecommendedLibraryArtistNames(Kate).Returns(Array.Empty<string>());
        Library((10, "Big Thief", new[] { "kate_recommended" }));

        var result = await _sut.Sync();

        result.Should().Be(new RecommendedArtistTagger.SyncResult(Added: 1, Removed: 1));
        await Wrote(10, new[] { "noggog_recommended" }, new[] { "kate_recommended" });
    }

    [Fact]
    public async Task Does_not_touch_plex_when_no_user_has_a_usable_username()
    {
        _users.Get(User).Returns((AppUser?)null);

        await _sut.Sync();

        await _plex.DidNotReceive().ResolveLibrary();
        await _plex.DidNotReceive().GetMusicArtists(Arg.Any<int>());
    }

    [Fact]
    public async Task One_artists_failed_write_does_not_stop_the_rest()
    {
        Recommends("Big Thief", "Snail Mail");
        Library(
            (10, "Big Thief", Array.Empty<string>()),
            (11, "Snail Mail", Array.Empty<string>()));
        _plex.SetArtistMoods(
                LibraryKey, 10,
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(Task.FromException(new HttpRequestException("boom")));

        var result = await _sut.Sync();

        result.Added.Should().Be(1); // only the one that landed is counted
        await Wrote(11, new[] { "noggog_recommended" }, Array.Empty<string>());
    }
}
