using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Download;

/// <summary>
/// The filesystem half of a staged download. streamrip exits 0 even when every track failed (it
/// gathers tracks with <c>return_exceptions=True</c> and only logs each one), so "did it work" has to
/// be answered by looking at what landed — not by an exit code. Each quality pass therefore writes
/// into its own scratch tree under the library root, and only a verified result is promoted into the
/// library proper.
///
/// Two passes of the same album produce <b>identical filename stems</b>: streamrip builds the name
/// from <c>track_format</c> (whose keys are all metadata — tracknumber, artist, title — never the
/// container) and appends the extension last. So a track present at FLAC and a track present only at
/// MP3 differ solely in extension, which is what lets <see cref="Graft"/> fill gaps per-track instead
/// of re-fetching a whole album at the lower quality and throwing away the lossless files.
///
/// Staging lives at <c>{library}/{StagingFolder}/{albumId}</c> — inside the library root so promotion
/// is a same-filesystem move, and dot-prefixed so Plex's scanner skips it while a grab is in flight.
/// </summary>
public static class DownloadStaging
{
    /// <summary>Scratch directory under the library root. Dot-prefixed so library scanners ignore it.</summary>
    public const string StagingFolder = ".mycelium-incoming";

    // Everything streamrip can emit as a finished track, including the containers its optional
    // --codec conversion produces. Anything else in the tree (cover art, playlists) isn't a track and
    // must not count toward "we got the album".
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".m4a", ".aac", ".alac", ".ogg", ".opus", ".wav", ".aiff", ".aif", ".wv",
    };

    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    // Which container extensions are lossless. Mirrors AudioQualityTier.FromCodec, which works off
    // Plex's codec names; here the only evidence is the filename streamrip chose.
    private static readonly HashSet<string> LosslessExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".flac", ".alac", ".wav", ".aiff", ".aif", ".wv" };

    /// <summary>
    /// The tier of what is sitting in <paramref name="dir"/>, by the same majority rule the library
    /// scan uses — so an album that came down lossless except for the one track Deezer would only
    /// serve at 320 reads as lossless in both places. Null when there are no audio files to judge.
    /// </summary>
    public static AudioQuality? QualityOf(string dir)
    {
        var files = AudioFiles(dir);
        return files.Count == 0
            ? null
            : AudioQualityTier.Majority(files.Select(f =>
                (AudioQuality?)(LosslessExtensions.Contains(Path.GetExtension(f))
                    ? AudioQuality.Lossless
                    : AudioQuality.Lossy)));
    }

    /// <summary>Every audio file anywhere under <paramref name="dir"/>; empty if it doesn't exist.</summary>
    public static IReadOnlyList<string> AudioFiles(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(IsAudio).ToArray()
            : Array.Empty<string>();

    /// <summary>
    /// Clears and recreates a staging directory, so a run never inherits files from an attempt that
    /// crashed before its cleanup (which would otherwise be counted as this run's tracks).
    /// </summary>
    public static void Reset(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
        Directory.CreateDirectory(dir);
    }

    /// <summary>Where one album's passes are staged.</summary>
    public static string PathFor(string libraryRoot, long albumId) =>
        Path.Combine(libraryRoot, StagingFolder, albumId.ToString());

    /// <summary>
    /// Removes one album's staging tree, and the shared parent along with it once it's empty — an
    /// abandoned dot-directory sitting in someone's music library is still litter. Returns whether
    /// the album's own tree is gone.
    /// </summary>
    public static bool TryCleanup(string libraryRoot, long albumId)
    {
        var deleted = TryDelete(PathFor(libraryRoot, albumId));

        var parent = Path.Combine(libraryRoot, StagingFolder);
        try
        {
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch
        {
            // Raced with another download staging its own album, or a permissions quirk. Harmless:
            // the directory is reused, not recreated per run.
        }

        return deleted;
    }

    /// <summary>Best-effort cleanup; returns whether the tree is gone.</summary>
    public static bool TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Folds the fallback-quality pass into the preferred-quality one, keeping the preferred file
    /// wherever both passes got the same track. Returns how many tracks the fallback contributed.
    ///
    /// When the preferred pass produced nothing at all (the common "this album has no lossless" case)
    /// the fallback tree simply becomes the result. Otherwise only tracks whose stem is missing from
    /// the preferred tree are moved across, so an album that is 80% FLAC keeps its 80% FLAC.
    /// </summary>
    public static int Graft(string preferredDir, string fallbackDir)
    {
        var preferredFiles = AudioFiles(preferredDir);
        if (preferredFiles.Count == 0)
        {
            // Nothing survived at the preferred quality, so there's nothing to preserve and nothing to
            // graft onto — the fallback pass IS the album. Move it wholesale so callers downstream
            // only ever have to look at the preferred tree.
            if (!Directory.Exists(fallbackDir))
            {
                return 0;
            }
            MoveInto(fallbackDir, preferredDir);
            return AudioFiles(preferredDir).Count;
        }

        var preferredStemsByDir = preferredFiles
            .GroupBy(f => Path.GetDirectoryName(f)!)
            .ToDictionary(g => g.Key, g => g.Select(Stem).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var have = preferredFiles.Select(Stem).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var grafted = 0;
        foreach (var group in AudioFiles(fallbackDir).GroupBy(f => Path.GetDirectoryName(f)!))
        {
            var missing = group.Where(f => !have.Contains(Stem(f))).ToArray();
            if (missing.Length == 0)
            {
                continue;
            }

            var destinationDir = Counterpart(group, preferredStemsByDir)
                                 ?? Path.Combine(preferredDir, Path.GetRelativePath(fallbackDir, group.Key));
            Directory.CreateDirectory(destinationDir);

            foreach (var file in missing)
            {
                File.Move(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: true);
                have.Add(Stem(file));
                grafted++;
            }
        }

        return grafted;
    }

    private static string Stem(string path) => Path.GetFileNameWithoutExtension(path);

    /// <summary>
    /// The directory in the preferred tree that holds the same album (or the same disc of it) as this
    /// group of fallback files, or null if the preferred pass got none of these tracks.
    ///
    /// Matched on shared track names rather than path, because the two passes can disagree on the
    /// folder's name — streamrip's default folder_format embeds {container} and {bit_depth}, so the
    /// same album is "Album [FLAC] [16B-44kHz]" in one pass and "Album [MP3] ..." in the other. Path
    /// alone can't tell that apart from a genuinely new directory (disc two of a multi-disc release,
    /// which the preferred pass may have missed entirely and which must stay its own folder).
    /// </summary>
    private static string? Counterpart(
        IEnumerable<string> fallbackFiles,
        Dictionary<string, HashSet<string>> preferredStemsByDir)
    {
        var stems = fallbackFiles.Select(Stem).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return preferredStemsByDir
            .Select(entry => (Dir: entry.Key, Shared: entry.Value.Count(stems.Contains)))
            .Where(match => match.Shared > 0)
            .OrderByDescending(match => match.Shared)
            .ThenBy(match => match.Dir, StringComparer.Ordinal)
            .Select(match => match.Dir)
            .FirstOrDefault();
    }

    /// <summary>
    /// Moves a verified staging tree's contents into the library root, merging into artist/album
    /// folders that already exist rather than failing on them, and <see cref="Unhide">un-hiding</see>
    /// every name on the way in.
    /// </summary>
    public static void Promote(string stagedDir, string libraryRoot) =>
        MoveInto(stagedDir, libraryRoot, unhide: true);

    /// <summary>
    /// The name to file something in the library under: the same name, with any leading dots removed.
    ///
    /// <para>An album whose title genuinely begins with one — "...And Justice for All", "...Like
    /// Clockwork" — makes streamrip write a dot-prefixed folder, and on Linux that is a <i>hidden</i>
    /// entry. Plex's scanner skips it for exactly the reason it skips our own
    /// <see cref="StagingFolder"/>: the album downloads cleanly, verifies, promotes, and then never
    /// appears in the library. Trimming the leading dot costs nothing — Plex reads the title from the
    /// file tags, not the folder name — and is the difference between the album existing and not.</para>
    ///
    /// <para>Only leading dots go; dots elsewhere (the extension, "Vol. 2") are untouched, and a name
    /// that is nothing but dots is left alone rather than trimmed away to an empty string.</para>
    /// </summary>
    public static string Unhide(string name)
    {
        var trimmed = name.TrimStart('.');
        return trimmed.Length == 0 ? name : trimmed;
    }

    /// <summary>
    /// Recursive move that merges rather than replaces. Deliberately file-by-file: Directory.Move
    /// refuses to cross a filesystem boundary, while File.Move falls back to copy+delete — so a
    /// library root that turns out to be a different mount than the staging parent still works.
    ///
    /// <paramref name="unhide"/> is on only for promotion into the library. Within staging the names
    /// are left exactly as streamrip wrote them, because <see cref="Graft"/> pairs the passes by
    /// filename and renaming mid-merge would break that match.
    /// </summary>
    private static void MoveInto(string source, string destination, bool unhide = false)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            File.Move(file, Path.Combine(destination, unhide ? Unhide(name) : name), overwrite: true);
        }
        foreach (var directory in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(directory);
            MoveInto(directory, Path.Combine(destination, unhide ? Unhide(name) : name), unhide);
        }
    }
}
