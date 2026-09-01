using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

public class StarHarvesterTests
{
    private readonly IUserRepo _users = Substitute.For<IUserRepo>();
    private readonly IPlexLinkRepo _links = Substitute.For<IPlexLinkRepo>();
    private readonly IPlexApi _plex = Substitute.For<IPlexApi>();
    private readonly IUserTrackRatingRepo _ratings = Substitute.For<IUserTrackRatingRepo>();
    private readonly ILibraryTrackRepo _tracks = Substitute.For<ILibraryTrackRepo>();

    private const int Library = 7;

    private StarHarvester Harvester() => new(
        _users, _links, _plex, _ratings, _tracks, NullLogger<StarHarvester>.Instance);

    public StarHarvesterTests()
    {
        _plex.ResolveLibrary().Returns(new PlexLibrary { Key = Library });
        _ratings.ReplaceForUser(Arg.Any<string>(), Arg.Any<IReadOnlyList<TrackRating>>())
            .Returns(call => ((IReadOnlyList<TrackRating>)call[1]).Count);
    }

    private static AppUser User(string subject) =>
        new(subject, subject, null, subject, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null);

    private static string TokenFor(string subject) => $"{subject}-token";

    private void Linked(params string[] subjects)
    {
        _users.GetAll().Returns(subjects.Select(User).ToArray());
        foreach (var subject in subjects)
        {
            _links.Get(subject).Returns(new PlexLink(
                subject, $"plex-{subject}", subject, null, TokenFor(subject), DateTimeOffset.UnixEpoch));
        }
    }

    private static PlexRatedTrack Track(double rating, string title = "Idioteque") => new()
    {
        Artist = "Radiohead",
        Album = "Kid A",
        Title = title,
        TrackNumber = 8,
        File = $"/music/{title}.flac",
        UserRating = rating,
    };

    [Fact]
    public async Task Each_account_is_read_with_its_own_plex_token()
    {
        // Ratings are per-Plex-account. Reading with the server's token — or with someone else's —
        // would record the wrong person's taste, silently and for everyone.
        Linked("kelsey", "justin");
        _plex.GetRatedTracks(Library, Arg.Any<string>()).Returns([Track(10)]);

        await Harvester().HarvestAll();

        await _plex.Received(1).GetRatedTracks(Library, TokenFor("kelsey"));
        await _plex.Received(1).GetRatedTracks(Library, TokenFor("justin"));
    }

    [Fact]
    public async Task Plex_ten_point_ratings_become_five_point_stars()
    {
        Linked("kelsey");
        _plex.GetRatedTracks(Library, TokenFor("kelsey")).Returns([Track(9)]);

        await Harvester().HarvestAll();

        await _ratings.Received(1).ReplaceForUser(
            "kelsey",
            Arg.Is<IReadOnlyList<TrackRating>>(r => r.Single().Stars == 4.5));
    }

    [Fact]
    public async Task An_account_with_no_plex_link_is_skipped_rather_than_emptied()
    {
        // The distinction the whole feature rests on: "we can't read your ratings" is not "you have
        // none". Wiping the mirror on an unlink would throw away the only copy that survives Plex —
        // which is the one thing this exists to keep.
        _users.GetAll().Returns([User("kelsey")]);
        _links.Get("kelsey").Returns((PlexLink?)null);

        var result = await Harvester().HarvestAll();

        await _ratings.DidNotReceive().ReplaceForUser(Arg.Any<string>(), Arg.Any<IReadOnlyList<TrackRating>>());
        result.Skipped.Should().Be(1);
        result.Users.Should().Be(0);
    }

    [Fact]
    public async Task A_rating_cleared_in_plex_stops_being_stored()
    {
        // The counterpart to the rule above: a *successful* read is authoritative, so an empty result
        // does empty the mirror. That is how un-rating a song propagates.
        Linked("kelsey");
        _plex.GetRatedTracks(Library, TokenFor("kelsey")).Returns([]);

        await Harvester().HarvestAll();

        await _ratings.Received(1).ReplaceForUser(
            "kelsey", Arg.Is<IReadOnlyList<TrackRating>>(r => r.Count == 0));
    }

    [Fact]
    public async Task One_failing_account_does_not_abort_the_pass()
    {
        Linked("broken", "kelsey");
        _plex.GetRatedTracks(Library, TokenFor("broken"))
            .Returns<PlexRatedTrack[]>(_ => throw new HttpRequestException("down"));
        _plex.GetRatedTracks(Library, TokenFor("kelsey")).Returns([Track(10)]);

        var result = await Harvester().HarvestAll();

        await _ratings.Received(1).ReplaceForUser("kelsey", Arg.Any<IReadOnlyList<TrackRating>>());
        result.Users.Should().Be(1);
        result.Skipped.Should().Be(1);
    }

    [Fact]
    public async Task The_library_track_listing_is_refreshed_once_not_once_per_user()
    {
        // Which songs exist is a fact about the library; only the ratings are per person. Reading it
        // per user would multiply the most expensive call in the app by the number of accounts.
        Linked("kelsey", "justin");
        _plex.GetMusicTracks(Library).Returns([
            new PlexLibraryTrack
            {
                Artist = "Radiohead", Album = "Kid A", Title = "Idioteque",
                TrackNumber = 8, File = "/music/kida/08.flac",
            },
        ]);
        _plex.GetRatedTracks(Library, Arg.Any<string>()).Returns([]);

        await Harvester().HarvestAll();

        await _plex.Received(1).GetMusicTracks(Library);
        await _tracks.Received(1).ReplaceAll(Arg.Is<IReadOnlyList<LibraryTrack>>(t =>
            t.Single().Artist == "Radiohead" && t.Single().Title == "Idioteque"
            && t.Single().File == "/music/kida/08.flac"));
    }

    [Fact]
    public async Task A_track_with_no_artist_or_album_is_left_out_of_the_listing()
    {
        // It has nowhere to go in the archive, which files everything under Library/Artist/Album.
        Linked("kelsey");
        _plex.GetMusicTracks(Library).Returns([
            new PlexLibraryTrack { Title = "Orphan", File = "/music/x.flac" },
        ]);
        _plex.GetRatedTracks(Library, Arg.Any<string>()).Returns([]);

        await Harvester().HarvestAll();

        await _tracks.Received(1).ReplaceAll(Arg.Is<IReadOnlyList<LibraryTrack>>(t => t.Count == 0));
    }

    [Fact]
    public async Task A_failed_listing_does_not_cost_us_the_ratings()
    {
        // Ratings are the harder thing to reconstruct, so they must not ride on the listing succeeding.
        Linked("kelsey");
        _plex.GetMusicTracks(Library).Returns<PlexLibraryTrack[]>(_ => throw new HttpRequestException("down"));
        _plex.GetRatedTracks(Library, TokenFor("kelsey")).Returns([Track(10)]);

        var result = await Harvester().HarvestAll();

        await _ratings.Received(1).ReplaceForUser("kelsey", Arg.Any<IReadOnlyList<TrackRating>>());
        result.Users.Should().Be(1);
    }

    [Fact]
    public async Task An_unreachable_server_ends_the_pass_without_touching_anything()
    {
        // Nothing stored is better than everything emptied: the next interval retries.
        _users.GetAll().Returns([User("kelsey")]);
        _plex.ResolveLibrary().Returns<PlexLibrary>(_ => throw new HttpRequestException("down"));

        var result = await Harvester().HarvestAll();

        await _ratings.DidNotReceive().ReplaceForUser(Arg.Any<string>(), Arg.Any<IReadOnlyList<TrackRating>>());
        result.Should().Be(new StarHarvestResult(0, 0, 0));
    }

    [Fact]
    public async Task The_track_identity_that_outlives_plex_is_carried_through()
    {
        Linked("kelsey");
        _plex.GetRatedTracks(Library, TokenFor("kelsey")).Returns([Track(8)]);

        await Harvester().HarvestAll();

        await _ratings.Received(1).ReplaceForUser("kelsey", Arg.Is<IReadOnlyList<TrackRating>>(r =>
            r.Single().Artist == "Radiohead"
            && r.Single().Album == "Kid A"
            && r.Single().Title == "Idioteque"
            && r.Single().TrackNumber == 8
            && r.Single().File == "/music/Idioteque.flac"));
    }
}
