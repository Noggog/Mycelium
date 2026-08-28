using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;
using Mycelium.Plex.Services.Smart;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The survey — "which of these does the user already have?" — and the two writes.
///
/// <para>The behaviour worth pinning down is that recognition goes by <em>rules</em>: a playlist the
/// user built by hand and named something unrelated must count as a match, while one that merely shares
/// a name must not. Getting that backwards means either handing someone a duplicate of a playlist they
/// already curate, or quietly claiming credit for one that selects something else.</para>
/// </summary>
public class SmartPlaylistServiceTests
{
    private const string Subject = "user-sub";
    private const string Username = "noggog";
    private const string LikedTagId = "749936";
    private const int Section = 1;

    private readonly IPlexLinkRepo _links = Substitute.For<IPlexLinkRepo>();
    private readonly IPlexApi _plexApi = Substitute.For<IPlexApi>();
    private readonly IUserRepo _users = Substitute.For<IUserRepo>();
    private readonly FakePlaylistApi _playlists = new();
    private readonly SmartPlaylistService _sut;

    public SmartPlaylistServiceTests()
    {
        _sut = new SmartPlaylistService(
            _links, _playlists, _plexApi, _users, NullLogger<SmartPlaylistService>.Instance);

        _plexApi.ResolveLibrary().Returns(new PlexLibrary { Key = Section, Title = "Music", Type = "artist" });
        Linked();
        // The user has thumbed at least one artist, so their liked mood tag exists on the server.
        _playlists.Tags[("mood", PlexSmartFilter.ArtistType)] = new List<PlexTagEntry>
        {
            new(LikedTagId, $"{Username}_liked"),
            new("749937", $"{Username}_disliked"),
            new("2779", "ambient"),
        };
    }

    /// <summary>Stores the user's answer about how they rate; absent means they never answered.</summary>
    private void RatesInHalfStars(bool? halfStars) =>
        _users.Get(Subject).Returns(new AppUser(
            Subject, Username, null, Username, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            HalfStarRatings: halfStars));

    private void Linked() =>
        _links.Get(Subject).Returns(new PlexLink(
            Subject, "acct-1", "Noggog", "n@example.com", "user-token", DateTimeOffset.UnixEpoch));

    private Task<PlaylistSurvey> Survey(int freshMonths = 3) => _sut.Survey(Subject, Username, freshMonths);

    private static async Task<StockPlaylistStatus> Row(Task<PlaylistSurvey> survey, string id) =>
        (await survey).Playlists.Single(p => p.Id == id);

    /// <summary>The stock definition by id, built the way the service will build it.</summary>
    private static StockPlaylistDefinition Definition(
        string definitionId, int freshMonths = 3, bool halfStars = SmartPlaylistCatalog.DefaultHalfStars) =>
        SmartPlaylistCatalog
            .Build(new StockPlaylistOptions(LikedTagId, FreshMonths: freshMonths, HalfStars: halfStars))
            .Single(d => d.Id == definitionId);

    /// <summary>Puts a playlist on the fake server with the rules the named stock definition generates.</summary>
    private void ServerHas(string title, string definitionId, int freshMonths = 3, int leafCount = 100)
    {
        var filter = Definition(definitionId, freshMonths).Filter!;
        _playlists.Add(title, Section, filter, leafCount);
    }

    // ---- linking gate ------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_linked_plex_account_there_is_nothing_to_survey()
    {
        _links.Get(Subject).Returns((PlexLink?)null);

        var survey = await Survey();

        survey.Linked.Should().BeFalse();
        survey.Playlists.Should().BeEmpty();
    }

    [Fact]
    public async Task Creating_without_a_link_fails_loudly_rather_than_writing_somewhere_else()
    {
        _links.Get(Subject).Returns((PlexLink?)null);

        var act = () => _sut.Create(Subject, Username, SmartPlaylistCatalog.FrontierId, 3);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _playlists.Created.Should().BeEmpty();
    }

    /// <summary>Every read and write must carry the user's token, never the server's.</summary>
    [Fact]
    public async Task All_playlist_calls_act_as_the_linked_user()
    {
        await _sut.Create(Subject, Username, SmartPlaylistCatalog.FrontierId, 3);

        _playlists.TokensSeen.Should().OnlyContain(t => t == "user-token");
    }

    // ---- recognition -------------------------------------------------------------------------

    [Fact]
    public async Task A_definition_the_user_has_not_built_is_offered()
    {
        (await Row(Survey(), "stars-4")).State.Should().Be(StockPlaylistState.NotCreated);
    }

    /// <summary>The whole point: same rules, unrelated name, still a match.</summary>
    [Fact]
    public async Task A_matching_playlist_is_recognised_whatever_it_is_called()
    {
        ServerHas("Driving music", "stars-4", leafCount: 3705);

        var row = await Row(Survey(), "stars-4");

        row.State.Should().Be(StockPlaylistState.Exists);
        row.MatchedTitle.Should().Be("Driving music");
        row.TrackCount.Should().Be(3705);
    }

    /// <summary>
    /// Plex drops nesting that repeats its parent's join when a playlist is re-saved by hand, so the
    /// stored rules can come back flatter than they went out. That must still be the same playlist.
    /// </summary>
    [Fact]
    public async Task A_playlist_plex_has_reflattened_still_matches()
    {
        var fresh = Definition("stars-4-fresh").Filter!;
        var redundantlyNested = fresh with
        {
            Rules = PlexGroup.All(PlexGroup.All(((PlexGroup)fresh.Rules!).Children.ToArray())),
        };
        _playlists.Add("re-saved by hand", Section, redundantlyNested, leafCount: 12);

        (await Row(Survey(), "stars-4-fresh")).State.Should().Be(StockPlaylistState.Exists);
    }

    /// <summary>
    /// Rule order carries no meaning — Plex stores whatever order the editor happened to produce.
    /// </summary>
    [Fact]
    public async Task Rule_order_does_not_affect_recognition()
    {
        var fresh = Definition("stars-4-fresh").Filter!;
        var reversed = fresh with
        {
            Rules = PlexGroup.All(((PlexGroup)fresh.Rules!).Children.Reverse().ToArray()),
        };
        _playlists.Add("backwards", Section, reversed);

        (await Row(Survey(), "stars-4-fresh")).State.Should().Be(StockPlaylistState.Exists);
    }

    /// <summary>A neighbouring tier is a different playlist, however similar it looks.</summary>
    [Fact]
    public async Task A_different_threshold_is_not_a_match()
    {
        ServerHas("Good stuff", "stars-3");

        (await Row(Survey(), "stars-4")).State.Should().Be(StockPlaylistState.NotCreated);
        (await Row(Survey(), "stars-3")).State.Should().Be(StockPlaylistState.Exists);
    }

    /// <summary>Likewise a different freshness window — that's a different set of tracks.</summary>
    [Fact]
    public async Task A_different_freshness_window_is_not_a_match()
    {
        ServerHas("4 star, six months", "stars-4-fresh", freshMonths: 6);

        (await Row(Survey(freshMonths: 3), "stars-4-fresh")).State.Should().Be(StockPlaylistState.NotCreated);
        (await Row(Survey(freshMonths: 6), "stars-4-fresh")).State.Should().Be(StockPlaylistState.Exists);
    }

    /// <summary>
    /// The rating scale is part of what a playlist <em>means</em>. A Frontier built for a half-star
    /// user puts the "never play again" floor at 0.5★; the same playlist read back after that user
    /// says they rate in whole stars selects a different set of tracks, and the page must say so
    /// rather than reporting a match it no longer has.
    /// </summary>
    [Fact]
    public async Task Changing_the_rating_scale_makes_an_existing_frontier_stop_matching()
    {
        RatesInHalfStars(true);
        _playlists.Add(
            "Frontier",
            Section,
            Definition(SmartPlaylistCatalog.FrontierId, halfStars: true).Filter!,
            leafCount: 900);

        (await Row(Survey(), SmartPlaylistCatalog.FrontierId)).State
            .Should().Be(StockPlaylistState.Exists);

        RatesInHalfStars(false);

        // Same name, different rules — which is the "offer to rewrite it" case, not a second copy.
        (await Row(Survey(), SmartPlaylistCatalog.FrontierId)).State
            .Should().Be(StockPlaylistState.Differs);
    }

    /// <summary>
    /// A user who has never answered gets the default scale, not a crash or an empty survey — the
    /// question is new, and every existing account is in exactly this state.
    /// </summary>
    [Fact]
    public async Task A_user_who_has_never_answered_gets_the_default_scale()
    {
        _users.Get(Subject).Returns((AppUser?)null);

        var survey = await Survey();

        survey.HalfStars.Should().Be(SmartPlaylistCatalog.DefaultHalfStars);
        survey.Playlists.Should().Contain(p => p.Id == "stars-4");
    }

    /// <summary>Whole stars means the half tiers aren't on offer at all.</summary>
    [Fact]
    public async Task Whole_star_users_are_not_offered_half_tiers()
    {
        RatesInHalfStars(false);

        var survey = await Survey();

        survey.HalfStars.Should().BeFalse();
        survey.Playlists.Should().Contain(p => p.Id == "stars-4")
            .And.NotContain(p => p.Id == "stars-3_5");
    }

    /// <summary>A playlist in another library section isn't this library's playlist.</summary>
    [Fact]
    public async Task A_matching_playlist_over_a_different_section_is_not_a_match()
    {
        var filter = Definition("stars-4").Filter!;
        _playlists.Add("other library", sectionKey: 4, filter);

        (await Row(Survey(), "stars-4")).State.Should().Be(StockPlaylistState.NotCreated);
    }

    /// <summary>
    /// Sharing a name but not a meaning is the one case name matters — we offer to fix it rather than
    /// creating a second playlist with the same title.
    /// </summary>
    [Fact]
    public async Task A_name_clash_with_different_rules_is_reported_as_differing()
    {
        var somethingElse = new PlexSmartFilter(
            PlexSmartFilter.ArtistType, new PlexCondition("track.viewCount", PlexOp.GreaterThan, "50"));
        _playlists.Add("4★+", Section, somethingElse, leafCount: 7);

        var row = await Row(Survey(), "stars-4");

        row.State.Should().Be(StockPlaylistState.Differs);
        row.MatchedRatingKey.Should().NotBeNull();
        row.Note.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Tag rules store per-server numeric ids, so recognising "My Library" means resolving the id back
    /// to the tag's name. A playlist pointing at a different mood is a different playlist.
    /// </summary>
    [Fact]
    public async Task My_library_matches_on_the_liked_tag_and_not_some_other_mood()
    {
        ServerHas("Bands I like", SmartPlaylistCatalog.MyLibraryId);
        (await Row(Survey(), SmartPlaylistCatalog.MyLibraryId)).State.Should().Be(StockPlaylistState.Exists);

        _playlists.Clear();
        _playlists.Add("Ambient", Section, new PlexSmartFilter(
            PlexSmartFilter.ArtistType, new PlexCondition("artist.mood", PlexOp.Is, "2779")));

        (await Row(Survey(), SmartPlaylistCatalog.MyLibraryId)).State.Should().Be(StockPlaylistState.NotCreated);
    }

    [Fact]
    public async Task My_library_is_unavailable_until_the_user_has_thumbed_someone()
    {
        _playlists.Tags[("mood", PlexSmartFilter.ArtistType)] = new List<PlexTagEntry> { new("2779", "ambient") };

        var row = await Row(Survey(), SmartPlaylistCatalog.MyLibraryId);

        row.State.Should().Be(StockPlaylistState.Unavailable);
        row.Note.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>An unparseable playlist is skipped, not fatal — the rest of the survey still answers.</summary>
    [Fact]
    public async Task A_playlist_whose_rules_cannot_be_read_does_not_break_the_survey()
    {
        _playlists.AddRaw("mystery", "library://x/item/999");
        ServerHas("Driving music", "stars-4");

        (await Row(Survey(), "stars-4")).State.Should().Be(StockPlaylistState.Exists);
    }

    // ---- writes ------------------------------------------------------------------------------

    [Fact]
    public async Task Creating_writes_the_definitions_rules_and_reports_the_result()
    {
        var status = await _sut.Create(Subject, Username, "stars-5", 3);

        status.State.Should().Be(StockPlaylistState.Exists);
        _playlists.Created.Should().ContainSingle().Which.Title.Should().Be("5★+");
        _playlists.Created.Single().Filter.Rules
            .Should().Be(new PlexCondition("track.userRating", PlexOp.GreaterThan, "9"));
    }

    /// <summary>
    /// The guard against duplicates: asking twice, or asking for something the user already built under
    /// another name, must not leave two playlists behind.
    /// </summary>
    [Fact]
    public async Task Creating_something_that_already_exists_is_a_no_op()
    {
        ServerHas("Driving music", "stars-4");

        var status = await _sut.Create(Subject, Username, "stars-4", 3);

        status.State.Should().Be(StockPlaylistState.Exists);
        status.MatchedTitle.Should().Be("Driving music");
        _playlists.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Creating_something_unavailable_does_not_write_a_ruleless_playlist()
    {
        _playlists.Tags[("mood", PlexSmartFilter.ArtistType)] = new List<PlexTagEntry>();

        var status = await _sut.Create(Subject, Username, SmartPlaylistCatalog.MyLibraryId, 3);

        status.State.Should().Be(StockPlaylistState.Unavailable);
        _playlists.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Updating_rewrites_the_clashing_playlist_in_place()
    {
        var somethingElse = new PlexSmartFilter(
            PlexSmartFilter.ArtistType, new PlexCondition("track.viewCount", PlexOp.GreaterThan, "50"));
        var ratingKey = _playlists.Add("4★+", Section, somethingElse);

        var status = await _sut.UpdateRules(Subject, Username, "stars-4", 3);

        status.State.Should().Be(StockPlaylistState.Exists);
        _playlists.Updated.Should().ContainSingle().Which.RatingKey.Should().Be(ratingKey);
        _playlists.Created.Should().BeEmpty();
        // And the survey now recognises it, rather than still offering the update.
        (await Row(Survey(), "stars-4")).State.Should().Be(StockPlaylistState.Exists);
    }

    [Fact]
    public async Task Updating_when_nothing_clashes_writes_nothing()
    {
        var status = await _sut.UpdateRules(Subject, Username, "stars-4", 3);

        status.State.Should().Be(StockPlaylistState.NotCreated);
        _playlists.Updated.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_definition_id_is_rejected()
    {
        var act = () => _sut.Create(Subject, Username, "stars-9", 3);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// A stand-in Plex account holding smart playlists. Stores each one the way Plex does — as an encoded
    /// content URI — so the service exercises the real parser rather than being handed a rule tree.
    /// </summary>
    private sealed class FakePlaylistApi : IPlexPlaylistApi
    {
        private readonly List<PlexPlaylist> _playlists = new();
        private int _nextKey = 1000;

        public Dictionary<(string Field, int Type), List<PlexTagEntry>> Tags { get; } = new();

        public List<(string Title, PlexSmartFilter Filter)> Created { get; } = new();
        public List<(string RatingKey, PlexSmartFilter Filter)> Updated { get; } = new();
        public List<string> TokensSeen { get; } = new();

        public void Clear() => _playlists.Clear();

        public string Add(string title, int sectionKey, PlexSmartFilter filter, int leafCount = 1)
        {
            var key = (_nextKey++).ToString();
            _playlists.Add(new PlexPlaylist(key, title, true, leafCount, Content(sectionKey, filter)));
            return key;
        }

        public void AddRaw(string title, string content) =>
            _playlists.Add(new PlexPlaylist((_nextKey++).ToString(), title, true, 0, content));

        private static string Content(int sectionKey, PlexSmartFilter filter) =>
            "library://x/directory/"
            + Uri.EscapeDataString(
                $"/library/sections/{sectionKey}/all?{PlexFilterSerializer.Serialize(filter)}");

        public Task<PlexPlaylist[]> GetSmartAudioPlaylists(string token)
        {
            TokensSeen.Add(token);
            return Task.FromResult(_playlists.ToArray());
        }

        public Task<PlexPlaylist> CreateSmartPlaylist(
            string token, string title, int sectionKey, PlexSmartFilter filter)
        {
            TokensSeen.Add(token);
            Created.Add((title, filter));
            var key = Add(title, sectionKey, filter, leafCount: 42);
            return Task.FromResult(_playlists.Single(p => p.RatingKey == key));
        }

        public Task<PlexPlaylist> UpdateSmartPlaylistFilter(
            string token, string ratingKey, int sectionKey, PlexSmartFilter filter)
        {
            TokensSeen.Add(token);
            Updated.Add((ratingKey, filter));
            var at = _playlists.FindIndex(p => p.RatingKey == ratingKey);
            _playlists[at] = _playlists[at] with { Content = Content(sectionKey, filter), LeafCount = 42 };
            return Task.FromResult(_playlists[at]);
        }

        public Task<IReadOnlyList<PlexTagEntry>> GetSectionTags(int sectionKey, string field, int type) =>
            Task.FromResult<IReadOnlyList<PlexTagEntry>>(
                Tags.TryGetValue((field, type), out var tags) ? tags : Array.Empty<PlexTagEntry>());
    }
}
