using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Download;
using Mycelium.Deezer.Models;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// Drives the real downloader against a stand-in <c>rip</c> that reproduces the behaviour this class
/// exists to defend against: streamrip gathers an album's tracks with <c>return_exceptions=True</c>,
/// logs the failures, and <b>exits 0 anyway</b>. So the fake always succeeds as a process, and each
/// test varies only which files it leaves behind per quality — which is exactly the signal the
/// downloader has to read instead of the exit code.
///
/// A real subprocess rather than a mock: the staging/verify/promote path is process plumbing plus
/// filesystem work, and stubbing either out would leave the interesting part untested.
/// </summary>
public class StreamripDownloaderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"mycelium-streamrip-tests-{Guid.NewGuid():N}");

    private readonly string _library;
    private readonly string _plans;
    private readonly string _rip;
    private readonly IDeezerApi _deezer = Substitute.For<IDeezerApi>();

    private const long AlbumId = 6181136;

    // The shell fake is POSIX; the download host is Linux (see Dockerfile), so skipping on Windows
    // costs nothing real.
    private static bool Unsupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public StreamripDownloaderTests()
    {
        _library = Path.Combine(_root, "music");
        _plans = Path.Combine(_root, "plans");
        _rip = Path.Combine(_root, "rip");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_plans);

        if (Unsupported)
        {
            return;
        }

        // Reads --folder/--quality, lays down whatever plans/<quality> lists, and always exits 0.
        // Cover art is written unconditionally: a quality with no plan yields a folder containing
        // nothing but cover.jpg, which is precisely what a FLAC-only request against an album Deezer
        // has no lossless for produces.
        File.WriteAllText(_rip,
            """
            #!/bin/sh
            folder=""
            quality=""
            while [ $# -gt 0 ]; do
              case "$1" in
                --folder) folder="$2"; shift 2 ;;
                --quality) quality="$2"; shift 2 ;;
                *) shift ;;
              esac
            done
            plandir="$(dirname "$0")/plans"
            # One line per invocation, so a test can assert how many quality passes actually ran.
            echo "$quality" >> "$plandir/calls"
            # Credential failure: streamrip lets the exception escape, so it prints a traceback naming
            # the exception type and exits 1 — before writing anything at all, at any quality.
            if [ -f "$plandir/credfail" ]; then
              echo "Traceback (most recent call last)"
              echo "  File streamrip/client/deezer.py line 53 in login"
              cat "$plandir/credfail"
              exit 1
            fi
            album="$folder/Food Pyramid/New Omni-Directional Healing Techniques"
            mkdir -p "$album"
            touch "$album/cover.jpg"
            plan="$plandir/$quality"
            if [ -f "$plan" ]; then
              while IFS= read -r f; do
                [ -n "$f" ] && touch "$album/$f"
              done < "$plan"
            fi
            exit 0

            """.ReplaceLineEndings("\n"));
        File.SetUnixFileMode(_rip,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose() => DownloadStaging.TryDelete(_root);

    /// <summary>Declares which files the fake produces at a given <c>--quality</c>.</summary>
    private void Plan(string quality, params string[] files) =>
        File.WriteAllLines(Path.Combine(_plans, quality), files);

    /// <summary>
    /// Makes every pass fail the way a rejected credential does: an uncaught Python exception on
    /// stdout and exit 1, with no files written. <paramref name="exception"/> is the type name
    /// streamrip lets escape — the only thing separating "bad ARL" from "no ARL", since the exit code
    /// is 1 for both.
    /// </summary>
    private void CredentialFailure(string exception) =>
        File.WriteAllText(Path.Combine(_plans, "credfail"), exception);

    /// <summary>The <c>--quality</c> of each streamrip invocation, in order.</summary>
    private string[] Calls()
    {
        var path = Path.Combine(_plans, "calls");
        return File.Exists(path)
            ? File.ReadAllLines(path).Where(l => l.Length > 0).ToArray()
            : Array.Empty<string>();
    }

    /// <summary>How many tracks Deezer says the album has; 0 stands in for a failed lookup.</summary>
    private void DeezerReports(int trackCount) =>
        _deezer.GetAlbumTracks(AlbumId).Returns(Enumerable.Range(0, trackCount)
            .Select(i => new DeezerTrack { id = i, title = $"Track {i}" }).ToArray());

    /// <summary>Null means the production default chain; an empty list means no fallback at all.</summary>
    private StreamripDownloader Sut(
        IReadOnlyList<string>? fallbacks = null, string configuredQuality = "2") =>
        new(new DownloaderConfig(
                DownloadDir: _library, RipBinary: _rip, Quality: configuredQuality,
                FallbackQualities: fallbacks ?? new[] { "1", "0" },
                Codec: "", BatchSize: 1, ItemDelay: TimeSpan.Zero, BatchInterval: TimeSpan.Zero,
                DownloadTimeout: TimeSpan.FromMinutes(1), SettleInterval: TimeSpan.Zero,
                SettleWindow: TimeSpan.Zero, FastSettleInterval: TimeSpan.Zero,
                FastSettleWindow: TimeSpan.Zero),
            _deezer,
            // These cases cover gap downloads, which never reach the swap. The upgrade path has its
            // own tests against a real path map and trash directory.
            new UpgradeSwap(
                Substitute.For<ILibraryQuery>(), Substitute.For<IArtistCatalogRepo>(),
                new LibraryPathMap(null), new LibraryTrash(NullLogger<LibraryTrash>.Instance),
                NullLogger<UpgradeSwap>.Instance),
            NullLogger<StreamripDownloader>.Instance);

    /// <summary>
    /// A queued album. <paramref name="target"/> is the quality the reconcile worked out from whoever
    /// liked it; null is a row from before tiers existed (or a manual paste), which uses the
    /// configured quality.
    /// </summary>
    private static PurchaseItem Item(AudioQuality? target = null) =>
        new("id", FeedKind.MissingAlbum, new ArtistKey("Food Pyramid"),
            "New Omni-Directional Healing Techniques", null, 0, Array.Empty<string>(),
            PurchaseStatus.Queued, DateTimeOffset.UtcNow, null, AlbumId,
            TargetQuality: target);

    private string[] Landed() =>
        DownloadStaging.AudioFiles(_library).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

    // ---- Credential failures: systemic, so they must be named and must not walk the ladder ----

    [Fact]
    public async Task A_rejected_arl_is_reported_as_such_and_does_not_walk_the_quality_ladder()
    {
        if (Unsupported) { return; }
        DeezerReports(4);
        // Deezer rejected the session token. streamrip raises AuthenticationError from login(), before
        // it has looked at the album — so no quality can help, and the two fallback passes would only
        // reproduce the same traceback. Walking them wastes time and buries the reason in triplicate.
        CredentialFailure("AuthenticationError");

        var outcome = await Sut().Request(Item());

        outcome.Accepted.Should().BeFalse();
        outcome.Failure.Should().Be(DownloadFailure.DeezerAuth);
        outcome.Failure.IsSystemic().Should().BeTrue();
        Calls().Should().Equal("2");
    }

    [Fact]
    public async Task A_missing_arl_is_told_apart_from_a_rejected_one()
    {
        if (Unsupported) { return; }
        DeezerReports(4);
        // Same systemic shape, different fix: nothing is configured at all, so the user needs
        // first-time setup rather than a refresh. streamrip raises a distinct exception for it.
        CredentialFailure("MissingCredentialsError");

        var outcome = await Sut().Request(Item());

        outcome.Failure.Should().Be(DownloadFailure.DeezerCredentialsMissing);
        outcome.Failure.IsSystemic().Should().BeTrue();
        Calls().Should().Equal("2");
    }

    [Fact]
    public async Task An_album_deezer_serves_no_tracks_for_is_not_blamed_on_the_credential()
    {
        if (Unsupported) { return; }
        DeezerReports(4);
        // The other zero-track ending: streamrip logs in fine and exits 0, but every track was
        // unavailable (geo-block, pulled master). That IS worth a retry and must not raise the
        // "downloads are blocked" banner, so it has to classify differently from an auth failure.
        var outcome = await Sut().Request(Item());

        outcome.Accepted.Should().BeFalse();
        outcome.Failure.Should().Be(DownloadFailure.NoTracksAvailable);
        outcome.Failure.IsSystemic().Should().BeFalse();
        // No credential problem, so the full ladder is still walked looking for a servable format.
        Calls().Should().Equal("2", "1", "0");
    }

    // ---- The reported bug: Deezer serves this album as MP3 only, so the FLAC pass got nothing ----

    [Fact]
    public async Task An_album_with_no_lossless_falls_back_to_mp3_instead_of_reporting_success_with_no_files()
    {
        if (Unsupported) { return; }
        DeezerReports(4);
        // No plan for quality 2: the FLAC pass leaves only cover art, exiting 0 all the same.
        Plan("1", "02. Prana Focus.mp3", "03. Manufracture.mp3",
            "04. Advanced Cool Down.mp3", "05. Shambhala.mp3");

        (await Sut().Request(Item())).Accepted.Should().BeTrue();

        Landed().Should().Equal(
            "02. Prana Focus.mp3", "03. Manufracture.mp3",
            "04. Advanced Cool Down.mp3", "05. Shambhala.mp3");
    }

    // ---- The real Food Pyramid case: Deezer's formats vary per track, so one downgrade isn't enough ----

    [Fact]
    public async Task An_album_whose_tracks_have_different_best_formats_walks_the_whole_quality_chain()
    {
        if (Unsupported) { return; }
        DeezerReports(4);
        // No FLAC for this album at all; exactly one track has a 320 master; the rest are 128 only.
        // Stopping after quality 1 — as a single fallback does — abandons three tracks, because
        // streamrip's own retry targets a CDN Deezer has retired.
        Plan("1", "04. Advanced Cool Down.mp3");
        Plan("0", "02. Prana Focus.mp3", "03. Manufracture.mp3",
            "04. Advanced Cool Down.mp3", "05. Shambhala.mp3");

        (await Sut().Request(Item())).Accepted.Should().BeTrue();

        Landed().Should().Equal(
            "02. Prana Focus.mp3", "03. Manufracture.mp3",
            "04. Advanced Cool Down.mp3", "05. Shambhala.mp3");
    }

    [Fact]
    public async Task The_chain_stops_as_soon_as_the_album_is_whole()
    {
        if (Unsupported) { return; }
        DeezerReports(2);
        Plan("1", "01. One.mp3", "02. Two.mp3");
        // Quality 0 would overwrite both with different files; reaching it at all is the bug.
        Plan("0", "01. One.mp3", "02. Two.mp3", "99. Should Never Appear.mp3");

        (await Sut().Request(Item())).Accepted.Should().BeTrue();

        Landed().Should().Equal("01. One.mp3", "02. Two.mp3");
    }

    [Fact]
    public async Task An_empty_fallback_chain_leaves_the_preferred_pass_as_the_only_attempt()
    {
        if (Unsupported) { return; }
        DeezerReports(4);
        Plan("1", "02. Prana Focus.mp3");

        (await Sut(Array.Empty<string>()).Request(Item())).Accepted.Should().BeFalse();

        Landed().Should().BeEmpty();
    }

    // ---- The mixed album: lossless tracks must survive the fallback pass ----

    [Fact]
    public async Task A_partly_lossless_album_keeps_its_flac_and_takes_mp3_only_for_the_missing_tracks()
    {
        if (Unsupported) { return; }
        DeezerReports(4);
        Plan("2", "02. Prana Focus.flac", "03. Manufracture.flac", "04. Advanced Cool Down.flac");
        Plan("1", "02. Prana Focus.mp3", "03. Manufracture.mp3",
            "04. Advanced Cool Down.mp3", "05. Shambhala.mp3");

        (await Sut().Request(Item())).Accepted.Should().BeTrue();

        Landed().Should().Equal(
            "02. Prana Focus.flac", "03. Manufracture.flac",
            "04. Advanced Cool Down.flac", "05. Shambhala.mp3");
    }

    [Fact]
    public async Task A_fully_lossless_album_is_promoted_without_a_fallback_pass()
    {
        if (Unsupported) { return; }
        DeezerReports(2);
        Plan("2", "02. Prana Focus.flac", "03. Manufracture.flac");
        // Present but never wanted — if the fallback ran, these would show up in the library.
        Plan("1", "02. Prana Focus.mp3", "03. Manufracture.mp3");

        (await Sut().Request(Item())).Accepted.Should().BeTrue();

        Landed().Should().Equal("02. Prana Focus.flac", "03. Manufracture.flac");
    }

    // ---- Nothing at any quality is a failure, not a silent success ----

    [Fact]
    public async Task An_album_with_no_tracks_at_any_quality_fails_and_leaves_the_library_untouched()
    {
        if (Unsupported) { return; }
        DeezerReports(4);

        (await Sut().Request(Item())).Accepted.Should().BeFalse();

        Landed().Should().BeEmpty();
        // Not even the cover art reaches the library — a folder holding only artwork is the exact
        // droppings this staging step exists to keep out of Plex.
        Directory.EnumerateFileSystemEntries(_library).Should().BeEmpty();
    }

    [Fact]
    public async Task A_track_Deezer_will_not_serve_at_any_quality_still_promotes_the_rest()
    {
        if (Unsupported) { return; }
        DeezerReports(4);
        Plan("2", "02. Prana Focus.flac", "03. Manufracture.flac", "04. Advanced Cool Down.flac");
        Plan("1", "02. Prana Focus.mp3", "03. Manufracture.mp3", "04. Advanced Cool Down.mp3");

        // 3 of 4 is logged as a partial promotion, but a geo-blocked track is not a reason to
        // withhold the album — and no quality would fix it, so failing would just retry forever.
        (await Sut().Request(Item())).Accepted.Should().BeTrue();

        Landed().Should().HaveCount(3);
    }

    // ---- Deezer's track count is best-effort; losing it must not break the fallback ----

    [Fact]
    public async Task When_the_deezer_track_count_is_unavailable_an_empty_pass_still_triggers_the_fallback()
    {
        if (Unsupported) { return; }
        DeezerReports(0);
        Plan("1", "02. Prana Focus.mp3");

        (await Sut().Request(Item())).Accepted.Should().BeTrue();

        Landed().Should().Equal("02. Prana Focus.mp3");
    }

    // ---- Housekeeping ----

    [Fact]
    public async Task The_staging_directory_is_cleaned_up_afterwards()
    {
        if (Unsupported) { return; }
        DeezerReports(1);
        Plan("2", "02. Prana Focus.flac");

        await Sut().Request(Item());

        Directory.Exists(Path.Combine(_library, DownloadStaging.StagingFolder)).Should().BeFalse();
    }

    [Fact]
    public async Task A_second_album_merges_into_an_artist_folder_the_library_already_has()
    {
        if (Unsupported) { return; }
        var existing = Path.Combine(_library, "Food Pyramid", "Ecstasy & Refreshment");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "01. Sun Ra.flac"), "x");

        DeezerReports(1);
        Plan("2", "02. Prana Focus.flac");

        (await Sut().Request(Item())).Accepted.Should().BeTrue();

        Landed().Should().Equal("01. Sun Ra.flac", "02. Prana Focus.flac");
    }

    // ---- Per-row quality: what a user's tier actually makes streamrip fetch ----

    [Fact]
    public async Task A_lossy_row_is_fetched_at_320_and_never_reaches_for_flac()
    {
        if (Unsupported) { return; }
        DeezerReports(2);
        Plan("1", "01. A.mp3", "02. B.mp3");

        var outcome = await Sut().Request(Item(AudioQuality.Lossy));

        outcome.Accepted.Should().BeTrue();
        // The whole point of the tier: a lossy user's like must not pull down the copy that costs
        // 3x the disk, even though the deployment is configured for FLAC.
        Calls().Should().Equal("1");
    }

    [Fact]
    public async Task A_lossy_row_still_walks_down_when_320_comes_up_short()
    {
        if (Unsupported) { return; }
        DeezerReports(2);
        Plan("1", "01. A.mp3");
        Plan("0", "01. A.mp3", "02. B.mp3");

        await Sut().Request(Item(AudioQuality.Lossy));

        // Deezer's per-track gaps exist at every tier, not just at FLAC — the ladder still matters
        // for a lossy row, it just starts a rung lower.
        Calls().Should().Equal("1", "0");
    }

    [Fact]
    public async Task A_lossless_row_starts_at_flac()
    {
        if (Unsupported) { return; }
        DeezerReports(2);
        Plan("2", "01. A.flac", "02. B.flac");

        await Sut().Request(Item(AudioQuality.Lossless));

        Calls().Should().Equal("2");
    }

    [Fact]
    public async Task A_row_with_no_target_downloads_exactly_as_it_always_did()
    {
        if (Unsupported) { return; }
        DeezerReports(2);
        Plan("2", "01. A.flac", "02. B.flac");

        // Rows written before tiers existed, and manual pastes no rating stands behind.
        await Sut().Request(Item(target: null));

        Calls().Should().Equal("2");
    }

    [Fact]
    public async Task A_target_above_the_configured_quality_is_clamped_to_it()
    {
        if (Unsupported) { return; }
        DeezerReports(2);
        Plan("1", "01. A.mp3", "02. B.mp3");

        // DEEZER_QUALITY is the operator's ceiling, not just a default for the untagged: a
        // deployment deliberately pinned to 320 must not be overridden by marking a user lossless.
        await Sut(configuredQuality: "1").Request(Item(AudioQuality.Lossless));

        Calls().Should().Equal("1");
    }
}
