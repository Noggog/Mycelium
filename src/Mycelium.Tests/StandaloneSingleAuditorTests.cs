using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Deezer.Models;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The single audit: which of an artist's singles are records in their own right, and so may be
/// pushed at a user, rather than trailers for an album that will make them redundant.
/// </summary>
public class StandaloneSingleAuditorTests
{
    private readonly IDeezerApi _deezer = Substitute.For<IDeezerApi>();
    private readonly FakeDeezerAlbumTrackRepo _tracks = new();
    private readonly StandaloneSingleAuditor _sut;

    public StandaloneSingleAuditorTests()
    {
        _sut = new StandaloneSingleAuditor(_deezer, _tracks, NullLogger<StandaloneSingleAuditor>.Instance);
        _deezer.GetAlbumTracks(Arg.Any<long>()).Returns(Array.Empty<DeezerTrack>());
    }

    // Dates are relative to now, because the grace period is measured against the wall clock: a fixed
    // date would quietly change meaning as it aged past two years.
    private static DateOnly DaysAgo(int days) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-days));

    private static SingleAuditRelease Single(long id, string title, DateOnly? released) =>
        new(id, title, "single", released);

    private static SingleAuditRelease Record(
        long id, string title, DateOnly? released, string type = "album") =>
        new(id, title, type, released);

    /// <summary>Stands in for what an earlier sweep learned, so a case needn't mock the fetch.</summary>
    private void Listing(long albumId, params string[] titles) => _tracks.Seed(albumId, titles);

    [Fact]
    public async Task A_single_no_album_holds_and_an_album_has_followed_is_offered()
    {
        // The case the feature exists for: a one-off released two years ago, an album since, and that
        // album doesn't carry the song.
        Listing(1, "Wayfaring Stranger");
        Listing(2, "Small Things", "Towing the Line");

        var verdict = await _sut.Audit(new[]
        {
            Single(1, "Wayfaring Stranger", DaysAgo(900)),
            Record(2, "Noonday Dream", DaysAgo(400)),
        }, learn: false);

        verdict.Should().BeEquivalentTo(new[] { 1L });
    }

    [Fact]
    public async Task A_single_a_later_album_absorbed_is_withheld()
    {
        // The ordinary pre-release cut. An album did follow it — test 2 passes — but the album carries
        // the song, so buying the single would buy it twice.
        Listing(1, "Small Things");
        Listing(2, "Small Things", "Towing the Line");

        var verdict = await _sut.Audit(new[]
        {
            Single(1, "Small Things", DaysAgo(900)),
            Record(2, "Noonday Dream", DaysAgo(400)),
        }, learn: false);

        verdict.Should().BeEmpty();
    }

    [Fact]
    public async Task A_single_an_earlier_album_already_held_is_withheld()
    {
        // Coverage is not only forward-looking: a song lifted off an old record and re-released as a
        // single is just as redundant as one that is about to appear on a new one.
        Listing(1, "Small Things");
        Listing(2, "Small Things", "Towing the Line");

        var verdict = await _sut.Audit(new[]
        {
            Single(1, "Small Things", DaysAgo(900)),
            Record(2, "Every Kingdom", DaysAgo(2000)),
            // An album after the single, so test 2 isn't what's doing the work here.
            Record(3, "Noonday Dream", DaysAgo(400)),
        }, learn: false);

        verdict.Should().BeEmpty();
    }

    [Fact]
    public async Task A_recent_single_with_no_album_since_is_withheld()
    {
        // Test 2. Nothing the artist has put out holds the song — but the album that will is simply not
        // out yet, and offering the single now is how the feed fills with things about to be redundant.
        Listing(1, "Wayfaring Stranger");
        Listing(2, "Small Things");

        var verdict = await _sut.Audit(new[]
        {
            Single(1, "Wayfaring Stranger", DaysAgo(30)),
            Record(2, "Every Kingdom", DaysAgo(2000)),
        }, learn: false);

        verdict.Should().BeEmpty();
    }

    [Fact]
    public async Task A_single_that_outlives_the_grace_period_is_offered_with_no_album_at_all()
    {
        // The fallback. An artist who never makes another record would otherwise bury every standalone
        // single they have for ever, which is most of what this audit is meant to find.
        Listing(1, "Wayfaring Stranger");

        var old = DateOnly.FromDateTime(DateTime.UtcNow - StandaloneSingleAuditor.FollowUpGrace)
            .AddDays(-1);
        var verdict = await _sut.Audit(new[] { Single(1, "Wayfaring Stranger", old) }, learn: false);

        verdict.Should().BeEquivalentTo(new[] { 1L });
    }

    [Fact]
    public async Task A_single_just_inside_the_grace_period_is_still_withheld()
    {
        // The other side of the same boundary, so the fallback can't silently become "offer everything".
        Listing(1, "Wayfaring Stranger");

        var recent = DateOnly.FromDateTime(DateTime.UtcNow - StandaloneSingleAuditor.FollowUpGrace)
            .AddDays(1);
        var verdict = await _sut.Audit(new[] { Single(1, "Wayfaring Stranger", recent) }, learn: false);

        verdict.Should().BeEmpty();
    }

    [Fact]
    public async Task A_single_released_alongside_an_album_that_does_not_hold_it_is_offered()
    {
        // Same-day counts as followed up. By this point the album is known not to carry the song, which
        // makes the single a companion release rather than a trailer.
        var day = DaysAgo(500);
        Listing(1, "Wayfaring Stranger");
        Listing(2, "Small Things");

        var verdict = await _sut.Audit(new[]
        {
            Single(1, "Wayfaring Stranger", day),
            Record(2, "Noonday Dream", day),
        }, learn: false);

        verdict.Should().BeEquivalentTo(new[] { 1L });
    }

    [Fact]
    public async Task An_ep_counts_as_the_album_that_follows_a_single()
    {
        // The feed treats EPs as records, so the audit has to as well — otherwise an artist who only
        // releases EPs could never clear test 2.
        Listing(1, "Wayfaring Stranger");
        Listing(2, "Small Things");

        var verdict = await _sut.Audit(new[]
        {
            Single(1, "Wayfaring Stranger", DaysAgo(900)),
            Record(2, "The Burgh Island EP", DaysAgo(400), type: "ep"),
        }, learn: false);

        verdict.Should().BeEquivalentTo(new[] { 1L });
    }

    [Fact]
    public async Task A_single_whose_song_is_a_different_cut_of_an_album_track_is_withheld()
    {
        // The label trimmed the album version for radio and Deezer lists both verbatim. Same song, so
        // the single is redundant — see AlbumTitleMatcher.NormalizeTrack.
        Listing(1, "Small Things - Radio Edit");
        Listing(2, "Small Things", "Towing the Line");

        var verdict = await _sut.Audit(new[]
        {
            Single(1, "Small Things", DaysAgo(900)),
            Record(2, "Noonday Dream", DaysAgo(400)),
        }, learn: false);

        verdict.Should().BeEmpty();
    }

    [Fact]
    public async Task A_single_carrying_one_new_song_and_one_album_track_is_withheld()
    {
        // A companion release to the album, not a record of its own: offering it buys the album's song
        // a second time. Every track has to be uncovered, not just the lead.
        Listing(1, "Wayfaring Stranger", "Small Things");
        Listing(2, "Small Things", "Towing the Line");

        var verdict = await _sut.Audit(new[]
        {
            Single(1, "Wayfaring Stranger", DaysAgo(900)),
            Record(2, "Noonday Dream", DaysAgo(400)),
        }, learn: false);

        verdict.Should().BeEmpty();
    }

    [Fact]
    public async Task A_single_named_after_a_record_is_withheld_without_reading_any_track_listing()
    {
        // The commonest teaser of all — the title track, out ahead of the album named after it. Caught
        // on the title alone so it costs no Deezer call, which is the point: no listing is seeded here
        // and the fetch is allowed, yet nothing is fetched.
        var verdict = await _sut.Audit(new[]
        {
            Single(1, "Noonday Dream", DaysAgo(900)),
            Record(2, "Noonday Dream", DaysAgo(400)),
        }, learn: true);

        verdict.Should().BeEmpty();
        await _deezer.DidNotReceive().GetAlbumTracks(Arg.Any<long>());
    }

    [Fact]
    public async Task An_undated_single_is_withheld()
    {
        // Deezer dates some old releases with a bare year or nothing at all. There is no way to place
        // such a single against the records around it, and a year would put a January single behind a
        // November album on nothing but sort order.
        Listing(1, "Wayfaring Stranger");
        Listing(2, "Small Things");

        var verdict = await _sut.Audit(new[]
        {
            Single(1, "Wayfaring Stranger", null),
            Record(2, "Noonday Dream", DaysAgo(400)),
        }, learn: false);

        verdict.Should().BeEmpty();
    }

    [Fact]
    public async Task An_album_whose_track_listing_is_unknown_cannot_clear_a_single()
    {
        // The unproven case. The album might well hold the song; we just haven't looked. Reading that as
        // "holds nothing" is exactly how a rate-limited sweep would start offering teasers.
        Listing(1, "Small Things");

        var verdict = await _sut.Audit(new[]
        {
            Single(1, "Small Things", DaysAgo(900)),
            Record(2, "Noonday Dream", DaysAgo(400)),
        }, learn: false);

        // The single's own listing is known and the album's is not, so nothing is proven either way —
        // and an unproven single is withheld.
        verdict.Should().BeEmpty();
    }

    [Fact]
    public async Task A_single_whose_own_track_listing_is_unknown_is_withheld()
    {
        // Symmetrically: the audit turns on knowing which songs the single is selling.
        Listing(2, "Small Things");

        var verdict = await _sut.Audit(new[]
        {
            Single(1, "Wayfaring Stranger", DaysAgo(900)),
            Record(2, "Noonday Dream", DaysAgo(400)),
        }, learn: false);

        verdict.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_is_fetched_for_an_artist_whose_singles_are_all_too_recent_to_judge()
    {
        // The cost control that makes this affordable nightly: test 2 and the title check need no track
        // listings, so an artist with nothing to judge yet spends no Deezer calls at all.
        await _sut.Audit(new[]
        {
            Single(1, "Wayfaring Stranger", DaysAgo(10)),
            Record(2, "Every Kingdom", DaysAgo(2000)),
        }, learn: true);

        await _deezer.DidNotReceive().GetAlbumTracks(Arg.Any<long>());
    }

    [Fact]
    public async Task Nothing_is_fetched_for_an_artist_with_no_singles()
    {
        await _sut.Audit(new[]
        {
            Record(1, "Every Kingdom", DaysAgo(2000)),
            Record(2, "Noonday Dream", DaysAgo(400)),
        }, learn: true);

        await _deezer.DidNotReceive().GetAlbumTracks(Arg.Any<long>());
    }

    [Fact]
    public async Task Learning_fetches_the_missing_listings_and_memoises_them()
    {
        _deezer.GetAlbumTracks(1).Returns(new[] { new DeezerTrack { title = "Wayfaring Stranger" } });
        _deezer.GetAlbumTracks(2).Returns(new[] { new DeezerTrack { title = "Small Things" } });

        var verdict = await _sut.Audit(new[]
        {
            Single(1, "Wayfaring Stranger", DaysAgo(900)),
            Record(2, "Noonday Dream", DaysAgo(400)),
        }, learn: true);

        verdict.Should().BeEquivalentTo(new[] { 1L });
        _tracks.Items.Should().ContainKey(1).And.ContainKey(2);
    }

    [Fact]
    public async Task Without_learning_nothing_is_fetched_even_when_the_memo_is_cold()
    {
        // The drill-down path, in front of a click. An unproven single is withheld until the sweep has
        // paid for the listings — the behaviour that existed before singles were offered at all.
        _deezer.GetAlbumTracks(Arg.Any<long>())
            .Returns(new[] { new DeezerTrack { title = "Wayfaring Stranger" } });

        var verdict = await _sut.Audit(new[]
        {
            Single(1, "Wayfaring Stranger", DaysAgo(900)),
            Record(2, "Noonday Dream", DaysAgo(400)),
        }, learn: false);

        verdict.Should().BeEmpty();
        await _deezer.DidNotReceive().GetAlbumTracks(Arg.Any<long>());
    }

    [Fact]
    public async Task An_unanswered_track_listing_is_not_memoised_as_an_empty_album()
    {
        // GetAlbumTracks returns empty for a failed call as well as a real miss, and a release out of a
        // discography listing has tracks by construction. Memoising the empty result would teach the
        // audit for good that this album holds nothing — which is the answer that lets teasers through.
        _deezer.GetAlbumTracks(1).Returns(new[] { new DeezerTrack { title = "Small Things" } });
        _deezer.GetAlbumTracks(2).Returns(Array.Empty<DeezerTrack>());

        await _sut.Audit(new[]
        {
            Single(1, "Small Things", DaysAgo(900)),
            Record(2, "Noonday Dream", DaysAgo(400)),
        }, learn: true);

        _tracks.Items.Should().ContainKey(1).And.NotContainKey(2);
    }

    [Fact]
    public async Task A_compilation_is_neither_a_candidate_nor_an_album_that_covers_one()
    {
        // Compilations are synced but are not records the feed reasons about. A greatest-hits repackage
        // holding the song says nothing about whether the single is standalone — it is the same
        // redundancy in the other direction — and the compilation itself is never offered.
        Listing(1, "Wayfaring Stranger");
        Listing(2, "Small Things");
        Listing(3, "Wayfaring Stranger", "Small Things");

        var verdict = await _sut.Audit(new[]
        {
            Single(1, "Wayfaring Stranger", DaysAgo(900)),
            Record(2, "Noonday Dream", DaysAgo(400)),
            Record(3, "The Best Of", DaysAgo(100), type: "compilation"),
        }, learn: false);

        verdict.Should().BeEquivalentTo(new[] { 1L });
    }
}
