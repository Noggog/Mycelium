using Mycelium.Interfaces;

namespace Mycelium.Backend;

/// <summary>
/// When a thumbed artist is worth offering back for a rethink, and how often to go looking. It cuts
/// both ways: a thumbed-<em>down</em> artist the ratings say is a keeper (a "second chance"), and a
/// thumbed-<em>up</em> one they say is a dud (a "second thoughts").
///
/// <paramref name="MinAverage"/> is the average star rating (0–5) a dislike's rated songs must reach to
/// contradict it; <paramref name="MaxAverage"/> the average a like's must fall to. Between the two the
/// ratings agree with the verdict closely enough to leave it alone. <paramref name="MinRatedFraction"/>
/// is the share of the artist's tracks that must actually be rated either way — the guard that stops a
/// single 5★ song on a 40-track discography from reading as "they liked this band" (or one 1★ from
/// condemning it). <paramref name="Interval"/> is the sweep cadence: this exists to re-litigate verdicts
/// made years ago, so it's a slow background pass (default weekly), never a per-request computation.
/// <paramref name="StartupDelay"/> offsets the first run past the catalog + album syncs so the boot
/// isn't three Plex/Deezer-heavy passes at once.
///
/// Read from the RECONSIDER_MIN_AVG_STARS / RECONSIDER_MAX_AVG_STARS / RECONSIDER_MIN_RATED_FRACTION /
/// RECONSIDER_SWEEP_INTERVAL_DAYS env vars in <see cref="MainModule"/>, so the thresholds are
/// configurable and the sweep stays env-free and unit-testable.
/// </summary>
public record ReconsiderPolicy(
    double MinAverage,
    double MaxAverage,
    double MinRatedFraction,
    TimeSpan Interval,
    TimeSpan StartupDelay)
{
    /// <summary>
    /// Whether these ratings contradict <paramref name="verdict"/> hard enough to offer it back: a
    /// dislike needs a high average, a like a low one, and all of them need enough of the discography
    /// rated.
    ///
    /// <para><b>Indifference is the two-sided case.</b> A like and a dislike each have one way of being
    /// wrong, so each tests one threshold. A shrug has two — the ratings may say you actually like the
    /// band, or that you actually don't — so it tests both, and which card it becomes is read off the
    /// stored average afterwards (see <c>DiscoveryEngine.IndifferentItems</c>). Between the thresholds
    /// lies a dead band, <c>(MaxAverage, MinAverage)</c> — 2★–3★ on the defaults — where the ratings
    /// are as unopinionated as the verdict is. That is not a contradiction, it is agreement, and it is
    /// the case this predicate exists to leave alone.</para>
    ///
    /// <para><b>Misconfiguration warning.</b> The two thresholds are independent env knobs
    /// (RECONSIDER_MAX_AVG_STARS / RECONSIDER_MIN_AVG_STARS), and setting max >= min collapses the dead
    /// band and makes this vacuously true for Indifferent alone — every shrugged-at artist with enough
    /// rated tracks would be offered back, every week. The like and dislike sides degrade gracefully
    /// under the same setting (they just flag more); this one does not, so it is called out here rather
    /// than left to be discovered from a feed full of cards.</para>
    /// </summary>
    public bool Contradicts(ArtistRatingStats stats, DiscoveryStatus verdict) =>
        HasEnoughEvidence(stats)
        && verdict switch
        {
            DiscoveryStatus.Disliked => stats.Average >= MinAverage,
            DiscoveryStatus.Liked => stats.Average <= MaxAverage,
            DiscoveryStatus.Indifferent => stats.Average >= MinAverage || stats.Average <= MaxAverage,
            _ => throw new ArgumentOutOfRangeException(
                nameof(verdict), verdict,
                "Only Liked/Disliked/Indifferent verdicts can be contradicted"),
        };

    /// <summary>
    /// Which way a flagged <see cref="DiscoveryStatus.Indifferent"/> row cuts: true when the ratings
    /// argue for a like, false when they argue for a dislike.
    ///
    /// <para>Deliberately a single boolean rather than two independent predicates. A row flagged while
    /// <see cref="MinAverage"/> was 3 and read back after someone raised it to 4 falls into the widened
    /// dead band; with two predicates it would match neither and vanish from both feed sections while
    /// still occupying a flagged queue row — invisible in the UI and impossible to clear from it. One
    /// boolean means every flagged row always lands somewhere, even after the thresholds move.</para>
    /// </summary>
    public bool ArguesForLike(ReconsiderSignal signal) => signal.Average >= MinAverage;

    /// <summary>
    /// Whether there's enough rated music here to argue with a verdict at all. False when the artist
    /// isn't in Plex, has no tracks, or has nothing rated — an unrated artist carries no signal either
    /// way, so the verdict stands.
    /// </summary>
    private bool HasEnoughEvidence(ArtistRatingStats stats) =>
        stats.Present
        && stats.TrackCount > 0
        && stats.RatedCount > 0
        && stats.RatedCount >= stats.TrackCount * MinRatedFraction;
}
