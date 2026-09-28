using FluentAssertions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Deezer.Models;
using Mycelium.Interfaces;
using Mycelium.ListenBrainz.Models;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// Which release group each Deezer album lands on, and by what. Modelled on the Iron Maiden sample in
/// <c>MUSICBRAINZ-IDENTITY.md</c> §2: reissued ids, remaster titles, and same-named singles.
/// </summary>
public class DeezerEditionMatcherTests
{
    private readonly List<MusicBrainzReleaseGroup> _groups = new();
    private readonly List<MusicBrainzRelease> _releases = new();
    private readonly List<DeezerAlbum> _listing = new();
    private readonly HashSet<long> _searchOnly = new();
    private readonly Dictionary<string, IReadOnlyList<DeezerAlbum>> _linked = new();
    private readonly Dictionary<string, DeezerAlbum> _byBarcode = new();
    private readonly Dictionary<string, IReadOnlySet<long>> _rejected = new();

    private MusicBrainzReleaseGroup Group(
        string id, string title, string type = "Album", string? date = null, params string[] secondary)
    {
        var group = new MusicBrainzReleaseGroup
        {
            Id = id, Title = title, PrimaryType = type, FirstReleaseDate = date, SecondaryTypes = secondary.ToList(),
        };
        _groups.Add(group);
        return group;
    }

    private MusicBrainzRelease Release(string id, string group, string? title = null, string? barcode = null)
    {
        var release = new MusicBrainzRelease
        {
            Id = id,
            Title = title ?? _groups.Single(g => g.Id == group).Title,
            Barcode = barcode,
            ReleaseGroup = new MusicBrainzReleaseGroup { Id = group },
        };
        _releases.Add(release);
        return release;
    }

    private static DeezerAlbum Album(long id, string title, string type = "album", string? date = null) =>
        new() { id = id, title = title, record_type = type, release_date = date };

    private DeezerAlbum Listed(long id, string title, string type = "album", string? date = null)
    {
        var album = Album(id, title, type, date);
        _listing.Add(album);
        return album;
    }

    private (List<DiscographyReleaseGroup> Groups, List<UnmatchedDeezerAlbum> Unmatched) Match() =>
        DeezerEditionMatcher.Match(_groups, _releases, _listing, _searchOnly, _linked, _byBarcode, _rejected);

    private static DeezerEdition Edition(List<DiscographyReleaseGroup> groups, string group) =>
        groups.Single(g => g.Mbid == group).Editions.Should().ContainSingle().Subject;

    [Fact]
    public void A_musicbrainz_link_places_the_album_with_high_confidence()
    {
        Group("rg-sit", "Somewhere in Time");
        Release("r-sit", "rg-sit");
        _linked["r-sit"] = [Album(900, "Somewhere in Time (2015 Remaster)")];

        var (groups, _) = Match();

        var edition = Edition(groups, "rg-sit");
        edition.AlbumId.Should().Be(900);
        edition.Method.Should().Be(EditionMatchMethod.MbLink);
        edition.Confidence.Should().Be(EditionConfidence.High);
    }

    [Fact]
    public void A_barcode_Deezer_answers_places_the_album()
    {
        Group("rg-sit", "Somewhere in Time");
        Release("r-cd", "rg-sit", barcode: "0077774625229");
        Release("r-digital", "rg-sit", barcode: "190295851910");
        _byBarcode["190295851910"] = Album(901, "Somewhere in Time");

        var (groups, _) = Match();

        Edition(groups, "rg-sit").Method.Should().Be(EditionMatchMethod.Upc);
    }

    [Fact]
    public void A_link_outranks_a_title_that_points_elsewhere()
    {
        Group("rg-studio", "Speed of Light", "Single", "2015");
        Group("rg-other", "Speed of Light", "Single", "2016");
        Release("r-other", "rg-other");
        Listed(902, "Speed of Light", "single", "2015-08-14");
        _linked["r-other"] = [_listing[0]];

        var (groups, _) = Match();

        Edition(groups, "rg-other").Method.Should().Be(EditionMatchMethod.MbLink);
        groups.Single(g => g.Mbid == "rg-studio").Editions.Should().BeEmpty();
    }

    [Fact]
    public void Titles_are_compared_as_records_so_a_remaster_finds_its_album()
    {
        Group("rg-powerslave", "Powerslave");
        Listed(903, "Powerslave (2015 Remaster)");

        var (groups, unmatched) = Match();

        var edition = Edition(groups, "rg-powerslave");
        edition.Method.Should().Be(EditionMatchMethod.Title);
        edition.Confidence.Should().Be(EditionConfidence.Low);
        unmatched.Should().BeEmpty();
    }

    [Fact]
    public void A_title_is_found_among_the_groups_releases()
    {
        // Chris Remo: "Firewatch Original Soundtrack" is a release in the group "Firewatch Original Score".
        Group("rg-firewatch", "Firewatch Original Score");
        Release("r-ost", "rg-firewatch", title: "Firewatch Original Soundtrack");
        Listed(904, "Firewatch Original Soundtrack");

        var (groups, _) = Match();

        Edition(groups, "rg-firewatch").Method.Should().Be(EditionMatchMethod.Title);
    }

    [Fact]
    public void Live_stays_a_different_record()
    {
        Group("rg-aces", "Aces High", "Single", "1984");
        Listed(905, "Aces High (Live)", "single", "2020-10-02");

        var (groups, unmatched) = Match();

        groups.Single().Editions.Should().BeEmpty();
        unmatched.Should().ContainSingle().Which.AlbumId.Should().Be(905);
    }

    [Fact]
    public void A_shared_title_is_settled_by_type_then_year()
    {
        Group("rg-single", "Aces High", "Single", "1984");
        Group("rg-album", "Aces High", "Album", "2020", "Live");
        Group("rg-single-2020", "Aces High", "Single", "2020");
        Listed(906, "Aces High", "single", "2020-10-02");

        var (groups, _) = Match();

        Edition(groups, "rg-single-2020").AlbumId.Should().Be(906);
    }

    [Fact]
    public void A_tie_nothing_breaks_leaves_the_album_unmatched()
    {
        Group("rg-a", "Aces High", "Single", "1984");
        Group("rg-b", "Aces High", "Single", "2020");
        Listed(907, "Aces High", "single");

        var (groups, unmatched) = Match();

        groups.Should().OnlyContain(g => g.Editions.Count == 0);
        unmatched.Should().ContainSingle();
    }

    [Fact]
    public void A_near_title_matches_loosely()
    {
        Group("rg-england", "Maiden England", "Album", "1989", "Live");
        Listed(908, "Maiden England '88", "album", "2013-03-25");

        var (groups, _) = Match();

        var edition = Edition(groups, "rg-england");
        edition.Method.Should().Be(EditionMatchMethod.TitleFuzzy);
        edition.Confidence.Should().Be(EditionConfidence.Low);
    }

    [Fact]
    public void A_one_word_title_is_never_a_near_match()
    {
        Group("rg-killers", "Killers");
        Listed(909, "Killers Live in Tokyo");

        var (_, unmatched) = Match();

        unmatched.Should().ContainSingle();
    }

    [Fact]
    public void An_album_only_search_found_is_marked_as_such()
    {
        Group("rg-senjutsu", "Senjutsu");
        Listed(910, "Senjutsu");
        _searchOnly.Add(910);

        var (groups, _) = Match();

        Edition(groups, "rg-senjutsu").Method.Should().Be(EditionMatchMethod.Search);
    }

    [Fact]
    public void A_rejected_album_is_not_matched_to_the_group_again()
    {
        Group("rg-powerslave", "Powerslave");
        Listed(911, "Powerslave");
        _rejected["rg-powerslave"] = new HashSet<long> { 911 };

        var (groups, unmatched) = Match();

        groups.Single().Editions.Should().BeEmpty();
        groups.Single().Rejected.Should().Equal(911);
        unmatched.Should().ContainSingle();
    }

    [Fact]
    public void Every_edition_of_a_group_is_kept()
    {
        Group("rg-senjutsu", "Senjutsu");
        Release("r-std", "rg-senjutsu", barcode: "111");
        Listed(912, "Senjutsu");
        Listed(913, "Senjutsu (Deluxe Edition)");
        _byBarcode["111"] = _listing[0];

        var (groups, _) = Match();

        groups.Single().Editions.Select(e => (e.AlbumId, e.Method)).Should().Equal(
            (912, EditionMatchMethod.Upc), (913, EditionMatchMethod.Title));
    }
}
