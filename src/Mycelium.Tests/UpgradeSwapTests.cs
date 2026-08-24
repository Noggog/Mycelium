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
    private readonly string _staged;
    private readonly ILibraryQuery _query = Substitute.For<ILibraryQuery>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();

    private const int AlbumKey = 4242;

    public UpgradeSwapTests()
    {
        _library = Path.Combine(_root, "music");
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

    private UpgradeSwap Sut(string? pathMap = $"{PlexRoot}:__LIBRARY__") =>
        new(_query, _catalog, new LibraryPathMap(pathMap?.Replace("__LIBRARY__", _library)),
            new LibraryTrash(NullLogger<LibraryTrash>.Instance), NullLogger<UpgradeSwap>.Instance);

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
}
