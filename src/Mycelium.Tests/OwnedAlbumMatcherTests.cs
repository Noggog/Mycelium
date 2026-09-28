using FluentAssertions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Xunit;

namespace Mycelium.Tests;

/// <summary>Which release group each owned album is, on a stored discography.</summary>
public class OwnedAlbumMatcherTests
{
    private readonly List<DiscographyReleaseGroup> _groups = new();
    private readonly Dictionary<string, AlbumIdentity> _earlier = new(StringComparer.OrdinalIgnoreCase);

    private void Group(
        string mbid, string title, string type = "Album", string[]? secondary = null,
        string[]? releaseTitles = null, string[]? deezerTitles = null) =>
        _groups.Add(new DiscographyReleaseGroup(
            mbid, title, type, secondary ?? [], null,
            (deezerTitles ?? []).Select((t, i) => new DeezerEdition(i, t, "album", null, 0, null, null, EditionMatchMethod.Title)).ToList(),
            [], releaseTitles));

    private OwnedAlbumMatch One(string album) =>
        OwnedAlbumMatcher.Match("Iron Maiden", [album], _groups, _earlier).Single();

    [Fact]
    public void A_remastered_copy_is_its_album()
    {
        Group("rg-powerslave", "Powerslave");

        var match = One("Powerslave (2015 Remaster)");

        match.ReleaseGroup.Should().Be("rg-powerslave");
        match.Method.Should().Be(OwnedAlbumMatchMethod.Title);
        match.LibraryArtist.Should().Be("Iron Maiden");
    }

    [Fact]
    public void A_release_title_finds_its_group()
    {
        Group("rg-firewatch", "Firewatch Original Score", releaseTitles: ["Firewatch Original Soundtrack"]);

        One("Firewatch Original Soundtrack").ReleaseGroup.Should().Be("rg-firewatch");
    }

    [Fact]
    public void A_Deezer_edition_title_finds_its_group()
    {
        Group("rg-stars", "Stars and Topsoil: A Collection (1982–1990)", deezerTitles: ["Stars And Topsoil"]);

        One("Stars and Topsoil").ReleaseGroup.Should().Be("rg-stars");
    }

    [Fact]
    public void The_studio_album_wins_over_a_live_record_of_the_same_name()
    {
        Group("rg-live", "Killers", secondary: ["Live"]);
        Group("rg-studio", "Killers");

        One("Killers").ReleaseGroup.Should().Be("rg-studio");
    }

    [Fact]
    public void Two_core_groups_of_one_name_leave_the_album_unmatched()
    {
        Group("rg-a", "Iron Maiden");
        Group("rg-b", "Iron Maiden", "EP");

        var match = One("Iron Maiden");

        match.ReleaseGroup.Should().BeNull();
        match.Method.Should().BeNull();
        match.Candidates.Should().Equal("rg-a", "rg-b");
    }

    [Fact]
    public void The_exact_title_settles_a_tie_with_a_bracketed_one()
    {
        Group("rg-sampler", "Sun (sampler)", "EP");
        Group("rg-sun", "Sun");

        var match = One("Sun");

        match.ReleaseGroup.Should().Be("rg-sun");
        match.Method.Should().Be(OwnedAlbumMatchMethod.Title);
    }

    [Fact]
    public void Tied_groups_are_ranked_album_first_then_with_a_Deezer_edition()
    {
        Group("rg-single", "NE-HI", "Single");
        Group("rg-ep", "NE-HI", "EP");
        Group("rg-album", "NE-HI (Remastered)");
        Group("rg-album-deezer", "NE-HI (Deluxe)", deezerTitles: ["NE-HI"]);

        var match = One("NE-HI (2017)");

        match.ReleaseGroup.Should().BeNull();
        match.Candidates.Should().Equal("rg-album-deezer", "rg-album", "rg-ep", "rg-single");
    }

    [Fact]
    public void A_manual_choice_is_kept_even_off_the_discography()
    {
        Group("rg-relaxin", "Relaxin'");
        _earlier["Relaxin' With the Miles Davis Quintet"] =
            new("Relaxin' With the Miles Davis Quintet", "rg-quintet", Manual: true);

        var match = One("Relaxin' With the Miles Davis Quintet");

        match.ReleaseGroup.Should().Be("rg-quintet");
        match.Method.Should().Be(OwnedAlbumMatchMethod.Manual);
    }

    [Fact]
    public void An_album_left_off_MusicBrainz_stays_unmatched()
    {
        Group("rg-home", "Home");
        _earlier["Home"] = new("Home", null, Manual: true);

        var match = One("Home");

        match.ReleaseGroup.Should().BeNull();
        match.Method.Should().Be(OwnedAlbumMatchMethod.Manual);
    }

    [Fact]
    public void An_unlinked_group_is_never_matched_again()
    {
        Group("rg-england", "Maiden England");
        _earlier["Maiden England '88"] = new("Maiden England '88", null, Rejected: ["rg-england"]);

        var match = One("Maiden England '88");

        match.ReleaseGroup.Should().BeNull();
        match.Candidates.Should().BeNull();
    }

    [Fact]
    public void A_near_title_matches_loosely()
    {
        Group("rg-england", "Maiden England");

        One("Maiden England '88").Method.Should().Be(OwnedAlbumMatchMethod.TitleFuzzy);
    }

    [Fact]
    public void An_earlier_id_on_the_discography_is_kept()
    {
        Group("rg-x", "The X Factor");
        _earlier["X Factor Sessions"] = new("X Factor Sessions", "rg-x");

        var match = One("X Factor Sessions");

        match.ReleaseGroup.Should().Be("rg-x");
        match.Method.Should().Be(OwnedAlbumMatchMethod.Earlier);
    }

    [Fact]
    public void An_earlier_id_from_another_act_is_dropped()
    {
        Group("rg-x", "The X Factor");
        _earlier["A Mix I Made"] = new("A Mix I Made", "rg-from-the-wrong-band");

        One("A Mix I Made").ReleaseGroup.Should().BeNull();
    }
}
