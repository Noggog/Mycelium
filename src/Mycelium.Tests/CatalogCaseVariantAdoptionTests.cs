using FluentAssertions;
using MongoDB.Bson;
using Mycelium.MongoDB.Services.Data;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// A Deezer/MusicBrainz pin or detach made under one spelling ("NoName", from a source search) has to
/// reach the library artist Plex spells differently ("Noname"), or the user's correction is silently
/// dropped and the library artist re-guesses its identity by name.
/// </summary>
public class CatalogCaseVariantAdoptionTests
{
    private static BsonDocument Pin(string name, long deezerId, bool present = false, bool presentFlag = false)
    {
        var doc = new BsonDocument
        {
            ["_id"] = name, ["name"] = name, ["deezerId"] = deezerId, ["deezerName"] = name,
            ["deezerOverride"] = true,
        };
        if (presentFlag) doc["present"] = present;
        return doc;
    }

    private static Dictionary<string, BsonDocument> Targets(params BsonDocument[] docs) =>
        docs.ToDictionary(d => d["_id"].AsString);

    [Fact]
    public void A_pin_under_another_spelling_moves_onto_the_library_artist()
    {
        var plan = ArtistCatalogRepo.PlanAdoptions(
            ["Noname"],
            [Pin("NoName", 235043301)],
            Targets(new BsonDocument { ["_id"] = "Noname", ["deezerId"] = 145007L, ["deezerOverride"] = false }));

        var adoption = plan.Should().ContainSingle().Subject;
        adoption.Target.Should().Be("Noname");
        adoption.Set["deezerId"].ToInt64().Should().Be(235043301);
        adoption.Set["deezerOverride"].AsBoolean.Should().BeTrue();
        adoption.Unset.Should().Contain("deezerUnlinked");
        adoption.DeleteFrom.Should().BeTrue("a pin-only doc has served its purpose once adopted");
    }

    [Fact]
    public void A_detach_under_another_spelling_clears_the_library_artists_identity()
    {
        var detach = new BsonDocument { ["_id"] = "ALEX", ["name"] = "ALEX", ["deezerUnlinked"] = true };

        var adoption = ArtistCatalogRepo.PlanAdoptions(["Alex"], [detach], Targets()).Single();

        adoption.Set["deezerUnlinked"].AsBoolean.Should().BeTrue();
        adoption.Unset.Should().Contain(["deezerOverride", "deezerId", "deezerName", "imageUrl"]);
    }

    [Fact]
    public void The_library_artists_own_decision_is_never_overwritten()
    {
        var adoption = ArtistCatalogRepo.PlanAdoptions(
            ["Miel"],
            [Pin("MIEL", 2)],
            Targets(new BsonDocument { ["_id"] = "Miel", ["deezerId"] = 1L, ["deezerOverride"] = true })).Single();

        adoption.Set.ElementCount.Should().Be(0);
        adoption.Unset.Should().BeEmpty();
    }

    [Fact]
    public void Each_source_is_adopted_on_its_own()
    {
        var pin = Pin("DAUGHTER", 1028626);
        pin["musicBrainzMbid"] = "mbid";
        pin["musicBrainzOverride"] = true;
        var target = new BsonDocument { ["_id"] = "Daughter", ["deezerUnlinked"] = true };

        var adoption = ArtistCatalogRepo.PlanAdoptions(["Daughter"], [pin], Targets(target)).Single();

        adoption.Set.Contains("deezerId").Should().BeFalse("the library artist already decided Deezer");
        adoption.Set["musicBrainzMbid"].AsString.Should().Be("mbid");
    }

    [Fact]
    public void An_old_library_doc_is_adopted_from_but_kept()
    {
        var adoption = ArtistCatalogRepo.PlanAdoptions(
            ["Miel"], [Pin("MIEL", 2, present: false, presentFlag: true)], Targets()).Single();

        adoption.Set["deezerId"].ToInt64().Should().Be(2);
        adoption.DeleteFrom.Should().BeFalse();
    }

    [Fact]
    public void A_pin_on_the_exact_library_spelling_is_left_to_the_sync()
    {
        ArtistCatalogRepo.PlanAdoptions(["Spoon"], [Pin("Spoon", 5)], Targets()).Should().BeEmpty();
    }

    [Fact]
    public void A_pin_for_an_artist_not_in_the_library_stays_where_it_is()
    {
        ArtistCatalogRepo.PlanAdoptions(["Spoon"], [Pin("Boygenius", 5457940)], Targets()).Should().BeEmpty();
    }

    [Fact]
    public void A_name_two_library_artists_share_case_blind_is_ambiguous_and_skipped()
    {
        ArtistCatalogRepo.PlanAdoptions(["SOLE", "Sole"], [Pin("sole", 1)], Targets()).Should().BeEmpty();
    }
}
