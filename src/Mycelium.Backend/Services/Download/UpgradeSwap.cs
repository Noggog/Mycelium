using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Download;

/// <summary>Why an upgrade couldn't be swapped in, when it couldn't.</summary>
public enum SwapRefusal
{
    /// <summary>It was swapped in.</summary>
    None,

    /// <summary>No <c>PLEX_PATH_MAP</c> is configured, so no library file can be located at all.</summary>
    NoPathMap,

    /// <summary>The album's files sit outside every mapped prefix — we can't reach them safely.</summary>
    Unmapped,

    /// <summary>The library has no record of where this album's files are (no rating key, no tracks).</summary>
    NotLocatable,

    /// <summary>
    /// What came down isn't better than what is already there. Deezer served the same tier we hold —
    /// so swapping would churn files, disturb the Plex item and gain nothing.
    /// </summary>
    NotAnUpgrade,

    /// <summary>
    /// What came down is short of the album. Promoting it would replace a complete record with an
    /// incomplete one — the one outcome an upgrade must never produce.
    /// </summary>
    Incomplete,

    /// <summary>
    /// The old copy could not be moved out of the way in one piece, so the new one was not promoted
    /// on top of a half-emptied folder. Whatever did move is put back.
    /// </summary>
    MoveIncomplete,
}

/// <summary>The outcome of trying to swap a downloaded upgrade in for the copy already held.</summary>
/// <param name="AlbumDir">
/// The folder to promote the upgrade into: the one the replaced copy was moved out of, or — when
/// that copy lived outside the main library root — its consolidated home under that root. Null when
/// there is no single folder to name, and the normal promote applies.
/// </param>
public readonly record struct SwapOutcome(
    bool Swapped, SwapRefusal Refusal, string? Detail = null, string? AlbumDir = null)
{
    public static SwapOutcome Ok(string? albumDir) => new(true, SwapRefusal.None, AlbumDir: albumDir);

    public static SwapOutcome Refused(SwapRefusal refusal, string? detail = null) =>
        new(false, refusal, detail);
}

/// <summary>
/// Replaces an album already in the library with a better copy of it.
///
/// <para>Sequenced <b>download → verify → move the old copy aside → promote → rescan</b>, and the
/// order is not cosmetic. The deployed streamrip config names album folders without the container,
/// so an upgrade lands at the same path as the copy it replaces and
/// <see cref="DownloadStaging.Promote"/> merges directories: promoting first would interleave both
/// encodings in one folder, which Plex reads as a doubled album. Moving first is what makes the
/// promote land on clean ground — and because the old copy goes to a trash folder with a manifest
/// rather than being deleted, a failure between the two steps is recoverable.</para>
///
/// <para>Where the new copy lands depends on where the old one lived. An album already under the
/// main library root is upgraded <b>in place</b>, back into the folder that was just emptied, because
/// streamrip names folders from Deezer's metadata and the library's were named by whatever filed them
/// — "Children Of Bodom" arriving beside an existing "Children of Bodom" is two artists on a
/// case-sensitive filesystem. An album in a <b>drop folder</b> — a second library root people upload
/// into — is instead <b>consolidated</b>: the new copy is filed under the main root the way any fresh
/// download would be, and the old one leaves the drop folder for good. Putting the upgrade back in
/// the drop folder would grow a second library there, one album at a time.</para>
///
/// <para>Two gates stand in front of all of that, and both refuse rather than proceed:</para>
/// <list type="bullet">
///   <item>the result must be <b>complete</b> — a short album must never replace a whole one; and</item>
///   <item>it must be <b>strictly better</b> than what is held — otherwise the swap is pure churn.</item>
/// </list>
/// </summary>
public class UpgradeSwap
{
    private readonly ILibraryQuery _library;
    private readonly IArtistCatalogRepo _catalog;
    private readonly LibraryPathMap _paths;
    private readonly LibraryTrash _trash;
    private readonly UpgradeMatchKeeper _matches;
    private readonly IPurchaseRepo _purchases;
    private readonly DownloaderConfig _config;
    private readonly ILogger<UpgradeSwap> _logger;

    public UpgradeSwap(
        ILibraryQuery library,
        IArtistCatalogRepo catalog,
        LibraryPathMap paths,
        LibraryTrash trash,
        UpgradeMatchKeeper matches,
        IPurchaseRepo purchases,
        DownloaderConfig config,
        ILogger<UpgradeSwap> logger)
    {
        _library = library;
        _catalog = catalog;
        _paths = paths;
        _trash = trash;
        _matches = matches;
        _purchases = purchases;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Clears the way for <paramref name="item"/>'s upgrade to be promoted, having checked that what
    /// was downloaded is worth promoting. Returns a refusal — and moves nothing — when it isn't.
    /// </summary>
    /// <param name="stagedDir">Where the verified download is sitting, pre-promotion.</param>
    /// <param name="landed">How many tracks it holds.</param>
    /// <param name="expected">How many Deezer says the album has; 0 when it wouldn't say.</param>
    public async Task<SwapOutcome> PrepareForPromotion(
        PurchaseItem item, string stagedDir, int landed, int expected)
    {
        var outcome = await Swap(item, stagedDir, landed, expected);
        if (!outcome.Swapped)
        {
            // Kept on the row, not just logged, so the Download page can say why this album wasn't
            // replaced — "got 9 of 10 tracks" is the answer to "why is this still MP3?".
            await _purchases.SetUpgrade(
                item.Id, new UpgradeReport(DateTimeOffset.UtcNow, RefusalDetail: outcome.Detail));
        }
        return outcome;
    }

    private async Task<SwapOutcome> Swap(PurchaseItem item, string stagedDir, int landed, int expected)
    {
        // Gate 1: completeness. Deezer's per-track gaps mean a lossless request can come back short,
        // and swapping that in would lose tracks the library already had — the one failure worse than
        // not upgrading at all.
        if (expected > 0 && landed < expected)
        {
            return SwapOutcome.Refused(
                SwapRefusal.Incomplete, $"got {landed} of {expected} tracks");
        }

        // Gate 2: it has to actually be better. With the fallback ladder on, an album Deezer has no
        // lossless master for comes back at 320 — the same tier we already hold — and promoting that
        // would move files and disturb the Plex item for nothing.
        var acquired = DownloadStaging.QualityOf(stagedDir);
        if (!(item.OwnedQuality < acquired))
        {
            return SwapOutcome.Refused(
                SwapRefusal.NotAnUpgrade,
                $"downloaded {acquired?.ToString() ?? "nothing identifiable"}, "
                + $"already hold {item.OwnedQuality?.ToString() ?? "unknown"}");
        }

        if (!_paths.IsConfigured)
        {
            return SwapOutcome.Refused(
                SwapRefusal.NoPathMap,
                "PLEX_PATH_MAP is not set, so the existing files can't be located");
        }

        var located = await LocateExisting(item);
        var existing = located?.Files ?? Array.Empty<string>();
        if (existing.Count == 0)
        {
            return SwapOutcome.Refused(
                SwapRefusal.NotLocatable, "the library reports no files for this album");
        }

        var local = existing.Select(f => (Plex: f, Local: _paths.ToLocal(f))).ToList();
        var unmapped = local.Where(f => f.Local is null).Select(f => f.Plex).ToList();
        if (unmapped.Count > 0)
        {
            // Deliberately all-or-nothing: moving half an album aside and promoting over the rest is
            // worse than leaving it alone, and a silent partial swap is exactly the failure the path
            // map exists to prevent.
            return SwapOutcome.Refused(
                SwapRefusal.Unmapped,
                $"{unmapped.Count} file(s) lie outside the mapped prefixes "
                + $"({string.Join(", ", _paths.PlexPrefixes)}); first is {unmapped[0]}");
        }

        var present = local.Select(f => f.Local!).Where(File.Exists).ToList();
        if (present.Count == 0)
        {
            return SwapOutcome.Refused(
                SwapRefusal.NotLocatable,
                "the mapped paths don't exist from here — check PLEX_PATH_MAP against the mounts");
        }

        // Read before the move: afterwards the files aren't there to locate it by.
        var albumDir = AlbumFolder(present);

        // An album outside the main library root came from a drop folder, so it is consolidated
        // rather than upgraded in place — see the class summary. Falling back to in-place when no
        // destination can be named keeps a nameable case from becoming a scatter across the root.
        var consolidating = albumDir is not null && _config.DownloadDir is { Length: > 0 }
                            && !IsUnder(albumDir, _config.DownloadDir);
        var promoteInto = consolidating
            ? DownloadStaging.ConsolidationTarget(stagedDir, _config.DownloadDir) ?? albumDir
            : albumDir;
        consolidating = consolidating && promoteInto != albumDir;

        // Saved before anything moves, so a copy that Plex matches to a different release — which
        // makes its ratings look lost — can be put back once it lands (see UpgradeMatchKeeper).
        var report = await _matches.Remember(item, located!.Value.Key);

        // Consolidating empties the old folder for good, so what Plex doesn't list — cover art, a
        // .cue, a stray log — goes with it rather than being left behind as a husk. An in-place
        // upgrade promotes back into that same folder, so its extras are left exactly where they are.
        var moving = consolidating ? WholeFolder(albumDir!, present) : present;

        var result = _trash.MoveAside(
            moving,
            $"{item.Artist.ArtistName} - {item.Album}",
            // Stamped from the row rather than the clock so a retry of the same album is traceable.
            item.Id.GetHashCode().ToString("x8"));

        // A part-moved album is the one state nothing downstream can recover from. In place it means
        // promoting onto a half-emptied folder, which interleaves two encodings; consolidating it
        // means the old copy stays in the drop folder *and* a new one appears under the main root.
        // Either way it is the doubled album this whole sequence exists to prevent, so what moved
        // goes back and the swap refuses.
        if (result.Moved < moving.Count)
        {
            var restored = _trash.Restore(result.Destination);
            _logger.LogError(
                "Upgrade for {Artist} — {Album}: only {Moved} of {Total} existing file(s) could be "
                + "moved aside, so nothing was promoted; {Restored} went back to the library",
                item.Artist.ArtistName, item.Album, result.Moved, moving.Count, restored);
            return SwapOutcome.Refused(
                SwapRefusal.MoveIncomplete,
                $"moved only {result.Moved} of {moving.Count} existing file(s) aside"
                + (restored == result.Moved
                    ? ""
                    : $"; {result.Moved - restored} could not be put back and are in {result.Destination}"));
        }

        if (consolidating)
        {
            // The folder is empty now and nothing will be promoted back into it. Leaving it would
            // leave the drop folder full of hollow artist trees.
            PruneEmptyFolders(albumDir!);
        }

        _logger.LogInformation(
            "Upgrade for {Artist} — {Album}: moved {Moved} existing file(s) aside to {Where}; "
            + "promoting the {Acquired} copy into {Into}{Consolidated}",
            item.Artist.ArtistName, item.Album, result.Moved, result.Destination, acquired,
            promoteInto ?? _config.DownloadDir,
            consolidating ? $" (consolidated out of {albumDir})" : "");
        await _purchases.SetUpgrade(item.Id, report with
        {
            ReplacedQuality = item.OwnedQuality,
            NewQuality = acquired,
            FilesMoved = result.Moved,
            MovedTo = result.Destination,
            AlbumFolder = promoteInto,
            PreviousFolder = consolidating ? albumDir : null,
        });
        return SwapOutcome.Ok(promoteInto);
    }

    /// <summary>
    /// Everything in the folder the old copy occupied, when the album owns that folder outright — so
    /// a consolidation takes the cover art and the stray .cue along with the tracks. Another album's
    /// audio sitting in the same folder means this one doesn't own it, and only the files Plex listed
    /// are taken; our own dot-directories are never swept up either way.
    /// </summary>
    private IReadOnlyList<string> WholeFolder(string albumDir, IReadOnlyList<string> present)
    {
        try
        {
            var all = Directory.EnumerateFiles(albumDir, "*", SearchOption.AllDirectories)
                .Where(f => !IsOurs(albumDir, f))
                .ToArray();
            var known = present.ToHashSet(StringComparer.Ordinal);
            return all.Any(f => DownloadStaging.IsAudio(f) && !known.Contains(f)) ? present : all;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Could not list {Dir}; moving only the files the library knows about", albumDir);
            return present;
        }
    }

    /// <summary>
    /// Whether a file under <paramref name="albumDir"/> is Mycelium's own bookkeeping — a previous
    /// removal's trash, or staging. Moving a trash folder into a trash folder is not a tidy-up.
    /// </summary>
    private static bool IsOurs(string albumDir, string file) =>
        Path.GetRelativePath(albumDir, file)
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar })
            .Any(segment => segment is LibraryTrash.TrashFolder or DownloadStaging.StagingFolder);

    /// <summary>
    /// Removes the emptied folder and any parent it leaves empty, stopping below the library root it
    /// sits in — a drop folder organised by contributor should lose the album and the artist, never
    /// the contributor's own folder if something else of theirs is still in it, and never the root.
    /// Only ever removes directories that are already empty.
    /// </summary>
    private void PruneEmptyFolders(string albumDir)
    {
        var root = _paths.LocalPrefixes
            .Where(prefix => IsUnder(albumDir, prefix))
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault();
        if (root is null)
        {
            return;
        }

        var dir = albumDir;
        while (IsUnder(dir, root))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    return;
                }
                Directory.Delete(dir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove the emptied folder {Dir}", dir);
                return;
            }

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir)
            {
                return;
            }
            dir = parent;
        }
    }

    /// <summary>Whether <paramref name="path"/> lies strictly inside <paramref name="root"/>.</summary>
    private static bool IsUnder(string? path, string root) =>
        path is not null
        && Normalize(path).StartsWith(Normalize(root) + "/", StringComparison.Ordinal);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// The folder the held copy lives in, to promote the upgrade back into — or null if its files
    /// share nothing narrower than a library root. Promoting "into" a root would scatter the new
    /// tracks loose at the top of the library, so that case falls back to the normal promote.
    /// </summary>
    private string? AlbumFolder(IReadOnlyList<string> files)
    {
        var common = DownloadStaging.CommonDirectory(files);
        if (common is null)
        {
            return null;
        }

        var normalized = common.Replace('\\', '/').TrimEnd('/');
        var isRootOrAbove = _paths.LocalPrefixes.Any(root =>
            root.Equals(normalized, StringComparison.Ordinal)
            || root.StartsWith(normalized + "/", StringComparison.Ordinal));
        return isRootOrAbove ? null : common;
    }

    /// <summary>
    /// The album's Plex rating key and the files the library says back it, or nothing when the
    /// library doesn't list the album.
    /// </summary>
    private async Task<(int Key, IReadOnlyList<string> Files)?> LocateExisting(PurchaseItem item)
    {
        var key = await UpgradeMatchKeeper.AlbumRatingKey(_catalog, item);
        return key is { } k ? (k, await _library.QueryAlbumFiles(k)) : null;
    }
}
