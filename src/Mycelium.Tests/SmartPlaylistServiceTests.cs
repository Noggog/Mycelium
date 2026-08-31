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
    private const string DislikedTagId = "749937";
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
        // The user has thumbed at least one artist each way, so both verdict moods exist on the
        // server — the liked one is what My Library and Frontier hang off, and the disliked one is
        // what Deep Frontier subtracts (and refuses to be built without).
        _playlists.Tags[("mood", PlexSmartFilter.ArtistType)] = new List<PlexTagEntry>
        {
            new(LikedTagId, $"{Username}_liked"),
            new(DislikedTagId, $"{Username}_disliked"),
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
            .Build(new StockPlaylistOptions(
                LikedTagId,
                DislikedArtistMoodTagId: DislikedTagId,
                FreshMonths: freshMonths,
                HalfStars: halfStars))
            .Single(d => d.Id == definitionId);

    /// <summary>Puts a playlist on the fake server with the rules the named stock definition generates.</summary>
    /// <summary>Puts a playlist on the fake server and hands back its rating key.</summary>
    private string ServerHas(string title, string definitionId, int freshMonths = 3, int leafCount = 100)
    {
        var filter = Definition(definitionId, freshMonths).Filter!;
        return _playlists.Add(title, Section, filter, leafCount);
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

    /// <summary>
    /// The same gate on the row where a missing tag would otherwise be invisible: without the reject
    /// mood Deep Frontier can't write its exclusion, and a Deep Frontier that quietly resurfaced
    /// rejected music would look entirely correct. Withheld, with a note naming the tag it wants — the
    /// page is the only place a failed MoodTagSeeder ever shows up.
    /// </summary>
    [Fact]
    public async Task Deep_frontier_is_unavailable_until_the_reject_tag_exists()
    {
        _playlists.Tags[("mood", PlexSmartFilter.ArtistType)] = new List<PlexTagEntry>
        {
            new(LikedTagId, $"{Username}_liked"),
        };

        var row = await Row(Survey(), SmartPlaylistCatalog.DeepFrontierId);

        row.State.Should().Be(StockPlaylistState.Unavailable);
        row.Note.Should().Contain("disliked");

        // ...and it comes straight back once something carries the tag.
        _playlists.Tags[("mood", PlexSmartFilter.ArtistType)]
            .Add(new PlexTagEntry(DislikedTagId, $"{Username}_disliked"));

        (await Row(Survey(), SmartPlaylistCatalog.DeepFrontierId))
            .State.Should().Be(StockPlaylistState.NotCreated);
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
        // Wrapped, because this is the name Plex is being handed — see PlaylistName.
        _playlists.Created.Should().ContainSingle().Which.Title.Should().Be(@"// 5★+ \\");
        _playlists.Created.Single().Filter.Rules
            .Should().Be(new PlexCondition("track.userRating", PlexOp.GreaterThan, "9"));
        // ...and the row reports the bare name back, so nothing downstream sees the wrapper.
        status.MatchedTitle.Should().Be("5★+");
        status.Title.Should().Be("5★+");
    }

    /// <summary>
    /// The wrapper marks a playlist as one of ours in a Plex sidebar full of hand-made ones, but it
    /// must not turn one name into two: a playlist holding the bare name and one holding the wrapped
    /// name are both "the name is taken", and neither may be reported as free.
    /// </summary>
    [Theory]
    [InlineData("4★+")]
    [InlineData(@"// 4★+ \\")]
    public async Task A_name_clash_is_seen_through_the_wrapper(string existingTitle)
    {
        var somethingElse = new PlexSmartFilter(
            PlexSmartFilter.ArtistType, new PlexCondition("track.viewCount", PlexOp.GreaterThan, "50"));
        _playlists.Add(existingTitle, Section, somethingElse, leafCount: 7);

        var row = await Row(Survey(), "stars-4");

        row.State.Should().Be(StockPlaylistState.Differs);
        // Reported bare either way — the page shows the app's spelling of the name, not Plex's.
        row.MatchedTitle.Should().Be("4★+");
    }

    /// <summary>
    /// A playlist the user renamed themselves keeps its own name in the survey. The unwrapping is a
    /// strip of our own decoration, not a rewrite of whatever the user typed.
    /// </summary>
    [Fact]
    public async Task An_unwrapped_name_of_the_users_own_is_reported_untouched()
    {
        ServerHas("// Driving music", "stars-4");

        (await Row(Survey(), "stars-4")).MatchedTitle.Should().Be("// Driving music");
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

    // ---- cover art ---------------------------------------------------------------------------

    /// <summary>
    /// Art belongs to a named starter, not to a rating. The 4★ starter and the 4★ tier select nearly
    /// the same music, so they are the pair that would go wrong if the cover were keyed on the tier —
    /// the picker would hand out a row wearing another playlist's identity.
    /// </summary>
    [Theory]
    [InlineData(SmartPlaylistCatalog.DeepFrontierId, "/api/playlists/art/deep-frontier")]
    [InlineData("stars-3-fresh-1mo", "/api/playlists/art/ridge")]
    [InlineData("stars-4-fresh-1mo", "/api/playlists/art/poolroom")]
    [InlineData("stars-5-fresh-1mo", "/api/playlists/art/prism")]
    [InlineData(SmartPlaylistCatalog.FrontierId, "/api/playlists/art/frontier")]
    [InlineData(SmartPlaylistCatalog.MyLibraryId, "/api/playlists/art/my-library")]
    [InlineData("stars-4", null)]
    [InlineData("stars-4-fresh", null)]
    public async Task Only_a_starter_with_a_cover_advertises_one(string id, string? artUrl)
    {
        (await Row(Survey(), id)).ArtUrl.Should().Be(artUrl);
    }

    /// <summary>
    /// A half-star rater is offered 3★/3.5★/4★ instead, wearing the same three covers in the same
    /// order — the cover follows the row, not the rating, which is the whole reason the ids name a
    /// picture rather than a star count.
    /// </summary>
    [Theory]
    [InlineData("stars-3-fresh-1mo", "/api/playlists/art/ridge")]
    [InlineData("stars-3_5-fresh-1mo", "/api/playlists/art/poolroom")]
    [InlineData("stars-4-fresh-1mo", "/api/playlists/art/prism")]
    public async Task A_half_star_rater_gets_the_same_covers_on_their_own_tiers(string id, string artUrl)
    {
        RatesInHalfStars(true);

        (await Row(Survey(), id)).ArtUrl.Should().Be(artUrl);
    }

    /// <summary>Whichever scale, the starters are three rows and no cover is used twice.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_starter_covers_are_never_shared_between_rows(bool halfStars)
    {
        RatesInHalfStars(halfStars);

        var art = (await Survey()).Playlists
            .Where(p => p.Id.EndsWith("-fresh-1mo"))
            .Select(p => p.ArtUrl)
            .ToArray();

        art.Should().HaveCount(3).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Creating_a_starter_gives_the_new_playlist_its_cover()
    {
        var created = await _sut.Create(Subject, Username, SmartPlaylistCatalog.DeepFrontierId, 3);

        var poster = _playlists.Posters.Should().ContainSingle().Subject;
        poster.RatingKey.Should().Be(created.MatchedRatingKey);
        poster.ContentType.Should().Be("image/jpeg");
        // The real bytes, read out of the backend assembly: a JPEG opens FF D8 FF.
        poster.Image.Take(3).Should().Equal(new byte[] { 0xFF, 0xD8, 0xFF });
    }

    [Fact]
    public async Task Creating_a_playlist_with_no_cover_uploads_nothing()
    {
        await _sut.Create(Subject, Username, "stars-4", 3);

        _playlists.Created.Should().ContainSingle();
        _playlists.Posters.Should().BeEmpty();
    }

    /// <summary>
    /// A cover is a decoration. A Plex that refuses the upload — an older server, a token without the
    /// rights — must still leave the user with the playlist they actually asked for.
    /// </summary>
    [Fact]
    public async Task A_refused_cover_does_not_cost_the_user_their_playlist()
    {
        _playlists.PosterFailure = new HttpRequestException("Plex said no");

        var created = await _sut.Create(Subject, Username, SmartPlaylistCatalog.DeepFrontierId, 3);

        created.State.Should().Be(StockPlaylistState.Exists);
        created.MatchedRatingKey.Should().NotBeNull();
        _playlists.Created.Should().ContainSingle();
    }

    // ---- a name held by something we can't rewrite --------------------------------------------

    /// <summary>
    /// "Replace" hands Plex a filter for a playlist that has one. The identical call against an
    /// ordinary playlist means "add these tracks", so a name held by a hand-made playlist is reported
    /// as a clash the user has to settle themselves rather than as a button that would quietly append
    /// a few hundred songs to it.
    /// </summary>
    [Fact]
    public async Task A_name_held_by_an_ordinary_playlist_is_not_offered_as_a_replace()
    {
        _playlists.AddPlain("Frontier");

        var row = (await Survey()).Playlists.Single(p => p.Id == SmartPlaylistCatalog.FrontierId);

        row.State.Should().Be(StockPlaylistState.Differs);
        row.Replaceable.Should().BeFalse();
        row.Note.Should().Contain("isn't a smart one");
    }

    /// <summary>...and the write refuses it too, not just the page that offers it.</summary>
    [Fact]
    public async Task Rewriting_the_rules_of_an_ordinary_playlist_is_refused()
    {
        _playlists.AddPlain("Frontier");

        var row = await _sut.UpdateRules(Subject, Username, SmartPlaylistCatalog.FrontierId, 3);

        row.State.Should().Be(StockPlaylistState.Differs);
        row.Replaceable.Should().BeFalse();
        _playlists.Updated.Should().BeEmpty();
    }

    /// <summary>A smart playlist holding the name is still rewritable — that is what Replace is for.</summary>
    [Fact]
    public async Task A_name_held_by_a_smart_playlist_is_still_replaceable()
    {
        ServerHas("Frontier", "stars-4");

        var row = (await Survey()).Playlists.Single(p => p.Id == SmartPlaylistCatalog.FrontierId);

        row.State.Should().Be(StockPlaylistState.Differs);
        row.Replaceable.Should().BeTrue();
    }

    /// <summary>
    /// Plex answers a rules rewrite with a 200 whether or not the stored query changed. Claiming
    /// success on the strength of the request being accepted is what made a Replace that changed
    /// nothing look like it had worked — until the page refreshed and said "name taken" again.
    /// </summary>
    [Fact]
    public async Task A_rewrite_plex_ignores_is_reported_as_still_differing()
    {
        ServerHas("Frontier", "stars-4");
        _playlists.IgnoreFilterUpdates = true;

        var row = await _sut.UpdateRules(Subject, Username, SmartPlaylistCatalog.FrontierId, 3);

        row.State.Should().Be(StockPlaylistState.Differs);
        row.Note.Should().Contain("still selects something else");
        // ...and nothing is described as ours when it isn't.
        _playlists.Summaries.Should().BeEmpty();
    }

    /// <summary>
    /// Creating over a taken name would leave two playlists called the same thing. The page only
    /// offers Create for a row it surveyed as missing, but a row surveyed before the clash appeared
    /// can still ask, and the answer is the clash — not a duplicate.
    /// </summary>
    [Fact]
    public async Task Creating_over_a_taken_name_makes_nothing()
    {
        ServerHas("Frontier", "stars-4");

        var row = await _sut.Create(Subject, Username, SmartPlaylistCatalog.FrontierId, 3);

        row.State.Should().Be(StockPlaylistState.Differs);
        _playlists.Created.Should().BeEmpty();
    }

    /// <summary>
    /// A generated playlist says what it is for in Plex itself: the tagline, the rules one clause per
    /// line, and whose taste it was built from. The sidebar only has room for a name, and these are
    /// per-account playlists — the same title means different tracks for two people — so the summary is
    /// the only place that record exists.
    /// </summary>
    [Fact]
    public async Task Creating_a_playlist_describes_it_in_plex()
    {
        var created = await _sut.Create(Subject, Username, SmartPlaylistCatalog.FrontierId, 3);

        var (ratingKey, summary) = _playlists.Summaries.Should().ContainSingle().Subject;
        ratingKey.Should().Be(created.MatchedRatingKey);
        // Against the row rather than against pasted wording: the summary is generated from the same
        // definition the page shows, and pinning the text here would mean editing this file to reword
        // a bullet in the catalog.
        summary.Should().StartWith(created.Description!);
        foreach (var detail in created.Details)
        {
            summary.Should().Contain($"• {detail}");
        }

        summary.Should().EndWith($"Generated by Mycelium for {Username}.");
    }

    /// <summary>
    /// The blurb is described from the same options the rules are, so a clause only shows up when the
    /// rule behind it does — here the reject floor, which sits at a different star for a half-star user.
    /// </summary>
    [Fact]
    public async Task The_description_names_this_users_own_reject_floor()
    {
        RatesInHalfStars(true);

        await _sut.Create(Subject, Username, SmartPlaylistCatalog.DeepFrontierId, 3);

        // The half-star floor, which only a half-star user has: a whole-star user's summary says 1★
        // here, because their client can't set anything lower.
        _playlists.Summaries.Should().ContainSingle().Which.Summary.Should().Contain("0.5★");
    }

    /// <summary>
    /// A description is a nicety in the same way a cover is: a Plex that refuses the edit must still
    /// leave the user with the playlist they actually pressed the button for.
    /// </summary>
    [Fact]
    public async Task A_refused_description_does_not_cost_the_user_their_playlist()
    {
        _playlists.SummaryFailure = new HttpRequestException("Plex said no");

        var created = await _sut.Create(Subject, Username, SmartPlaylistCatalog.FrontierId, 3);

        created.State.Should().Be(StockPlaylistState.Exists);
        created.MatchedRatingKey.Should().NotBeNull();
        _playlists.Created.Should().ContainSingle();
    }

    /// <summary>
    /// Unlike the cover, the description follows the rules: it is a statement of what the playlist
    /// selects, and a rewrite replaces exactly the rules it was describing.
    /// </summary>
    [Fact]
    public async Task Replacing_a_drifted_playlist_rewrites_its_description()
    {
        var key = ServerHas("Frontier", "stars-4");

        await _sut.UpdateRules(Subject, Username, SmartPlaylistCatalog.FrontierId, 3);

        var (ratingKey, summary) = _playlists.Summaries.Should().ContainSingle().Subject;
        ratingKey.Should().Be(key);
        summary.Should().StartWith("New or forgotten music in your wheelhouse");
    }

    /// <summary>
    /// Rewriting rules leaves artwork alone — by then the cover may be one the user picked themselves,
    /// and replacing it is not what they asked for when they pressed Replace.
    /// </summary>
    [Fact]
    public async Task Replacing_a_drifted_playlist_leaves_its_cover_alone()
    {
        ServerHas("Deep Frontier", "stars-4");

        var updated = await _sut.UpdateRules(Subject, Username, SmartPlaylistCatalog.DeepFrontierId, 3);

        updated.State.Should().Be(StockPlaylistState.Exists);
        _playlists.Updated.Should().ContainSingle();
        _playlists.Posters.Should().BeEmpty();
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
        public List<(string RatingKey, byte[] Image, string ContentType)> Posters { get; } = new();
        public List<(string RatingKey, string Summary)> Summaries { get; } = new();
        public List<string> TokensSeen { get; } = new();

        /// <summary>Set to make the cover upload throw, standing in for a Plex that refuses it.</summary>
        public Exception? PosterFailure { get; set; }

        /// <summary>The same, for the summary edit.</summary>
        public Exception? SummaryFailure { get; set; }

        public void Clear() => _playlists.Clear();

        public string Add(string title, int sectionKey, PlexSmartFilter filter, int leafCount = 1)
        {
            var key = (_nextKey++).ToString();
            _playlists.Add(new PlexPlaylist(key, title, true, leafCount, Content(sectionKey, filter)));
            return key;
        }

        public void AddRaw(string title, string content) =>
            _playlists.Add(new PlexPlaylist((_nextKey++).ToString(), title, true, 0, content));

        /// <summary>An ordinary hand-made playlist: no rules, and none it could be given.</summary>
        public string AddPlain(string title, int leafCount = 12)
        {
            var key = (_nextKey++).ToString();
            _playlists.Add(new PlexPlaylist(key, title, false, leafCount, null));
            return key;
        }

        /// <summary>
        /// Makes the rules rewrite a no-op that still answers 200, standing in for a Plex that accepts
        /// the request and keeps the playlist it had.
        /// </summary>
        public bool IgnoreFilterUpdates { get; set; }

        private static string Content(int sectionKey, PlexSmartFilter filter) =>
            "library://x/directory/"
            + Uri.EscapeDataString(
                $"/library/sections/{sectionKey}/all?{PlexFilterSerializer.Serialize(filter)}");

        // The stock-playlist feature only ever deals in smart playlists; the archive's whole-listing
        // and membership reads are exercised by PlaylistHarvesterTests instead. Throwing rather than
        // returning empty so a future caller here can't quietly get a wrong answer.
        public Task<PlexPlaylist[]> GetAudioPlaylists(string token) =>
            throw new NotSupportedException("SmartPlaylistService should not read non-smart playlists");

        public Task<PlexPlaylistItem[]> GetPlaylistItems(string token, string ratingKey) =>
            throw new NotSupportedException("SmartPlaylistService should not read playlist membership");

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
            if (!IgnoreFilterUpdates)
            {
                _playlists[at] = _playlists[at] with
                {
                    Content = Content(sectionKey, filter), LeafCount = 42,
                };
            }

            return Task.FromResult(_playlists[at]);
        }

        public Task SetPlaylistSummary(string token, string ratingKey, string summary)
        {
            TokensSeen.Add(token);
            if (SummaryFailure is not null)
            {
                throw SummaryFailure;
            }

            Summaries.Add((ratingKey, summary));
            return Task.CompletedTask;
        }

        public async Task UploadPlaylistPoster(
            string token, string ratingKey, Stream image, string contentType)
        {
            TokensSeen.Add(token);
            if (PosterFailure is not null)
            {
                throw PosterFailure;
            }

            using var buffer = new MemoryStream();
            await image.CopyToAsync(buffer);
            Posters.Add((ratingKey, buffer.ToArray(), contentType));
        }

        public Task<IReadOnlyList<PlexTagEntry>> GetSectionTags(int sectionKey, string field, int type) =>
            Task.FromResult<IReadOnlyList<PlexTagEntry>>(
                Tags.TryGetValue((field, type), out var tags) ? tags : Array.Empty<PlexTagEntry>());
    }
}
