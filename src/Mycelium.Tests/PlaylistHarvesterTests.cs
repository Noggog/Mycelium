using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

public class PlaylistHarvesterTests
{
    private readonly IUserRepo _users = Substitute.For<IUserRepo>();
    private readonly IPlexLinkRepo _links = Substitute.For<IPlexLinkRepo>();
    private readonly IPlexPlaylistApi _plex = Substitute.For<IPlexPlaylistApi>();
    private readonly IUserPlaylistRepo _store = Substitute.For<IUserPlaylistRepo>();

    private PlaylistHarvester Harvester() => new(
        _users, _links, _plex, _store, NullLogger<PlaylistHarvester>.Instance);

    public PlaylistHarvesterTests()
    {
        _store.ReplaceForUser(Arg.Any<string>(), Arg.Any<IReadOnlyList<UserPlaylist>>())
            .Returns(call => ((IReadOnlyList<UserPlaylist>)call[1]).Count);
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

    private static PlexPlaylist Manual(string title, string key = "1") =>
        new(key, title, Smart: false, LeafCount: 2, Content: null);

    private static PlexPlaylist Smart(string title, string rules, string key = "2") =>
        new(key, title, Smart: true, LeafCount: 40, Content: rules);

    [Fact]
    public async Task A_hand_built_playlist_is_stored_with_its_ordered_tracks()
    {
        Linked("kelsey");
        _plex.GetAudioPlaylists(TokenFor("kelsey")).Returns([Manual("Driving")]);
        _plex.GetPlaylistItems(TokenFor("kelsey"), "1").Returns(
        [
            new PlexPlaylistItem(1, "Radiohead", "Kid A", "Idioteque", "/music/a.flac"),
            new PlexPlaylistItem(2, "Portishead", "Dummy", "Roads", "/music/b.flac"),
        ]);

        await Harvester().HarvestAll();

        await _store.Received(1).ReplaceForUser("kelsey", Arg.Is<IReadOnlyList<UserPlaylist>>(p =>
            p.Single().Title == "Driving"
            && !p.Single().Smart
            && p.Single().Tracks.Count == 2
            && p.Single().Tracks[0].Position == 1
            && p.Single().Tracks[0].Title == "Idioteque"
            && p.Single().Tracks[1].Position == 2));
    }

    [Fact]
    public async Task A_smart_playlist_stores_its_rules_and_never_reads_its_membership()
    {
        // The rules are the durable thing. Reading the members would cost a request per playlist to
        // archive an answer that goes stale the next time the library changes.
        Linked("kelsey");
        _plex.GetAudioPlaylists(TokenFor("kelsey")).Returns([Smart("4 stars up", "track.userRating>>7")]);

        await Harvester().HarvestAll();

        await _plex.DidNotReceive().GetPlaylistItems(Arg.Any<string>(), Arg.Any<string>());
        await _store.Received(1).ReplaceForUser("kelsey", Arg.Is<IReadOnlyList<UserPlaylist>>(p =>
            p.Single().Smart
            && p.Single().Rules == "track.userRating>>7"
            && p.Single().Tracks.Count == 0));
    }

    [Fact]
    public async Task Both_kinds_survive_the_same_pass()
    {
        Linked("kelsey");
        _plex.GetAudioPlaylists(TokenFor("kelsey")).Returns(
            [Manual("Driving"), Smart("4 stars up", "track.userRating>>7")]);
        _plex.GetPlaylistItems(TokenFor("kelsey"), "1").Returns(
            [new PlexPlaylistItem(1, "A", "B", "C", "/music/a.flac")]);

        var result = await Harvester().HarvestAll();

        result.Playlists.Should().Be(2);
    }

    [Fact]
    public async Task Each_account_is_read_with_its_own_token()
    {
        // Playlists live in the user's own Plex account, so the server token would read the owner's.
        Linked("kelsey", "justin");
        _plex.GetAudioPlaylists(Arg.Any<string>()).Returns([]);

        await Harvester().HarvestAll();

        await _plex.Received(1).GetAudioPlaylists(TokenFor("kelsey"));
        await _plex.Received(1).GetAudioPlaylists(TokenFor("justin"));
    }

    [Fact]
    public async Task An_account_with_no_plex_link_is_skipped_rather_than_emptied()
    {
        // Same rule as the star harvest: unlinking is not deleting, and the mirror may be the only copy
        // that outlives Plex.
        _users.GetAll().Returns([User("kelsey")]);
        _links.Get("kelsey").Returns((PlexLink?)null);

        var result = await Harvester().HarvestAll();

        await _store.DidNotReceive().ReplaceForUser(Arg.Any<string>(), Arg.Any<IReadOnlyList<UserPlaylist>>());
        result.Skipped.Should().Be(1);
    }

    [Fact]
    public async Task One_failing_account_does_not_abort_the_pass()
    {
        Linked("broken", "kelsey");
        _plex.GetAudioPlaylists(TokenFor("broken"))
            .Returns<PlexPlaylist[]>(_ => throw new HttpRequestException("down"));
        _plex.GetAudioPlaylists(TokenFor("kelsey")).Returns([Smart("ok", "rules")]);

        var result = await Harvester().HarvestAll();

        await _store.Received(1).ReplaceForUser("kelsey", Arg.Any<IReadOnlyList<UserPlaylist>>());
        result.Users.Should().Be(1);
        result.Skipped.Should().Be(1);
    }
}
