using System.Text;

namespace Mycelium.Backend.Services.Archive;

/// <summary>What happened to one file between the last snapshot and this one.</summary>
public enum FileChange
{
    Added,
    Modified,
    Removed,
}

/// <summary>One changed file.</summary>
public record ArchiveChange(string Path, FileChange Change);

/// <summary>
/// Turns a snapshot into the sentence that describes it.
///
/// <para>This is what makes the archive usable as a history rather than as a backup:
/// <c>git log --oneline</c> becomes a readable account of what the library did, and nobody has to
/// diff two commits to find out whether a night mattered.</para>
///
/// <para>With a file per album, "what changed" is simply which files changed — so the summary counts
/// albums and artists directly, in the language of the thing being described.</para>
/// </summary>
public static class ArchiveDelta
{
    /// <summary>
    /// The commit message: a one-line summary that reads well in <c>--oneline</c>, then the changed
    /// paths underneath.
    /// </summary>
    public static string CommitMessage(DateOnly date, IReadOnlyList<ArchiveChange> changes)
    {
        var builder = new StringBuilder();
        builder.Append("snapshot ").Append(date.ToString("yyyy-MM-dd"));

        var headline = Headline(changes);
        if (headline.Length > 0)
        {
            builder.Append(" — ").Append(headline);
        }

        builder.Append("\n\n");

        // Capped so a first run, or a sweep that touches every album, can't produce a commit message
        // thousands of lines long.
        const int maxLines = 25;
        var listed = changes
            .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Path, StringComparer.Ordinal)
            .ToList();

        foreach (var change in listed.Take(maxLines))
        {
            builder.Append("  ").Append(Marker(change.Change)).Append(' ').Append(change.Path).Append('\n');
        }

        if (listed.Count > maxLines)
        {
            builder.Append("  ...and ").Append(listed.Count - maxLines).Append(" more\n");
        }

        return builder.ToString();
    }

    private static string Headline(IReadOnlyList<ArchiveChange> changes)
    {
        var parts = new List<string>();

        void Note(string noun, Func<ArchiveChange, bool> match)
        {
            var total = changes.Count(match);
            if (total > 0)
            {
                parts.Add($"{total} {noun}{(total == 1 ? "" : "s")}");
            }
        }

        // An artist's own file changing is a different event from one of their albums changing, so the
        // two are counted apart rather than lumped as "files".
        Note("album", c => IsLibrary(c) && !c.Path.EndsWith("/metadata.yaml", StringComparison.Ordinal));
        Note("artist", c => IsLibrary(c) && c.Path.EndsWith("/metadata.yaml", StringComparison.Ordinal));
        Note("playlist file", c => c.Path.StartsWith("playlists/", StringComparison.Ordinal));
        Note("decision file", c => c.Path == "decisions.yaml");
        Note("user file", c => c.Path == "users.yaml");

        return string.Join(", ", parts);
    }

    private static bool IsLibrary(ArchiveChange change) =>
        change.Path.StartsWith("Library/", StringComparison.Ordinal);

    private static char Marker(FileChange change) => change switch
    {
        FileChange.Added => '+',
        FileChange.Removed => '-',
        _ => '~',
    };
}
