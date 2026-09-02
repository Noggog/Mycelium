using FluentAssertions;
using Mycelium.Interfaces;
using Xunit;

namespace Mycelium.Tests;

public class ArtistTagTests
{
    [Fact]
    public void Every_verdict_gets_its_own_suffix()
    {
        ArtistTag.For("noggog", DiscoveryStatus.Liked).Should().Be("noggog_liked");
        ArtistTag.For("noggog", DiscoveryStatus.Disliked).Should().Be("noggog_disliked");
        ArtistTag.For("noggog", DiscoveryStatus.Indifferent).Should().Be("noggog_indifferent");
    }

    /// <summary>
    /// The regression guard for the fold this method used to be. When it read
    /// <c>status == Liked ? "liked" : "disliked"</c>, every non-verdict status silently produced a
    /// rejection tag — and a wrongly-rejected band fails nothing loudly, it just stops appearing in
    /// the Deep Frontier playlist. A snooze is a deferred decision and Pending is no decision at all;
    /// neither has a tag, and asking for one must yield nothing rather than the wrong thing.
    /// </summary>
    [Theory]
    [InlineData(DiscoveryStatus.Pending)]
    [InlineData(DiscoveryStatus.Snoozed)]
    public void A_status_that_is_not_a_verdict_has_no_tag(DiscoveryStatus status)
    {
        ArtistTag.For("noggog", status).Should().BeNull();
    }

    /// <summary>
    /// The one-verdict-tag invariant, from the other side: whatever goes on, everything else comes off.
    /// This was expressible as "the opposite" with two verdicts and is not with three — an
    /// Indifferent→Liked flip has two tags to strip, and a call site that stripped only one would leave
    /// a stale verdict behind without failing anything.
    /// </summary>
    [Fact]
    public void The_other_verdict_tags_are_everything_but_the_one_going_on()
    {
        ArtistTag.OtherVerdictTags("noggog", DiscoveryStatus.Liked)
            .Should().BeEquivalentTo("noggog_disliked", "noggog_indifferent");
        ArtistTag.OtherVerdictTags("noggog", DiscoveryStatus.Disliked)
            .Should().BeEquivalentTo("noggog_liked", "noggog_indifferent");
        ArtistTag.OtherVerdictTags("noggog", DiscoveryStatus.Indifferent)
            .Should().BeEquivalentTo("noggog_liked", "noggog_disliked");
    }

    /// <summary>
    /// A cleared rating passes null: we don't know which verdict the row held, so every one comes off.
    /// This is the only route that removes a verdict tag, so a tag missing from this set is a tag with
    /// no way out of Plex short of the dev panel.
    /// </summary>
    [Fact]
    public void Clearing_a_rating_strips_every_verdict_tag()
    {
        ArtistTag.OtherVerdictTags("noggog", current: null)
            .Should().BeEquivalentTo("noggog_liked", "noggog_disliked", "noggog_indifferent");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("@example.com")]
    public void No_usable_username_yields_no_tags_to_strip(string? username)
    {
        ArtistTag.OtherVerdictTags(username, DiscoveryStatus.Liked).Should().BeEmpty();
    }

    [Fact]
    public void Username_is_lowercased()
    {
        ArtistTag.For("Noggog", DiscoveryStatus.Liked).Should().Be("noggog_liked");
    }

    [Fact]
    public void Email_style_username_trims_to_the_local_part()
    {
        ArtistTag.For("noggog@gmail.com", DiscoveryStatus.Liked).Should().Be("noggog_liked");
    }

    [Fact]
    public void Non_alphanumeric_characters_are_stripped_but_underscores_kept()
    {
        ArtistTag.For("Justin C. Swanson", DiscoveryStatus.Liked).Should().Be("justincswanson_liked");
        ArtistTag.For("a_b-c", DiscoveryStatus.Liked).Should().Be("a_bc_liked");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@example.com")]
    [InlineData("!!!")]
    public void No_usable_username_yields_null_so_the_caller_skips_tagging(string? username)
    {
        ArtistTag.For(username, DiscoveryStatus.Liked).Should().BeNull();
    }

    [Theory]
    [InlineData("noggog_liked")]
    [InlineData("noggog_disliked")]
    [InlineData("noggog_indifferent")]
    [InlineData("NOGGOG_LIKED")]
    public void Managed_tags_are_recognized(string label)
    {
        ArtistTag.IsManaged(label).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Pop/Rock")]
    [InlineData("noggog")]
    [InlineData("favorite")]
    public void Non_managed_labels_are_left_alone(string? label)
    {
        ArtistTag.IsManaged(label).Should().BeFalse();
    }

    [Fact]
    public void What_For_produces_is_recognized_as_managed()
    {
        ArtistTag.IsManaged(ArtistTag.For("noggog", DiscoveryStatus.Liked)).Should().BeTrue();
        ArtistTag.IsManaged(ArtistTag.For("noggog", DiscoveryStatus.Disliked)).Should().BeTrue();
        ArtistTag.IsManaged(ArtistTag.For("noggog", DiscoveryStatus.Indifferent)).Should().BeTrue();
    }

    /// <summary>
    /// The suffixes must not alias each other, or the dev wipe's rebuild would misclassify. Worth
    /// pinning because "_liked" is a substring of "_disliked" in the obvious reading — it isn't a
    /// suffix of it, which is what EndsWith actually asks.
    /// </summary>
    [Fact]
    public void The_verdict_suffixes_do_not_alias_one_another()
    {
        ArtistTag.IsVerdict("noggog_indifferent").Should().BeTrue();
        "noggog_disliked".EndsWith("_liked", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        "noggog_indifferent".EndsWith("_liked", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public void Added_credit_gets_its_own_suffix_and_the_same_username_cleanup()
    {
        ArtistTag.Added("noggog").Should().Be("noggog_added");
        ArtistTag.Added("Justin C. Swanson@gmail.com").Should().Be("justincswanson_added");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("@example.com")]
    public void No_usable_username_yields_no_added_credit(string? username)
    {
        ArtistTag.Added(username).Should().BeNull();
    }

    [Fact]
    public void An_added_credit_is_ours_but_is_not_a_verdict()
    {
        // The distinction the dev wipe turns on: it strips what it can rebuild from the stored ratings,
        // and nothing can rebuild who added a record. The tag editor hides both.
        var added = ArtistTag.Added("noggog")!;
        ArtistTag.IsAdded(added).Should().BeTrue();
        ArtistTag.IsManaged(added).Should().BeTrue();
        ArtistTag.IsVerdict(added).Should().BeFalse();
    }

    [Theory]
    [InlineData("noggog_liked")]
    [InlineData("NOGGOG_DISLIKED")]
    [InlineData("noggog_indifferent")]
    public void A_verdict_is_ours_but_is_not_an_added_credit(string label)
    {
        ArtistTag.IsVerdict(label).Should().BeTrue();
        ArtistTag.IsManaged(label).Should().BeTrue();
        ArtistTag.IsAdded(label).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Pop/Rock")]
    [InlineData("ambient")]
    public void A_descriptor_mood_is_neither(string? label)
    {
        ArtistTag.IsVerdict(label).Should().BeFalse();
        ArtistTag.IsAdded(label).Should().BeFalse();
        ArtistTag.IsRecommended(label).Should().BeFalse();
    }

    [Fact]
    public void Recommended_marker_gets_its_own_suffix_and_the_same_username_cleanup()
    {
        ArtistTag.Recommended("noggog").Should().Be("noggog_recommended");
        ArtistTag.Recommended("Justin C. Swanson@gmail.com").Should().Be("justincswanson_recommended");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("@example.com")]
    public void No_usable_username_yields_no_recommended_marker(string? username)
    {
        ArtistTag.Recommended(username).Should().BeNull();
    }

    [Fact]
    public void A_recommended_marker_is_ours_but_is_neither_a_verdict_nor_a_credit()
    {
        // It is derived state, not a decision: the sweep recomputes it wholesale, so the dev wipe has
        // no business stripping it (IsVerdict) and it is not history anyone could lose (IsAdded). The
        // tag editor still hides it, because hand-editing it would just be undone at the next pass.
        var marker = ArtistTag.Recommended("noggog")!;
        ArtistTag.IsRecommended(marker).Should().BeTrue();
        ArtistTag.IsManaged(marker).Should().BeTrue();
        ArtistTag.IsVerdict(marker).Should().BeFalse();
        ArtistTag.IsAdded(marker).Should().BeFalse();
    }
}
