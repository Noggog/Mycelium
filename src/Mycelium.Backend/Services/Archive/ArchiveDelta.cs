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
/// Turns a snapshot into the one line that describes it.
///
/// <para>This is what makes the archive usable as a history rather than as a backup:
/// <c>git log --oneline</c> becomes a readable account of what the library did, and nobody has to
/// diff two commits to find out whether a night mattered.</para>
///
/// <para><b>A subject and nothing else.</b> The message used to list every changed path underneath,
/// which was the one thing it had no business saying: git already records exactly that, in more
/// detail and without a cap, and <c>git log --name-status</c> or <c>--stat</c> prints it on demand.
/// Duplicating it made every commit long, put a truncation ("...and 175 more") in front of the reader
/// on the runs where the detail mattered most, and risked drifting from what was actually committed.
/// The date went with it, for the reason no tracked file carries one either: git timestamps the
/// commit.</para>
///
/// <para>What is left is the part git can't derive — that a file under <c>Library/</c> is an album
/// rather than an artist, and that a change to either is worth counting in the language of the thing
/// being described.</para>
/// </summary>
public static class ArchiveDelta
{
    /// <summary>
    /// The commit message: a single line, sized to read well in <c>--oneline</c>.
    /// </summary>
    public static string CommitMessage(IReadOnlyList<ArchiveChange> changes)
    {
        var headline = Headline(changes);
        return headline.Length > 0 ? headline : "snapshot";
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
        // One file per person, so the count is of people whose playlists moved, not of playlists.
        Note("playlist file", c => c.Path.StartsWith("playlists/", StringComparison.Ordinal));

        // These two are single files, so a count would only ever be "1" and says nothing.
        if (changes.Any(c => c.Path == "decisions.yaml"))
        {
            parts.Add("decisions");
        }

        if (changes.Any(c => c.Path == "users.yaml"))
        {
            parts.Add("users");
        }

        return string.Join(", ", parts);
    }

    private static bool IsLibrary(ArchiveChange change) =>
        change.Path.StartsWith("Library/", StringComparison.Ordinal);
}
