using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Download;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The pre-check that stops an upgrade being offered when Deezer has nothing better to give.
///
/// What these tests are really pinning down is the <em>absence</em> of an answer. Deezer's per-track
/// FLAC size is scoped to the asking account, so an expired credential doesn't fail — it reports a
/// confident "no FLAC" for the entire catalogue. A pre-check that believed it would snooze every
/// upgradeable album in the library off one dead cookie, silently, with nothing erroring to show for
/// it. So the rule under test is: only a positive finding may suppress anything, and every other
/// outcome has to leave the album exactly as it would have been with no pre-check at all.
/// </summary>
public class UpgradeAvailabilityTests
{
    private const string Artist = "milo";
    private const string Album = "so the flies don't come";
    private const long DeezerId = 42;

    private readonly IDeezerQualityProbe _probe = Substitute.For<IDeezerQualityProbe>();
    private readonly StreamripArlStore _arl =
        Substitute.For<StreamripArlStore>(NullLogger<StreamripArlStore>.Instance);
    private readonly IAlbumBlockRepo _blocks = Substitute.For<IAlbumBlockRepo>();
    private readonly UpgradeAvailability _sut;

    public UpgradeAvailabilityTests()
    {
        _arl.Read().Returns("a-live-token");
        _blocks.GetAll().Returns(Array.Empty<AlbumBlock>());
        _sut = new UpgradeAvailability(
            _probe, _arl, _blocks, NullLogger<UpgradeAvailability>.Instance);
    }

    /// <summary>An upgrade row: a record the library holds, but only as MP3.</summary>
    private static MissingAlbum Upgrade(string album = Album, long id = DeezerId) =>
        new(new ArtistKey(Artist), new AlbumKey(album), null, id,
            OwnedQuality: AudioQuality.Lossy);

    /// <summary>A gap row: a record the library doesn't have at all.</summary>
    private static MissingAlbum Gap(string album = "the other one", long id = 99) =>
        new(new ArtistKey(Artist), new AlbumKey(album), null, id);

    private void Answers(DeezerQualityVerdict verdict, int tracks = 12, int lossless = 12) =>
        _probe.Probe(Arg.Any<string?>(), Arg.Any<long>())
            .Returns(new DeezerAlbumQuality(verdict, tracks, lossless));

    [Fact]
    public async Task An_album_Deezer_has_no_lossless_copy_of_is_dropped()
    {
        Answers(DeezerQualityVerdict.LossyOnly, tracks: 12, lossless: 0);

        var kept = await _sut.Confirm(new[] { Upgrade() });

        kept.Should().BeEmpty();
    }

    [Fact]
    public async Task Dropping_it_records_the_verdict_where_both_readers_look()
    {
        Answers(DeezerQualityVerdict.LossyOnly, tracks: 12, lossless: 0);

        await _sut.Confirm(new[] { Upgrade() });

        // Persisted rather than merely filtered, because the feed is not the only route to an upgrade
        // download: PurchaseService.Reconcile derives the buy list from liked albums and owned quality
        // without consulting these rows, so a dropped row alone would silence the card while reconcile
        // went on queueing the album for ever.
        await _blocks.Received(1).Add(Arg.Is<AlbumBlock>(b =>
            b.Artist == Artist
            && b.Album == Album
            && b.Scope == AlbumBlockScope.Upgrade
            && b.RetryAfter != null));
    }

    [Fact]
    public async Task The_verdict_is_a_snooze_rather_than_a_foreclosure()
    {
        Answers(DeezerQualityVerdict.LossyOnly, tracks: 12, lossless: 0);

        await _sut.Confirm(new[] { Upgrade() });

        // A catalogue can gain a lossless master later, so "Deezer hasn't got one" must lapse. Matches
        // the stamp DownloadService writes on the same finding reached the expensive way.
        await _blocks.Received().Add(Arg.Is<AlbumBlock>(b =>
            b.RetryAfter > DateTimeOffset.UtcNow.AddDays(179)
            && b.RetryAfter < DateTimeOffset.UtcNow.AddDays(181)));
    }

    [Fact]
    public async Task An_album_Deezer_has_in_lossless_is_offered()
    {
        Answers(DeezerQualityVerdict.LosslessAvailable);

        var kept = await _sut.Confirm(new[] { Upgrade() });

        kept.Should().ContainSingle();
        await _blocks.DidNotReceive().Add(Arg.Any<AlbumBlock>());
    }

    [Fact]
    public async Task A_partly_lossless_album_is_still_offered()
    {
        // Deezer's catalogue is patchy per track. Ten FLAC plus two MP3 still reads lossless under the
        // majority rule and is genuinely better than the twelve MP3 it replaces — refusing it here
        // would throw away the common case (see QUALITY-TIERS.md, "the fallback ladder stays ON").
        Answers(DeezerQualityVerdict.LosslessAvailable, tracks: 12, lossless: 10);

        var kept = await _sut.Confirm(new[] { Upgrade() });

        kept.Should().ContainSingle();
    }

    [Fact]
    public async Task A_barely_lossless_album_is_treated_as_nothing_better()
    {
        // Two lossless tracks in twelve. The ladder fills the other ten at 320, so what lands reads as
        // *lossy* under the majority rule and loses UpgradeSwap's strictly-better gate — the download
        // would be spent to leave the MP3 exactly where it was. Seen in the wild on the second album
        // ever probed against the real gateway, so this is not a hypothetical.
        Answers(DeezerQualityVerdict.LosslessAvailable, tracks: 12, lossless: 2);

        var kept = await _sut.Confirm(new[] { Upgrade() });

        kept.Should().BeEmpty();
        await _blocks.Received(1).Add(Arg.Is<AlbumBlock>(b =>
            b.Scope == AlbumBlockScope.Upgrade && b.RetryAfter != null));
    }

    [Fact]
    public async Task An_evenly_split_album_is_offered()
    {
        // Six and six. The majority rule sends ties to lossless, so this clears the gate — and the
        // pre-check has to agree with the gate rather than second-guess it, or it starts suppressing
        // upgrades that would in fact have gone through.
        Answers(DeezerQualityVerdict.LosslessAvailable, tracks: 12, lossless: 6);

        var kept = await _sut.Confirm(new[] { Upgrade() });

        kept.Should().ContainSingle();
        await _blocks.DidNotReceive().Add(Arg.Any<AlbumBlock>());
    }

    [Fact]
    public async Task An_unconfirmable_album_is_offered_anyway()
    {
        Answers(DeezerQualityVerdict.Unknown, tracks: 0, lossless: 0);

        var kept = await _sut.Confirm(new[] { Upgrade() });

        // The load-bearing case. A dead ARL, an account without lossless, Deezer unreachable — all
        // arrive here, and all must degrade to the behaviour that existed before the pre-check: offer
        // it, let the download find out, let DownloadService record the snooze.
        kept.Should().ContainSingle();
        await _blocks.DidNotReceive().Add(Arg.Any<AlbumBlock>());
    }

    [Fact]
    public async Task With_no_credential_configured_nothing_is_probed_at_all()
    {
        _arl.Read().Returns((string?)null);

        var kept = await _sut.Confirm(new[] { Upgrade() });

        kept.Should().ContainSingle();
        await _probe.DidNotReceive().Probe(Arg.Any<string?>(), Arg.Any<long>());
    }

    [Fact]
    public async Task A_gap_is_never_probed()
    {
        Answers(DeezerQualityVerdict.LossyOnly, tracks: 12, lossless: 0);

        var kept = await _sut.Confirm(new[] { Gap() });

        // The question is only ever asked about replacing a record we hold. An album we don't have is
        // worth offering in whatever quality Deezer has it.
        kept.Should().ContainSingle();
        await _probe.DidNotReceive().Probe(Arg.Any<string?>(), Arg.Any<long>());
    }

    [Fact]
    public async Task An_album_already_carrying_a_verdict_is_not_probed_again()
    {
        _blocks.GetAll().Returns(new[]
        {
            new AlbumBlock(Artist, Album, null, AlbumBlockScope.Upgrade,
                DateTimeOffset.UtcNow.AddDays(90)),
        });

        await _sut.Confirm(new[] { Upgrade() });

        // One probe per album ever, not one per sweep — which is what keeps a per-album call on an
        // undocumented endpoint down to a handful a night.
        await _probe.DidNotReceive().Probe(Arg.Any<string?>(), Arg.Any<long>());
    }

    [Fact]
    public async Task A_lapsed_verdict_becomes_a_candidate_again()
    {
        _blocks.GetAll().Returns(new[]
        {
            new AlbumBlock(Artist, Album, null, AlbumBlockScope.Upgrade,
                DateTimeOffset.UtcNow.AddDays(-1)),
        });
        Answers(DeezerQualityVerdict.LosslessAvailable);

        var kept = await _sut.Confirm(new[] { Upgrade() });

        // Same expiry test the feed and reconcile apply, so an album returns to probing at the same
        // moment it returns to being offerable.
        kept.Should().ContainSingle();
        await _probe.Received(1).Probe(Arg.Any<string?>(), Arg.Any<long>());
    }

    [Fact]
    public async Task A_release_block_does_not_count_as_an_upgrade_verdict()
    {
        _blocks.GetAll().Returns(new[]
        {
            new AlbumBlock(Artist, Album, null, AlbumBlockScope.Release),
        });
        Answers(DeezerQualityVerdict.LosslessAvailable);

        await _sut.Confirm(new[] { Upgrade() });

        // "Don't carry this record" and "keep the copy we have" are different verdicts on purpose;
        // reading one as the other is how an owned album starts rendering as blocked everywhere.
        await _probe.Received(1).Probe(Arg.Any<string?>(), Arg.Any<long>());
    }

    [Fact]
    public async Task Gaps_pass_through_alongside_a_dropped_upgrade()
    {
        _probe.Probe(Arg.Any<string?>(), DeezerId)
            .Returns(new DeezerAlbumQuality(DeezerQualityVerdict.LossyOnly, 12, 0));

        var kept = await _sut.Confirm(new[] { Upgrade(), Gap() });

        kept.Should().ContainSingle().Which.IsUpgrade.Should().BeFalse();
    }
}
