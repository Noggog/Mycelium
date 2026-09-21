using System.Text.Json;

namespace Mycelium.Backend.Services.Download;

/// <summary>What one move-aside did, so a caller can report it and a human can undo it.</summary>
/// <param name="Moved">How many files were moved out of the library.</param>
/// <param name="Destination">Where they went, or null when nothing moved.</param>
public readonly record struct TrashResult(int Moved, string? Destination);

/// <summary>Where superseded albums go.</summary>
/// <param name="Root">
/// One directory every removal is filed under (<c>LIBRARY_TRASH_DIR</c>), so clearing them out is a
/// single delete. Null means the old behaviour: a <see cref="LibraryTrash.TrashFolder"/> inside each
/// album's own folder.
/// </param>
public record LibraryTrashConfig(string? Root);

/// <summary>
/// Moves a superseded album out of the library instead of deleting it.
///
/// <para>An upgrade has to remove the copy it replaces <b>before</b> the new one is promoted, not
/// after: the deployed streamrip config names album folders without the container, so a FLAC upgrade
/// lands at the same path as the MP3 it replaces and <see cref="DownloadStaging.Promote"/> merges
/// directories — leaving both encodings interleaved in one folder, which Plex reads as a doubled
/// album. Moving first is what makes the promote land on clean ground.</para>
///
/// <para>Nothing is ever deleted here. Files go under a folder named for the album — inside the
/// configured <see cref="LibraryTrashConfig.Root"/>, or failing that a <c>.mycelium-removed</c>
/// directory in the album's own folder — with a <c>manifest.json</c> recording where each file was.
/// Deletion is a separate, later, human decision — and until then a bad swap is reversible by
/// reading the manifest.</para>
/// </summary>
public class LibraryTrash
{
    /// <summary>
    /// Where superseded files go. Dot-prefixed so a library scanner ignores it — the same trick
    /// <see cref="DownloadStaging.StagingFolder"/> uses, and load-bearing for the same reason: a
    /// "removed" album that Plex re-indexes has not been removed.
    /// </summary>
    public const string TrashFolder = ".mycelium-removed";

    /// <summary>The record of where each moved file came from, read back by <see cref="Restore"/>.</summary>
    private const string ManifestName = "manifest.json";

    private readonly ILogger<LibraryTrash> _logger;
    private readonly string? _root;

    public LibraryTrash(ILogger<LibraryTrash> logger, LibraryTrashConfig config)
    {
        _logger = logger;
        _root = string.IsNullOrWhiteSpace(config.Root) ? null : config.Root.Trim();
    }

    /// <summary>
    /// Moves <paramref name="files"/> aside, and returns how many made it.
    ///
    /// <para><paramref name="label"/> names the album; it is slugified into the trash folder name and
    /// paired with <paramref name="stamp"/> so two removals of the same album — or of two albums
    /// sharing a title — can't collide.</para>
    ///
    /// <para>Without a configured root, files stay beside the album and so on its own filesystem. A
    /// configured root on another filesystem (or another bind mount — the kernel counts those as
    /// separate even on one disk) still works, but each move degrades to copy-then-delete: slower,
    /// and non-atomic per file. A file that fails to move is logged and skipped rather than aborting
    /// the batch — the caller compares the count against what it asked for and decides.</para>
    /// </summary>
    public TrashResult MoveAside(IReadOnlyList<string> files, string label, string stamp)
    {
        if (files.Count == 0)
        {
            return new TrashResult(0, null);
        }

        // Without a configured root, the album's own directory is the natural place: using the library
        // root would recreate the whole artist/album path under the trash for no gain.
        var folder = $"{Slug(label)}-{stamp}";
        var destination = _root is not null
            ? Path.Combine(_root, folder)
            : Path.Combine(DownloadStaging.CommonDirectory(files) ?? Path.GetTempPath(), TrashFolder, folder);
        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex)
        {
            // An unwritable trash root (a bad LIBRARY_TRASH_DIR, a full disk) must read as "nothing
            // moved" rather than throw through the caller mid-swap: it is the one failure where the
            // library is still whole, and the caller's own count check turns it into a clean refusal.
            _logger.LogError(ex, "Could not open the trash folder {Destination}; nothing was moved", destination);
            return new TrashResult(0, null);
        }

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
    /// Puts a move-aside back where it came from, reading the manifest written beside it. Returns how
    /// many files made it home.
    ///
    /// <para>This is the undo <see cref="MoveAside"/>'s manifest was always for, done by the code
    /// rather than by hand. A swap that can't go through after the move has started leaves the worst
    /// state of all — half an album in the library and half in the trash — so the caller walks it
    /// back before refusing. Best-effort by the same reasoning as the move itself: a file that won't
    /// go back is logged and left, and the manifest stays on disk so a human can finish the job.</para>
    /// </summary>
    public int Restore(string? destination)
    {
        if (destination is null || !Directory.Exists(destination))
        {
            return 0;
        }

        List<(string From, string To)> moved;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(destination, ManifestName)));
            moved = document.RootElement.GetProperty("files").EnumerateArray()
                .Select(e => (From: e.GetProperty("from").GetString()!, To: e.GetProperty("to").GetString()!))
                .Where(m => m.From is not null && m.To is not null)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not read the removal manifest in {Destination}; the files there have to be put "
                + "back by hand", destination);
            return 0;
        }

        var restored = 0;
        foreach (var (from, to) in moved)
        {
            try
            {
                if (!File.Exists(to))
                {
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(from)!);
                File.Move(to, from);
                restored++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not put {File} back at {Original}", to, from);
            }
        }

        if (restored == moved.Count)
        {
            // Nothing left in there but the manifest describing an undone move, which would only
            // mislead whoever goes looking through the trash later.
            try
            {
                File.Delete(Path.Combine(destination, ManifestName));
                if (!Directory.EnumerateFileSystemEntries(destination).Any())
                {
                    Directory.Delete(destination);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not clear the emptied trash folder {Destination}", destination);
            }
        }

        _logger.LogInformation(
            "Restored {Restored}/{Total} file(s) from {Destination}", restored, moved.Count, destination);
        return restored;
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
                Path.Combine(destination, ManifestName),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            // The files have already moved; losing the manifest makes recovery manual rather than
            // impossible, so this must not fail the swap.
            _logger.LogWarning(ex, "Could not write the removal manifest in {Destination}", destination);
        }
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
