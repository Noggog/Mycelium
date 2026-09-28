using System.Text.Json;
using FluentAssertions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>The reconciliation page's album groups, and what a person's answer to one does.</summary>
public class AlbumReconciliationTests
{
    private const string Maiden = "ca891d65-d9b0-4258-89f7-e6ba29d83767";
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);

    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly FakeArtistResolutionRepo _resolutions = new();
    private readonly FakeArtistDiscographyRepo _discographies = new();
    private readonly AlbumReconciliation _sut;

    public AlbumReconciliationTests()
    {
        _sut = new AlbumReconciliation(_discographies, _resolutions, _catalog);
        _resolutions.Seed(new ArtistResolution(
            "Iron Maiden", ArtistResolutionStatus.Resolved, ResolutionConfidence.High, Maiden, "Iron Maiden",
            null, null, 4, [], "", Start));
        _catalog.GetAlbumIdentities(Arg.Any<ArtistKey>()).Returns(new Dictionary<string, AlbumIdentity>());

        _discographies.Put(new ArtistDiscography(
            Maiden, "Iron Maiden", [], Start, Start.AddDays(60),
            [
                Group("rg-maiden", "Iron Maiden", "Album", tracks: 8),
                Group("rg-maiden-ep", "Iron Maiden", "EP"),
                Group("rg-england", "Maiden England"),
                Group("rg-powerslave", "Powerslave"),
            ],
            [new UnmatchedDeezerAlbum(555, "Home Tapes (Deluxe)", "album", null)],
            [
                new OwnedAlbumMatch("Iron Maiden", "Iron Maiden", null, null, ["rg-maiden", "rg-maiden-ep"]),
                new OwnedAlbumMatch("Home Tapes", "Iron Maiden", null, null),
                new OwnedAlbumMatch("Garage Demos", "Iron Maiden", null, null),
                new OwnedAlbumMatch("Maiden England '88", "Iron Maiden", "rg-england", OwnedAlbumMatchMethod.TitleFuzzy),
                new OwnedAlbumMatch("Powerslave", "Iron Maiden", "rg-powerslave", OwnedAlbumMatchMethod.Title),
                new OwnedAlbumMatch("Left Alone", "Iron Maiden", null, OwnedAlbumMatchMethod.Manual),
            ]));
    }

    private static DiscographyReleaseGroup Group(string mbid, string title, string type = "Album", int? tracks = null) =>
        new(mbid, title, type, [], "1980",
            tracks is { } t ? [new DeezerEdition(1, title, "album", null, t, null, true, EditionMatchMethod.MbLink)] : [],
            []);

    [Fact]
    public async Task Albums_that_need_a_person_are_grouped_per_artist()
    {
        var artist = (await _sut.List()).Single();

        artist.Should().BeEquivalentTo(new { Pick = 1, Missing = 2, Loose = 1 });
        artist.Albums.Select(a => (a.Title, a.Kind)).Should().Equal(
            ("Iron Maiden", AlbumReconcileKind.Pick),
            ("Garage Demos", AlbumReconcileKind.Missing),
            ("Home Tapes", AlbumReconcileKind.Missing),
            ("Maiden England '88", AlbumReconcileKind.Loose));

        var pick = artist.Albums[0];
        pick.Candidates.Select(c => c.Mbid).Should().Equal("rg-maiden", "rg-maiden-ep");
        pick.Candidates[0].Should().BeEquivalentTo(new { Tracks = 8, HasDeezer = true });
        artist.Albums[1].DeezerAlbumId.Should().BeNull();
        artist.Albums[2].DeezerAlbumId.Should().Be(555);
        artist.Albums[3].Linked!.Mbid.Should().Be("rg-england");
    }

    [Fact]
    public async Task A_pick_is_stored_as_a_manual_choice_and_leaves_the_page()
    {
        await _sut.Link(Maiden, "Iron Maiden", "iron maiden", "rg-maiden");

        await _catalog.Received(1).SetAlbumIdentities(
            new ArtistKey("Iron Maiden"),
            Arg.Is<IReadOnlyCollection<AlbumIdentity>>(d =>
                d.Single() == new AlbumIdentity("Iron Maiden", "rg-maiden", true, null)));
        var owned = _discographies.Items[Maiden].Owned!.Single(o => o.Title == "Iron Maiden");
        owned.Should().Be(new OwnedAlbumMatch("Iron Maiden", "Iron Maiden", "rg-maiden", OwnedAlbumMatchMethod.Manual));
        (await _sut.List()).Single().Pick.Should().Be(0);
    }

    [Fact]
    public async Task Leaving_an_album_takes_it_off_the_page()
    {
        await _sut.Leave(Maiden, "Iron Maiden", "Garage Demos");

        await _catalog.Received(1).SetAlbumIdentities(
            new ArtistKey("Iron Maiden"),
            Arg.Is<IReadOnlyCollection<AlbumIdentity>>(d =>
                d.Single() == new AlbumIdentity("Garage Demos", null, true, null)));
        (await _sut.List()).Single().Albums.Select(a => a.Title).Should().NotContain("Garage Demos");
    }

    [Fact]
    public async Task Unlinking_a_loose_match_rejects_the_group_and_sends_it_to_missing()
    {
        await _sut.Unlink(Maiden, "Iron Maiden", "Maiden England '88");

        await _catalog.Received(1).SetAlbumIdentities(
            new ArtistKey("Iron Maiden"),
            Arg.Is<IReadOnlyCollection<AlbumIdentity>>(d =>
                d.Single().Mbid == null && !d.Single().Manual && d.Single().Rejected!.SequenceEqual(new[] { "rg-england" })));
        (await _sut.List()).Single().Albums.Single(a => a.Title == "Maiden England '88").Kind
            .Should().Be(AlbumReconcileKind.Missing);
    }

    [Fact]
    public async Task An_album_not_on_the_discography_is_not_found()
    {
        (await _sut.Leave(Maiden, "Iron Maiden", "Somebody Else's Record")).Should().BeNull();

        await _catalog.DidNotReceiveWithAnyArgs().SetAlbumIdentities(default!, default!);
    }

    [Fact]
    public async Task Kinds_go_to_the_web_app_by_name()
    {
        var json = JsonSerializer.Serialize(await _sut.List(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.Should().Contain("\"kind\":\"Pick\"").And.Contain("\"kind\":\"Loose\"");
    }
}
