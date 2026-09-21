using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Download;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// Replacing an album already in the library. Every case here is about <em>refusing</em> — this is the
/// only code in the app that moves a user's existing files, and each gate exists because the failure
/// it prevents is silent: a short album quietly losing tracks, a pointless swap churning files for no
/// gain, or a path map mistake moving something that isn't what we meant.
/// </summary>
public class UpgradeSwapTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"mycelium-swap-tests-{Guid.NewGuid():N}");

    private readonly string _library;
    private readonly string _drop;
    private readonly string _staged;
    private readonly ILibraryQuery _query = Substitute.For<ILibraryQuery>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly FakePurchaseRepo _purchases = new();

    private const int AlbumKey = 4242;

    public UpgradeSwapTests()
    {
        _library = Path.Combine(_root, "music");
        _drop = Path.Combine(_root, "mediadrop");
        _staged = Path.Combine(_root, "staged");
        Directory.CreateDirectory(Path.Combine(_library, "Alvvays", "Blue Rev"));
        Directory.CreateDirectory(_staged);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Plex's namespace is deliberately different from ours, as it is in reality.</summary>
    private const string PlexRoot = "/plex-media/music";

    /// <summary>
    /// The second library root — a folder people upload into, organised by contributor rather than by
    /// artist, so the album sits one level deeper than it does under the main root.
    /// </summary>
    private const string PlexDropRoot = "/plex-mediadrop/Music";

    private const string BothRoots = $"{PlexRoot}:__LIBRARY__,{PlexDropRoot}:__DROP__";

    private UpgradeSwap Sut(string? pathMap = $"{PlexRoot}:__LIBRARY__", string? trashRoot = null) =>
        new(_query, _catalog,
            new LibraryPathMap(pathMap?.Replace("__LIBRARY__", _library).Replace("__DROP__", _drop)),
            new LibraryTrash(NullLogger<LibraryTrash>.Instance, new LibraryTrashConfig(trashRoot)),
            new UpgradeMatchKeeper(_query, Substitute.For<ILibraryMatcher>(), _catalog, _purchases,
                DownloaderConfigForTests.Default, NullLogger<UpgradeMatchKeeper>.Instance),
            _purchases, DownloaderConfigForTests.Default with { DownloadDir = _library },
            NullLogger<UpgradeSwap>.Instance);

    /// <summary>Puts an owned album on disk and tells the fake library where Plex thinks it is.</summary>
    private string[] ExistingAlbum(params string[] fileNames)
    {
        var dir = Path.Combine(_library, "Alvvays", "Blue Rev");
        var written = new List<string>();
        foreach (var name in fileNames)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, "audio");
            written.Add(path);
        }

        _catalog.GetAlbumPlexRatingKeys(Arg.Any<IReadOnlyCollection<string>>()).Returns(
            new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alvvays"] = new(StringComparer.OrdinalIgnoreCase) { ["Blue Rev"] = AlbumKey },
            });
        _query.QueryAlbumFiles(AlbumKey).Returns(
            fileNames.Select(n => $"{PlexRoot}/Alvvays/Blue Rev/{n}").ToArray());
        return written.ToArray();
    }

    /// <summary>What the download produced, sitting in staging.</summary>
    private void Downloaded(params string[] fileNames)
    {
        foreach (var name in fileNames)
        {
            File.WriteAllText(Path.Combine(_staged, name), "audio");
        }
    }

    private static PurchaseItem Upgrade(AudioQuality? owned = AudioQuality.Lossy) =>
        new("album:alvvays blue rev", FeedKind.UpgradeAlbum, new ArtistKey("Alvvays"), "Blue Rev",
            null, 0, Array.Empty<string>(), PurchaseStatus.Downloading, DateTimeOffset.UtcNow, null,
            7, "Alvvays", DownloadFailure.None, false, AudioQuality.Lossless, null, owned);

    private string[] LibraryFiles() =>
        Directory.EnumerateFiles(Path.Combine(_library, "Alvvays", "Blue Rev"))
            .Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

    [Fact]
    public async Task A_complete_better_copy_moves_the_old_one_aside()
    {
        ExistingAlbum("01.mp3", "02.mp3");
        Downloaded("01.flac", "02.flac");

        var outcome = await Sut().PrepareForPromotion(Upgrade(), _staged, landed: 2, expected: 2);

        outcome.Swapped.Should().BeTrue();
        // The old copy is gone from the library — so the promote that follows lands on clean ground
        // instead of interleaving two encodings in one folder.
        LibraryFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task The_old_copy_is_moved_not_deleted()
    {
        ExistingAlbum("01.mp3");
        Downloaded("01.flac");

        await Sut().PrepareForPromotion(Upgrade(), _staged, landed: 1, expected: 1);

        var trash = Directory.EnumerateFiles(_library, "*", SearchOption.AllDirectories)
            .Where(f => f.Contains(LibraryTrash.TrashFolder, StringComparison.Ordinal))
            .ToArray();
        trash.Should().Contain(f => f.EndsWith("01.mp3", StringComparison.Ordinal));
        // And a record of where it came from, so a bad swap is reversible by hand.
        trash.Should().Contain(f => f.EndsWith("manifest.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_swap_names_the_folder_it_emptied_so_the_upgrade_goes_back_there()
    {
        ExistingAlbum("01.mp3", "02.mp3");
        Downloaded("01.flac", "02.flac");

        var outcome = await Sut().PrepareForPromotion(Upgrade(), _staged, landed: 2, expected: 2);

        outcome.AlbumDir.Should().Be(Path.Combine(_library, "Alvvays", "Blue Rev"));
    }

    [Fact]
    public async Task Files_loose_at_the_library_root_give_no_folder_to_promote_into()
    {
        // Promoting "into" the root would scatter tracks across the top of the library.
        File.WriteAllText(Path.Combine(_library, "01.mp3"), "audio");
        _catalog.GetAlbumPlexRatingKeys(Arg.Any<IReadOnlyCollection<string>>()).Returns(
            new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alvvays"] = new(StringComparer.OrdinalIgnoreCase) { ["Blue Rev"] = AlbumKey },
            });
        _query.QueryAlbumFiles(AlbumKey).Returns(new[] { $"{PlexRoot}/01.mp3" });
        Downloaded("01.flac");

        var outcome = await Sut().PrepareForPromotion(Upgrade(), _staged, landed: 1, expected: 1);

        outcome.Swapped.Should().BeTrue();
        outcome.AlbumDir.Should().BeNull();
    }

    [Fact]
    public async Task The_old_copys_plex_match_is_saved_before_its_files_move()
    {
        // Ratings follow the match. If the new copy lands matched to a different release, this is
        // what lets it be put back.
        ExistingAlbum("01.mp3");
        Downloaded("01.flac");
        var item = Upgrade();
        _purchases.Seed(item);
        _query.QueryAlbumMatch(AlbumKey).Returns("plex://album/blue-rev");

        await Sut().PrepareForPromotion(item, _staged, landed: 1, expected: 1);

        var report = _purchases.Items.Single().Upgrade!;
        report.OldMatch.Should().Be("plex://album/blue-rev");
        report.Match.Should().Be(UpgradeMatchCheck.Waiting);
        // And what the swap did, for the Download page to show.
        report.FilesMoved.Should().Be(1);
        report.MovedTo.Should().NotBeNull();
        report.ReplacedQuality.Should().Be(AudioQuality.Lossy);
        report.NewQuality.Should().Be(AudioQuality.Lossless);
    }

    [Fact]
    public async Task A_refusal_is_recorded_on_the_row_with_its_reason()
    {
        ExistingAlbum("01.mp3", "02.mp3", "03.mp3");
        Downloaded("01.flac", "02.flac");
        var item = Upgrade();
        _purchases.Seed(item);

        await Sut().PrepareForPromotion(item, _staged, landed: 2, expected: 3);

        _purchases.Items.Single().Upgrade!.RefusalDetail.Should().Be("got 2 of 3 tracks");
    }

    [Fact]
    public async Task A_configured_trash_root_collects_the_old_copy_outside_the_library()
    {
        // LIBRARY_TRASH_DIR: every removal in one place, so clearing them out is a single delete
        // rather than a hunt through every album folder.
        var trashRoot = Path.Combine(_root, "music-to-delete");
        ExistingAlbum("01.mp3");
        Downloaded("01.flac");

        await Sut(trashRoot: trashRoot).PrepareForPromotion(Upgrade(), _staged, landed: 1, expected: 1);

        LibraryFiles().Should().BeEmpty();
        Directory.EnumerateDirectories(_library, LibraryTrash.TrashFolder, SearchOption.AllDirectories)
            .Should().BeEmpty();
        var trash = Directory.EnumerateFiles(trashRoot, "*", SearchOption.AllDirectories).ToArray();
        trash.Should().Contain(f => f.EndsWith("01.mp3", StringComparison.Ordinal));
        trash.Should().Contain(f => f.EndsWith("manifest.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_short_result_is_refused_and_the_library_left_alone()
    {
        // Replacing a complete album with an incomplete one is the one outcome an upgrade must never
        // produce — worse than not upgrading at all, and invisible until someone plays it.
        ExistingAlbum("01.mp3", "02.mp3", "03.mp3");
        Downloaded("01.flac", "02.flac");

        var outcome = await Sut().PrepareForPromotion(Upgrade(), _staged, landed: 2, expected: 3);

        outcome.Swapped.Should().BeFalse();
        outcome.Refusal.Should().Be(SwapRefusal.Incomplete);
        LibraryFiles().Should().HaveCount(3);
    }

    [Fact]
    public async Task A_result_that_is_no_better_is_refused()
    {
        // With the fallback ladder on, an album Deezer has no lossless master for comes back at 320 —
        // the tier we already hold. Swapping would churn files and disturb the Plex item for nothing.
        ExistingAlbum("01.mp3");
        Downloaded("01.mp3");

        var outcome = await Sut().PrepareForPromotion(Upgrade(), _staged, landed: 1, expected: 1);

        outcome.Swapped.Should().BeFalse();
        outcome.Refusal.Should().Be(SwapRefusal.NotAnUpgrade);
        LibraryFiles().Should().ContainSingle();
    }

    [Fact]
    public async Task Nothing_is_touched_without_a_path_map()
    {
        ExistingAlbum("01.mp3");
        Downloaded("01.flac");

        var outcome = await Sut(pathMap: null).PrepareForPromotion(Upgrade(), _staged, 1, 1);

        outcome.Swapped.Should().BeFalse();
        outcome.Refusal.Should().Be(SwapRefusal.NoPathMap);
        LibraryFiles().Should().ContainSingle();
    }

    [Fact]
    public async Task An_album_outside_the_mapped_prefixes_is_refused_whole()
    {
        // All-or-nothing on purpose: moving half an album aside and promoting over the rest is worse
        // than leaving it alone.
        ExistingAlbum("01.mp3");
        Downloaded("01.flac");

        var outcome = await Sut(pathMap: "/somewhere/else:__LIBRARY__")
            .PrepareForPromotion(Upgrade(), _staged, 1, 1);

        outcome.Swapped.Should().BeFalse();
        outcome.Refusal.Should().Be(SwapRefusal.Unmapped);
        LibraryFiles().Should().ContainSingle();
    }

    [Fact]
    public async Task An_album_the_library_cannot_locate_is_refused()
    {
        Downloaded("01.flac");
        _catalog.GetAlbumPlexRatingKeys(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase));

        var outcome = await Sut().PrepareForPromotion(Upgrade(), _staged, 1, 1);

        outcome.Swapped.Should().BeFalse();
        outcome.Refusal.Should().Be(SwapRefusal.NotLocatable);
    }

    [Fact]
    public async Task An_album_of_unknown_quality_is_never_replaced()
    {
        // "We don't know what's on disk" is not grounds for overwriting it, however good the download
        // looks — the same rule that keeps un-swept albums out of the upgrade feed. This shouldn't be
        // reachable (the feed wouldn't have offered it), which is exactly why it is worth pinning:
        // the last gate in front of a destructive move should not depend on an earlier one holding.
        ExistingAlbum("01.mp3");
        Downloaded("01.flac");

        var outcome = await Sut().PrepareForPromotion(Upgrade(owned: null), _staged, 1, 1);

        outcome.Swapped.Should().BeFalse();
        outcome.Refusal.Should().Be(SwapRefusal.NotAnUpgrade);
        LibraryFiles().Should().ContainSingle();
    }

    /// <summary>
    /// Puts an owned album in the drop folder — which nests it under a contributor — and tells the
    /// fake library where Plex thinks it is.
    /// </summary>
    private string DropAlbum(params string[] fileNames)
    {
        var dir = Path.Combine(_drop, "Brennan", "Alvvays", "Blue Rev");
        Directory.CreateDirectory(dir);
        foreach (var name in fileNames)
        {
            File.WriteAllText(Path.Combine(dir, name), "audio");
        }

        _catalog.GetAlbumPlexRatingKeys(Arg.Any<IReadOnlyCollection<string>>()).Returns(
            new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alvvays"] = new(StringComparer.OrdinalIgnoreCase) { ["Blue Rev"] = AlbumKey },
            });
        _query.QueryAlbumFiles(AlbumKey).Returns(
            fileNames.Select(n => $"{PlexDropRoot}/Brennan/Alvvays/Blue Rev/{n}").ToArray());
        return dir;
    }

    /// <summary>What the download produced, in the {artist}/{album} folders streamrip actually writes.</summary>
    private void DownloadedUnder(string relativeDir, params string[] fileNames)
    {
        var dir = Path.Combine(_staged, relativeDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        foreach (var name in fileNames)
        {
            File.WriteAllText(Path.Combine(dir, name), "audio");
        }
    }

    [Fact]
    public async Task An_album_in_the_drop_folder_is_consolidated_into_the_main_library()
    {
        // The drop folder is for uploads, not for the library to grow a second copy of itself in. An
        // upgrade found there is filed under the main root the way any fresh download would be.
        DropAlbum("01.mp3");
        DownloadedUnder("Alvvays/Blue Rev", "01.flac");

        var outcome = await Sut(pathMap: BothRoots).PrepareForPromotion(Upgrade(), _staged, 1, 1);

        outcome.Swapped.Should().BeTrue();
        outcome.AlbumDir.Should().Be(Path.Combine(_library, "Alvvays", "Blue Rev"));
    }

    [Fact]
    public async Task Consolidating_reuses_an_artist_folder_that_differs_only_in_case()
    {
        // streamrip names folders from Deezer's metadata and the library's were named by whatever
        // filed them. On Linux "ALVVAYS" beside "Alvvays" is two artists, which is the whole reason an
        // in-place upgrade goes back to its own folder — consolidation has to answer it too.
        Directory.CreateDirectory(Path.Combine(_library, "Alvvays"));
        DropAlbum("01.mp3");
        DownloadedUnder("ALVVAYS/Blue Rev", "01.flac");

        var outcome = await Sut(pathMap: BothRoots).PrepareForPromotion(Upgrade(), _staged, 1, 1);

        outcome.AlbumDir.Should().Be(Path.Combine(_library, "Alvvays", "Blue Rev"));
    }

    [Fact]
    public async Task Consolidating_takes_the_whole_folder_and_leaves_no_husk_behind()
    {
        // Nothing is promoted back into the drop folder, so cover art left there would be orphaned
        // and the emptied artist folder would be litter in someone's collection.
        var dir = DropAlbum("01.mp3");
        File.WriteAllText(Path.Combine(dir, "cover.jpg"), "art");
        DownloadedUnder("Alvvays/Blue Rev", "01.flac");
        var trashRoot = Path.Combine(_root, "music-to-delete");

        await Sut(pathMap: BothRoots, trashRoot: trashRoot).PrepareForPromotion(Upgrade(), _staged, 1, 1);

        var trash = Directory.EnumerateFiles(trashRoot, "*", SearchOption.AllDirectories).ToArray();
        trash.Should().Contain(f => f.EndsWith("01.mp3", StringComparison.Ordinal));
        trash.Should().Contain(f => f.EndsWith("cover.jpg", StringComparison.Ordinal));
        // The album and the artist folder go; the contributor's own folder and the root stay.
        Directory.Exists(Path.Combine(_drop, "Brennan", "Alvvays")).Should().BeFalse();
        Directory.Exists(_drop).Should().BeTrue();
    }

    [Fact]
    public async Task A_shared_folder_in_the_drop_area_keeps_what_isnt_this_albums()
    {
        // Loose tracks filed straight under an artist mean the folder isn't this album's to clear out.
        var dir = Path.Combine(_drop, "Brennan", "Alvvays");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "01.mp3"), "audio");
        File.WriteAllText(Path.Combine(dir, "someone-elses.mp3"), "audio");
        _catalog.GetAlbumPlexRatingKeys(Arg.Any<IReadOnlyCollection<string>>()).Returns(
            new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alvvays"] = new(StringComparer.OrdinalIgnoreCase) { ["Blue Rev"] = AlbumKey },
            });
        _query.QueryAlbumFiles(AlbumKey).Returns(new[] { $"{PlexDropRoot}/Brennan/Alvvays/01.mp3" });
        DownloadedUnder("Alvvays/Blue Rev", "01.flac");

        var outcome = await Sut(pathMap: BothRoots).PrepareForPromotion(Upgrade(), _staged, 1, 1);

        outcome.Swapped.Should().BeTrue();
        File.Exists(Path.Combine(dir, "someone-elses.mp3")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "01.mp3")).Should().BeFalse();
    }

    [Fact]
    public async Task An_album_already_in_the_main_library_is_still_upgraded_in_place()
    {
        // Consolidation is for the drop folder only; everything else keeps the library's own layout.
        ExistingAlbum("01.mp3");
        DownloadedUnder("ALVVAYS/Blue Rev", "01.flac");

        var outcome = await Sut(pathMap: BothRoots).PrepareForPromotion(Upgrade(), _staged, 1, 1);

        outcome.AlbumDir.Should().Be(Path.Combine(_library, "Alvvays", "Blue Rev"));
        _purchases.Items.SingleOrDefault()?.Upgrade?.PreviousFolder.Should().BeNull();
    }

    [Fact]
    public async Task A_half_moved_album_is_put_back_rather_than_promoted_onto()
    {
        // The worst reachable state: half the old copy in the trash, the new one about to land on top
        // of the rest. One file that won't move has to undo the ones that did. A second disc gives a
        // subfolder to make unwritable, which is the difference between a partial move and no move.
        var dir = Path.Combine(_library, "Alvvays", "Blue Rev");
        var disc2 = Path.Combine(dir, "disc2");
        Directory.CreateDirectory(disc2);
        File.WriteAllText(Path.Combine(dir, "01.mp3"), "audio");
        File.WriteAllText(Path.Combine(disc2, "02.mp3"), "audio");
        _catalog.GetAlbumPlexRatingKeys(Arg.Any<IReadOnlyCollection<string>>()).Returns(
            new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alvvays"] = new(StringComparer.OrdinalIgnoreCase) { ["Blue Rev"] = AlbumKey },
            });
        _query.QueryAlbumFiles(AlbumKey).Returns(new[]
        {
            $"{PlexRoot}/Alvvays/Blue Rev/01.mp3", $"{PlexRoot}/Alvvays/Blue Rev/disc2/02.mp3",
        });
        Downloaded("01.flac", "02.flac");
        if (!TryMakeReadOnly(disc2))
        {
            return; // Running as root, or on a filesystem that ignores the mode — nothing to prove here.
        }

        try
        {
            var outcome = await Sut(trashRoot: Path.Combine(_root, "music-to-delete"))
                .PrepareForPromotion(Upgrade(), _staged, 2, 2);

            outcome.Swapped.Should().BeFalse();
            outcome.Refusal.Should().Be(SwapRefusal.MoveIncomplete);
        }
        finally
        {
            File.SetUnixFileMode(disc2,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Both tracks are still where they were, so the album plays exactly as it did before — the
        // one that did move has been put back from the manifest.
        File.Exists(Path.Combine(dir, "01.mp3")).Should().BeTrue();
        File.Exists(Path.Combine(disc2, "02.mp3")).Should().BeTrue();
    }

    /// <summary>
    /// Makes a directory unwritable, and says whether that actually took — it doesn't for root, and
    /// a test that silently passes because the setup did nothing is worse than no test.
    /// </summary>
    private static bool TryMakeReadOnly(string dir)
    {
        try
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var probe = Path.Combine(dir, "probe.tmp");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
