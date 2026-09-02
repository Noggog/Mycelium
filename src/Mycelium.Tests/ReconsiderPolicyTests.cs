using FluentAssertions;
using Mycelium.Backend;
using Mycelium.Interfaces;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The thresholds themselves, apart from the sweep that applies them. Worth pinning separately because
/// indifference broke the shape the predicate had: a like and a dislike each have one way of being
/// wrong, so each tests one bound, while a shrug can be wrong in either direction and tests both. That
/// leaves a dead band between the bounds which is not a gap but the point — ratings as unopinionated as
/// the verdict agree with it.
/// </summary>
public class ReconsiderPolicyTests
{
    private static readonly ReconsiderPolicy Policy = new(
        MinAverage: 3, MaxAverage: 2, MinRatedFraction: 1.0 / 3,
        Interval: TimeSpan.FromDays(7), StartupDelay: TimeSpan.Zero);

    private static ArtistRatingStats Stats(double average, int rated = 4, int tracks = 6) =>
        new(new ArtistKey("Low"), Present: true, Highest: 5, Lowest: 0,
            Average: average, RatedCount: rated, TrackCount: tracks);

    [Theory]
    [InlineData(3.0, true)]   // at the bar
    [InlineData(4.5, true)]
    [InlineData(2.9, false)]
    public void A_dislike_is_contradicted_only_by_a_high_average(double average, bool expected)
    {
        Policy.Contradicts(Stats(average), DiscoveryStatus.Disliked).Should().Be(expected);
    }

    [Theory]
    [InlineData(2.0, true)]   // at the bar
    [InlineData(0.5, true)]
    [InlineData(2.1, false)]
    public void A_like_is_contradicted_only_by_a_low_average(double average, bool expected)
    {
        Policy.Contradicts(Stats(average), DiscoveryStatus.Liked).Should().Be(expected);
    }

    /// <summary>
    /// Both bounds, and the dead band between them. 2★–3★ on the shipped thresholds is the band where a
    /// shrug is simply correct.
    /// </summary>
    [Theory]
    [InlineData(4.5, true)]
    [InlineData(3.0, true)]   // at the upper bar
    [InlineData(2.9, false)]  // dead band
    [InlineData(2.5, false)]  // dead band
    [InlineData(2.1, false)]  // dead band
    [InlineData(2.0, true)]   // at the lower bar
    [InlineData(0.5, true)]
    public void A_shrug_is_contradicted_from_either_side_but_not_from_the_middle(
        double average, bool expected)
    {
        Policy.Contradicts(Stats(average), DiscoveryStatus.Indifferent).Should().Be(expected);
    }

    /// <summary>
    /// The evidence bar applies to indifference exactly as it does to a thumb: one 5★ song on a
    /// forty-track discography is not the user telling you they like the band.
    /// </summary>
    [Fact]
    public void A_shrug_with_too_little_rated_is_left_alone_however_extreme_the_average()
    {
        Policy.Contradicts(Stats(5.0, rated: 1, tracks: 40), DiscoveryStatus.Indifferent)
            .Should().BeFalse();
    }

    /// <summary>
    /// The direction split the two indifferent feed sections are built on. It is one boolean rather than
    /// two predicates so that every flagged row always lands in a section, even if the thresholds are
    /// retuned after it was flagged — see <see cref="ReconsiderPolicy.ArguesForLike"/>.
    /// </summary>
    [Theory]
    [InlineData(4.5, true)]
    [InlineData(3.0, true)]
    [InlineData(2.5, false)]
    [InlineData(1.0, false)]
    public void Which_way_a_flagged_shrug_cuts_is_read_off_the_average(double average, bool arguesUp)
    {
        Policy.ArguesForLike(new ReconsiderSignal(average, 4, 6)).Should().Be(arguesUp);
    }

    /// <summary>
    /// A row flagged under one threshold and read after it moved falls in the widened dead band. Two
    /// independent predicates would drop it from both feed sections — flagged in Mongo, invisible in the
    /// UI, unclearable from it. The single boolean guarantees it still lands somewhere.
    /// </summary>
    [Fact]
    public void A_row_stranded_by_a_retuned_threshold_still_lands_on_one_side()
    {
        var retuned = Policy with { MinAverage = 4 };
        var flaggedUnderTheOldBar = new ReconsiderSignal(3.2, 4, 6);

        retuned.Contradicts(Stats(3.2), DiscoveryStatus.Indifferent).Should().BeFalse();
        // ...and yet it is still flagged, so serving it must not depend on Contradicts agreeing.
        retuned.ArguesForLike(flaggedUnderTheOldBar).Should().BeFalse();
    }

    [Theory]
    [InlineData(DiscoveryStatus.Pending)]
    [InlineData(DiscoveryStatus.Snoozed)]
    public void A_status_that_is_not_a_verdict_cannot_be_contradicted(DiscoveryStatus status)
    {
        var act = () => Policy.Contradicts(Stats(4.0), status);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
