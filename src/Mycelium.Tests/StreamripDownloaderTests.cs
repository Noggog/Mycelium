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
            album="$folder/Food Pyramid/New Omni-Directional Healing Techniques"
            mkdir -p "$album"
            touch "$album/cover.jpg"
            plan="$(dirname "$0")/plans/$quality"
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

    /// <summary>How many tracks Deezer says the album has; 0 stands in for a failed lookup.</summary>
    private void DeezerReports(int trackCount) =>
        _deezer.GetAlbumTracks(AlbumId).Returns(Enumerable.Range(0, trackCount)
            .Select(i => new DeezerTrack { id = i, title = $"Track {i}" }).ToArray());

    /// <summary>Null means the production default chain; an empty list means no fallback at all.</summary>
    private StreamripDownloader Sut(IReadOnlyList<string>? fallbacks = null) =>
        new(new DownloaderConfig(
                DownloadDir: _library, RipBinary: _rip, Quality: "2",
                FallbackQualities: fallbacks ?? new[] { "1", "0" },
                Codec: "", BatchSize: 1, ItemDelay: TimeSpan.Zero, BatchInterval: TimeSpan.Zero,
                DownloadTimeout: TimeSpan.FromMinutes(1), SettleInterval: TimeSpan.Zero,
                SettleWindow: TimeSpan.Zero),
            _deezer,
            NullLogger<StreamripDownloader>.Instance);

    private static PurchaseItem Item() =>
        new("id", FeedKind.MissingAlbum, new ArtistKey("Food Pyramid"),
            "New Omni-Directional Healing Techniques", null, 0, Array.Empty<string>(),
            PurchaseStatus.Queued, DateTimeOffset.UtcNow, null, AlbumId);

    private string[] Landed() =>
        DownloadStaging.AudioFiles(_library).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

    // ---- The reported bug: Deezer serves this album as MP3 only, so the FLAC pass got nothing ----

    [Fact]
    public async Task An_album_with_no_lossless_falls_back_to_mp3_instead_of_reporting_success_with_no_files()
    {
        if (Unsupported) { return; }
        DeezerReports(4);
        // No plan for quality 2: the FLAC pass leaves only cover art, exiting 0 all the same.
        Plan("1", "02. Prana Focus.mp3", "03. Manufracture.mp3",
            "04. Advanced Cool Down.mp3", "05. Shambhala.mp3");

        (await Sut().Request(Item())).Should().BeTrue();

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

        (await Sut().Request(Item())).Should().BeTrue();

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

        (await Sut().Request(Item())).Should().BeTrue();

        Landed().Should().Equal("01. One.mp3", "02. Two.mp3");
    }

    [Fact]
    public async Task An_empty_fallback_chain_leaves_the_preferred_pass_as_the_only_attempt()
    {
        if (Unsupported) { return; }
        DeezerReports(4);
        Plan("1", "02. Prana Focus.mp3");

        (await Sut(Array.Empty<string>()).Request(Item())).Should().BeFalse();

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

        (await Sut().Request(Item())).Should().BeTrue();

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

        (await Sut().Request(Item())).Should().BeTrue();

        Landed().Should().Equal("02. Prana Focus.flac", "03. Manufracture.flac");
    }

    // ---- Nothing at any quality is a failure, not a silent success ----

    [Fact]
    public async Task An_album_with_no_tracks_at_any_quality_fails_and_leaves_the_library_untouched()
    {
        if (Unsupported) { return; }
        DeezerReports(4);

        (await Sut().Request(Item())).Should().BeFalse();

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
        (await Sut().Request(Item())).Should().BeTrue();

        Landed().Should().HaveCount(3);
    }

    // ---- Deezer's track count is best-effort; losing it must not break the fallback ----

    [Fact]
    public async Task When_the_deezer_track_count_is_unavailable_an_empty_pass_still_triggers_the_fallback()
    {
        if (Unsupported) { return; }
        DeezerReports(0);
        Plan("1", "02. Prana Focus.mp3");

        (await Sut().Request(Item())).Should().BeTrue();

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

        (await Sut().Request(Item())).Should().BeTrue();

        Landed().Should().Equal("01. Sun Ra.flac", "02. Prana Focus.flac");
    }
}
