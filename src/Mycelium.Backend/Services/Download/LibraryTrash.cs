using System.Text.Json;

namespace Mycelium.Backend.Services.Download;

/// <summary>What one move-aside did, so a caller can report it and a human can undo it.</summary>
/// <param name="Moved">How many files were moved out of the library.</param>
/// <param name="Destination">Where they went, or null when nothing moved.</param>
public readonly record struct TrashResult(int Moved, string? Destination);

/// <summary>
/// Moves a superseded album out of the library instead of deleting it.
///
/// <para>An upgrade has to remove the copy it replaces <b>before</b> the new one is promoted, not
/// after: the deployed streamrip config names album folders without the container, so a FLAC upgrade
/// lands at the same path as the MP3 it replaces and <see cref="DownloadStaging.Promote"/> merges
/// directories — leaving both encodings interleaved in one folder, which Plex reads as a doubled
/// album. Moving first is what makes the promote land on clean ground.</para>
///
/// <para>Nothing is ever deleted here. Files go to a <c>.mycelium-removed</c> directory beside the
/// library root they came from, under a folder named for the album, with a <c>manifest.json</c>
/// recording where each file was. Deletion is a separate, later, human decision — and until then a
/// bad swap is reversible by reading the manifest.</para>
/// </summary>
public class LibraryTrash
{
    /// <summary>
    /// Where superseded files go. Dot-prefixed so a library scanner ignores it — the same trick
    /// <see cref="DownloadStaging.StagingFolder"/> uses, and load-bearing for the same reason: a
    /// "removed" album that Plex re-indexes has not been removed.
    /// </summary>
    public const string TrashFolder = ".mycelium-removed";

    private readonly ILogger<LibraryTrash> _logger;

    public LibraryTrash(ILogger<LibraryTrash> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Moves <paramref name="files"/> aside, and returns how many made it.
    ///
    /// <para><paramref name="label"/> names the album; it is slugified into the trash folder name and
    /// paired with <paramref name="stamp"/> so two removals of the same album — or of two albums
    /// sharing a title — can't collide.</para>
    ///
    /// <para>Files are grouped by the root they live under so each stays on its own filesystem: a
    /// cross-device move degrades to copy-then-delete, which for a 300MB album is slow and, worse,
    /// non-atomic. A file that fails to move is logged and skipped rather than aborting the batch —
    /// the caller compares the count against what it asked for and decides.</para>
    /// </summary>
    public TrashResult MoveAside(IReadOnlyList<string> files, string label, string stamp)
    {
        if (files.Count == 0)
        {
            return new TrashResult(0, null);
        }

        // The album's own directory is the natural root to preserve structure against; using the
        // library root would recreate the whole artist/album path under the trash for no gain.
        var sourceRoot = CommonDirectory(files);
        var destination = Path.Combine(sourceRoot, TrashFolder, $"{Slug(label)}-{stamp}");
        Directory.CreateDirectory(destination);

        var moved = new List<(string From, string To)>();
        foreach (var file in files)
        {
            try
            {
                var target = Path.Combine(destination, Path.GetFileName(file));
                // Two discs can hold "01 - Track.mp3"; keep both rather than overwriting one with
                // the other, or the manifest would describe a file that is no longer there.
                target = Unique(target);
                File.Move(file, target);
                moved.Add((file, target));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not move {File} aside; leaving it in place", file);
            }
        }

        WriteManifest(destination, label, moved);
        _logger.LogInformation(
            "Moved {Moved}/{Total} file(s) of \"{Label}\" aside to {Destination}",
            moved.Count, files.Count, label, destination);
        return new TrashResult(moved.Count, destination);
    }

    /// <summary>
    /// Records where every file came from, so a swap that goes wrong can be undone by hand. Written
    /// even when nothing moved: an empty manifest still says which album the folder belongs to.
    /// </summary>
    private void WriteManifest(string destination, string label, IReadOnlyList<(string From, string To)> moved)
    {
        try
        {
            var manifest = new
            {
                album = label,
                movedAt = DateTimeOffset.UtcNow,
                files = moved.Select(m => new { from = m.From, to = m.To }).ToArray(),
            };
            File.WriteAllText(
                Path.Combine(destination, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            // The files have already moved; losing the manifest makes recovery manual rather than
            // impossible, so this must not fail the swap.
            _logger.LogWarning(ex, "Could not write the removal manifest in {Destination}", destination);
        }
    }

    /// <summary>The deepest directory containing every file — the album's folder, in the normal case.</summary>
    private static string CommonDirectory(IReadOnlyList<string> files)
    {
        var directories = files.Select(f => Path.GetDirectoryName(f) ?? "").Where(d => d.Length > 0).ToList();
        if (directories.Count == 0)
        {
            return Path.GetTempPath();
        }

        var common = directories[0];
        foreach (var directory in directories.Skip(1))
        {
            while (!directory.Equals(common, StringComparison.Ordinal)
                   && !directory.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                var parent = Path.GetDirectoryName(common);
                if (string.IsNullOrEmpty(parent) || parent == common)
                {
                    return common;
                }
                common = parent;
            }
        }
        return common;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>A filesystem-safe folder name. Never derived from anything but the album title.</summary>
    private static string Slug(string label)
    {
        var cleaned = new string(label
            .Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '-')
            .ToArray())
            .Trim();
        cleaned = cleaned.Length > 60 ? cleaned[..60].Trim() : cleaned;
        return cleaned.Length == 0 ? "album" : cleaned;
    }
}
