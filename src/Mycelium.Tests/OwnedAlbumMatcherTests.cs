using FluentAssertions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Xunit;

namespace Mycelium.Tests;

/// <summary>Which release group each owned album is, on a stored discography.</summary>
public class OwnedAlbumMatcherTests
{
    private readonly List<DiscographyReleaseGroup> _groups = new();
    private readonly Dictionary<string, string?> _earlier = new(StringComparer.OrdinalIgnoreCase);

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
        _earlier["X Factor Sessions"] = "rg-x";

        var match = One("X Factor Sessions");

        match.ReleaseGroup.Should().Be("rg-x");
        match.Method.Should().Be(OwnedAlbumMatchMethod.Earlier);
    }

    [Fact]
    public void An_earlier_id_from_another_act_is_dropped()
    {
        Group("rg-x", "The X Factor");
        _earlier["A Mix I Made"] = "rg-from-the-wrong-band";

        One("A Mix I Made").ReleaseGroup.Should().BeNull();
    }
}
