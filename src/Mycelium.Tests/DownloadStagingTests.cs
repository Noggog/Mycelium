using FluentAssertions;
using Mycelium.Backend.Services.Download;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The staging layer is what turns "streamrip exited 0" into "these tracks actually landed", so its
/// merge and promote steps are the part of the download path most worth pinning down: they decide
/// whether an 80%-lossless album keeps its FLAC, and whether a half-finished grab reaches Plex.
/// Everything here runs against a real temp directory — the code is pure filesystem work, and mocking
/// it away would test nothing.
/// </summary>
public class DownloadStagingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"mycelium-staging-tests-{Guid.NewGuid():N}");

    public DownloadStagingTests() => Directory.CreateDirectory(_root);

    public void Dispose() => DownloadStaging.TryDelete(_root);

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private void File_(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), "x");
    }

    private static string[] Names(string dir) =>
        DownloadStaging.AudioFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

    // ---- AudioFiles: only real tracks count toward "we got the album" ----

    [Fact]
    public void AudioFiles_IgnoresCoverArtAndOtherNonTracks()
    {
        var album = Dir("preferred", "Food Pyramid", "New Omni-Directional Healing Techniques");
        File_(album, "cover.jpg");
        File_(album, "album.nfo");
        File_(album, "02. Food Pyramid - Prana Focus.flac");

        Names(Dir("preferred")).Should().Equal("02. Food Pyramid - Prana Focus.flac");
    }

    [Fact]
    public void AudioFiles_OnMissingDirectory_IsEmpty()
    {
        DownloadStaging.AudioFiles(Path.Combine(_root, "never-created")).Should().BeEmpty();
    }

    // ---- Graft: the case that started this — a FLAC pass that grabbed nothing but the cover ----

    [Fact]
    public void Graft_WhenPreferredPassGotNoTracks_TakesTheFallbackTreeWholesale()
    {
        var preferredAlbum = Dir("preferred", "Food Pyramid", "New Omni-Directional Healing Techniques");
        File_(preferredAlbum, "cover.jpg");

        var fallbackAlbum = Dir("fallback", "Food Pyramid", "New Omni-Directional Healing Techniques");
        File_(fallbackAlbum, "02. Food Pyramid - Prana Focus.mp3");
        File_(fallbackAlbum, "03. Food Pyramid - Manufracture.mp3");

        var grafted = DownloadStaging.Graft(Dir("preferred"), Dir("fallback"));

        grafted.Should().Be(2);
        Names(Dir("preferred")).Should().Equal(
            "02. Food Pyramid - Prana Focus.mp3",
            "03. Food Pyramid - Manufracture.mp3");
    }

    // ---- Graft: the mixed album — lossless must survive ----

    [Fact]
    public void Graft_OnAMixedAlbum_KeepsFlacAndAddsMp3OnlyForTheMissingTracks()
    {
        var preferredAlbum = Dir("preferred", "Artist", "Album");
        File_(preferredAlbum, "01. Artist - One.flac");
        File_(preferredAlbum, "02. Artist - Two.flac");

        // The fallback pass re-fetches the whole album at MP3; only the gap may be taken from it.
        var fallbackAlbum = Dir("fallback", "Artist", "Album");
        File_(fallbackAlbum, "01. Artist - One.mp3");
        File_(fallbackAlbum, "02. Artist - Two.mp3");
        File_(fallbackAlbum, "03. Artist - Three.mp3");

        var grafted = DownloadStaging.Graft(Dir("preferred"), Dir("fallback"));

        grafted.Should().Be(1);
        Names(Dir("preferred")).Should().Equal(
            "01. Artist - One.flac",
            "02. Artist - Two.flac",
            "03. Artist - Three.mp3");
    }

    [Fact]
    public void Graft_WhenTheFallbackPassAddsNothing_LeavesThePreferredTreeAlone()
    {
        var preferredAlbum = Dir("preferred", "Artist", "Album");
        File_(preferredAlbum, "01. Artist - One.flac");
        var fallbackAlbum = Dir("fallback", "Artist", "Album");
        File_(fallbackAlbum, "01. Artist - One.mp3");

        DownloadStaging.Graft(Dir("preferred"), Dir("fallback")).Should().Be(0);
        Names(Dir("preferred")).Should().Equal("01. Artist - One.flac");
    }

    [Fact]
    public void Graft_WhenBothPassesGotNothing_ReportsNothingGrafted()
    {
        File_(Dir("preferred", "Artist", "Album"), "cover.jpg");

        DownloadStaging.Graft(Dir("preferred"), Dir("fallback")).Should().Be(0);
        DownloadStaging.AudioFiles(Dir("preferred")).Should().BeEmpty();
    }

    // streamrip's *default* folder_format embeds {container} and {bit_depth}, so the two passes can
    // disagree on the album folder name even though the track filenames match. Matching on stems
    // rather than relative paths is what keeps the merge working there.
    [Fact]
    public void Graft_WhenThePassesNameTheAlbumFolderDifferently_StillMergesPerTrack()
    {
        File_(Dir("preferred", "Artist", "Album [FLAC] [16B-44kHz]"), "01. Artist - One.flac");

        var fallbackAlbum = Dir("fallback", "Artist", "Album [MP3] [16B-44kHz]");
        File_(fallbackAlbum, "01. Artist - One.mp3");
        File_(fallbackAlbum, "02. Artist - Two.mp3");

        DownloadStaging.Graft(Dir("preferred"), Dir("fallback")).Should().Be(1);

        var merged = DownloadStaging.AudioFiles(Dir("preferred"));
        merged.Select(Path.GetFileName).OrderBy(n => n).Should().Equal(
            "01. Artist - One.flac", "02. Artist - Two.mp3");
        // The grafted track joins the preferred pass's folder rather than creating a second album dir.
        merged.Select(f => Path.GetFileName(Path.GetDirectoryName(f))).Distinct()
            .Should().Equal("Album [FLAC] [16B-44kHz]");
    }

    [Fact]
    public void Graft_OnAMultiDiscAlbum_PutsEachGraftedTrackInItsOwnDiscFolder()
    {
        File_(Dir("preferred", "Artist", "Album", "CD1"), "01. Artist - One.flac");
        File_(Dir("fallback", "Artist", "Album", "CD1"), "01. Artist - One.mp3");
        File_(Dir("fallback", "Artist", "Album", "CD2"), "01. Artist - Two.mp3");

        DownloadStaging.Graft(Dir("preferred"), Dir("fallback")).Should().Be(1);

        Path.Combine(_root, "preferred", "Artist", "Album", "CD2", "01. Artist - Two.mp3")
            .Should().Match(p => File.Exists(p));
    }

    // ---- Promote: merging into a library that already has the artist ----

    [Fact]
    public void Promote_MergesIntoAnExistingArtistFolderInsteadOfFailing()
    {
        var library = Dir("music");
        File_(Path.Combine(library, "Artist", "Earlier Album"), "01. Artist - Old.flac");
        File_(Dir("preferred", "Artist", "New Album"), "01. Artist - New.flac");

        DownloadStaging.Promote(Dir("preferred"), library);

        Names(library).Should().Equal("01. Artist - New.flac", "01. Artist - Old.flac");
        Directory.Exists(Path.Combine(library, "Artist", "Earlier Album")).Should().BeTrue();
    }

    [Fact]
    public void Promote_MovesTheFilesRatherThanCopyingThem()
    {
        var library = Dir("music");
        var staged = Dir("preferred", "Artist", "Album");
        File_(staged, "01. Artist - One.flac");
        File_(staged, "cover.jpg");

        DownloadStaging.Promote(Dir("preferred"), library);

        DownloadStaging.AudioFiles(Dir("preferred")).Should().BeEmpty();
        File.Exists(Path.Combine(library, "Artist", "Album", "cover.jpg")).Should().BeTrue();
    }

    // ---- Reset: a crashed previous attempt must not be counted as this run's tracks ----

    [Fact]
    public void Reset_ClearsLeftoversFromAnEarlierAttempt()
    {
        var staging = Dir("staging");
        File_(Path.Combine(staging, "preferred", "Artist", "Album"), "01. Stale.flac");

        DownloadStaging.Reset(staging);

        Directory.Exists(staging).Should().BeTrue();
        DownloadStaging.AudioFiles(staging).Should().BeEmpty();
    }
}
