using FluentAssertions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Xunit;

namespace Mycelium.Tests;

public class ArtistIdentityJudgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);

    private static ResolutionCandidate Candidate(string mbid, int? overlap, params string[] evidence) =>
        new(mbid, "Vulture", null, overlap, evidence);

    private static ArtistResolution Judge(
        int owned,
        params ResolutionCandidate[] candidates) =>
        ArtistIdentityJudge.Judge("Vulture", owned, null, false, false, candidates, Now);

    [Fact]
    public void The_candidate_holding_the_librarys_albums_is_the_artist()
    {
        var result = Judge(3,
            Candidate("danish-indie", 0, ResolutionEvidence.Name),
            Candidate("german-metal", 3, ResolutionEvidence.Name));

        result.Status.Should().Be(ArtistResolutionStatus.Resolved);
        result.Mbid.Should().Be("german-metal");
        result.Confidence.Should().Be(ResolutionConfidence.High);
        result.NeedsAttention.Should().BeFalse();
    }

    [Fact]
    public void One_album_of_many_is_only_medium_confidence()
    {
        var result = Judge(10, Candidate("a", 1, ResolutionEvidence.Name));

        result.Confidence.Should().Be(ResolutionConfidence.Medium);
    }

    [Fact]
    public void One_album_plus_the_Deezer_link_is_high_confidence()
    {
        var result = Judge(10, Candidate("a", 1, ResolutionEvidence.Name, ResolutionEvidence.Deezer));

        result.Confidence.Should().Be(ResolutionConfidence.High);
    }

    [Fact]
    public void The_librarys_only_album_matching_is_high_confidence()
    {
        Judge(1, Candidate("a", 1, ResolutionEvidence.Name)).Confidence.Should().Be(ResolutionConfidence.High);
    }

    [Fact]
    public void Two_acts_sharing_the_librarys_albums_is_ambiguous()
    {
        var result = Judge(4,
            Candidate("a", 1, ResolutionEvidence.Name),
            Candidate("b", 1, ResolutionEvidence.Name));

        result.Status.Should().Be(ArtistResolutionStatus.Ambiguous);
        result.Mbid.Should().BeNull();
        result.NeedsAttention.Should().BeTrue();
    }

    [Fact]
    public void A_tie_on_albums_is_broken_by_the_Deezer_link()
    {
        var result = Judge(4,
            Candidate("a", 1, ResolutionEvidence.Name),
            Candidate("b", 1, ResolutionEvidence.Name, ResolutionEvidence.Deezer));

        result.Mbid.Should().Be("b");
        result.Confidence.Should().Be(ResolutionConfidence.Medium);
    }

    [Fact]
    public void A_name_alone_resolves_but_needs_a_person()
    {
        var result = Judge(0, Candidate("a", null, ResolutionEvidence.Name));

        result.Status.Should().Be(ArtistResolutionStatus.Resolved);
        result.Confidence.Should().Be(ResolutionConfidence.Low);
        result.NeedsAttention.Should().BeTrue();
    }

    [Fact]
    public void Several_same_named_acts_and_nothing_else_is_ambiguous()
    {
        Judge(0,
                Candidate("a", null, ResolutionEvidence.Name),
                Candidate("b", null, ResolutionEvidence.Name))
            .Status.Should().Be(ArtistResolutionStatus.Ambiguous);
    }

    [Fact]
    public void A_Deezer_link_with_no_albums_to_check_is_medium()
    {
        var result = Judge(0,
            Candidate("a", null, ResolutionEvidence.Deezer),
            Candidate("b", null, ResolutionEvidence.Name));

        result.Mbid.Should().Be("a");
        result.Confidence.Should().Be(ResolutionConfidence.Medium);
    }

    [Fact]
    public void A_Deezer_link_whose_discography_has_none_of_the_librarys_albums_is_low()
    {
        // The Deezer page merges same-named acts, so its link can point at the wrong one of them.
        var result = Judge(5, Candidate("a", 0, ResolutionEvidence.Deezer, ResolutionEvidence.Name));

        result.Confidence.Should().Be(ResolutionConfidence.Low);
        result.Reason.Should().Contain("None of the library's 5 album(s)");
    }

    private static ResolutionCandidate Holding(string mbid, params string[] albums) =>
        new(mbid, "Doldrums", mbid, albums.Length, [ResolutionEvidence.Name], albums);

    [Fact]
    public void Candidates_each_holding_a_different_share_of_the_albums_is_mixed()
    {
        var result = Judge(4,
            Holding("canadian", "Lesser Evil", "Egypt"),
            Holding("space-rock", "Acupuncture", "Feng Shui"));

        result.Status.Should().Be(ArtistResolutionStatus.Mixed);
        result.Mbid.Should().BeNull();
        result.NeedsAttention.Should().BeTrue();
    }

    [Fact]
    public void A_bigger_share_does_not_hide_a_second_act_with_two_albums()
    {
        Judge(7,
                Holding("a", "One", "Two", "Three", "Four", "Five"),
                Holding("b", "Six", "Seven"))
            .Status.Should().Be(ArtistResolutionStatus.Mixed);
    }

    [Fact]
    public void Two_acts_sharing_the_same_album_title_is_not_a_split()
    {
        Judge(3, Holding("a", "Greatest Hits"), Holding("b", "Greatest Hits"))
            .Status.Should().Be(ArtistResolutionStatus.Ambiguous);
    }

    [Fact]
    public void One_stray_common_title_on_another_act_is_not_a_split()
    {
        var result = Judge(5, Holding("a", "One", "Two", "Three", "Four"), Holding("b", "Demo"));

        result.Status.Should().Be(ArtistResolutionStatus.Resolved);
        result.Mbid.Should().Be("a");
    }

    [Fact]
    public void No_candidates_is_missing()
    {
        var result = Judge(2);

        result.Status.Should().Be(ArtistResolutionStatus.Missing);
        result.NeedsAttention.Should().BeTrue();
    }

    [Fact]
    public void A_pin_is_taken_as_given()
    {
        var pinned = new MusicBrainzIdentity("pinned", "Vulture", "German speed/thrash metal band");

        var result = ArtistIdentityJudge.Judge("Vulture", 3, pinned, currentIsPinned: true, unlinked: false, [], Now);

        result.Status.Should().Be(ArtistResolutionStatus.Pinned);
        result.Mbid.Should().Be("pinned");
        result.Disambiguation.Should().Be("German speed/thrash metal band");
        result.NeedsAttention.Should().BeFalse();
    }

    [Fact]
    public void An_unlink_needs_a_person()
    {
        var result = ArtistIdentityJudge.Judge("Vulture", 3, null, false, unlinked: true, [], Now);

        result.Status.Should().Be(ArtistResolutionStatus.Unlinked);
        result.NeedsAttention.Should().BeTrue();
    }

    [Fact]
    public void Resolving_to_another_act_than_todays_link_is_flagged()
    {
        var today = new MusicBrainzIdentity("danish-indie", "Vulture");

        var result = ArtistIdentityJudge.Judge("Vulture", 3, today, false, false,
            [
                Candidate("danish-indie", 0, ResolutionEvidence.Current, ResolutionEvidence.Name),
                Candidate("german-metal", 3, ResolutionEvidence.Name),
            ],
            Now);

        result.Mbid.Should().Be("german-metal");
        result.CurrentMbid.Should().Be("danish-indie");
        result.DisagreesWithCurrent.Should().BeTrue();
    }
}
