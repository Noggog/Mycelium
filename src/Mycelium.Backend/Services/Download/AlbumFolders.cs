namespace Mycelium.Backend.Services.Download;

/// <summary>
/// Reasoning about the folder an owned album lives in, shared by everything that moves one out of the
/// library — an upgrade swapping in a better copy (<see cref="UpgradeSwap"/>) and a deliberate removal
/// (<see cref="LibraryAlbumRemover"/>). Paths here are local ones, already through the
/// <see cref="LibraryPathMap"/>.
/// </summary>
internal static class AlbumFolders
{
    /// <summary>
    /// The folder an album's files live in — or null if they share nothing narrower than a library
    /// root. Treating a root as "the album's folder" would sweep up, or scatter files across, the top
    /// of the library, so callers fall back to handling the listed files alone.
    /// </summary>
    public static string? FolderOf(IReadOnlyList<string> files, LibraryPathMap paths)
    {
        var common = DownloadStaging.CommonDirectory(files);
        if (common is null)
        {
            return null;
        }

        var normalized = Normalize(common);
        var isRootOrAbove = paths.LocalPrefixes.Any(root =>
            root.Equals(normalized, StringComparison.Ordinal)
            || root.StartsWith(normalized + "/", StringComparison.Ordinal));
        return isRootOrAbove ? null : common;
    }

    /// <summary>
    /// Everything in the folder the album occupies, when the album owns that folder outright — so a
    /// move takes the cover art and the stray .cue along with the tracks. Another album's audio
    /// sitting in the same folder means this one doesn't own it, and only the files the library listed
    /// (<paramref name="present"/>) are taken; our own dot-directories are never swept up either way.
    /// </summary>
    public static IReadOnlyList<string> Owning(string albumDir, IReadOnlyList<string> present, ILogger logger)
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
            logger.LogWarning(
                ex, "Could not list {Dir}; moving only the files the library knows about", albumDir);
            return present;
        }
    }

    /// <summary>
    /// Removes the emptied folder and any parent it leaves empty, stopping below the library root it
    /// sits in — a drop folder organised by contributor should lose the album and the artist, never
    /// the contributor's own folder if something else of theirs is still in it, and never the root.
    /// Only ever removes directories that are already empty.
    /// </summary>
    public static void PruneEmpty(string albumDir, LibraryPathMap paths, ILogger logger)
    {
        var root = paths.LocalPrefixes
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
                logger.LogWarning(ex, "Could not remove the emptied folder {Dir}", dir);
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
    public static bool IsUnder(string? path, string root) =>
        path is not null
        && Normalize(path).StartsWith(Normalize(root) + "/", StringComparison.Ordinal);

    /// <summary>
    /// Whether a file under <paramref name="albumDir"/> is Mycelium's own bookkeeping — a previous
    /// removal's trash, or staging. Moving a trash folder into a trash folder is not a tidy-up.
    /// </summary>
    private static bool IsOurs(string albumDir, string file) =>
        Path.GetRelativePath(albumDir, file)
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar })
            .Any(segment => segment is LibraryTrash.TrashFolder or DownloadStaging.StagingFolder);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}
