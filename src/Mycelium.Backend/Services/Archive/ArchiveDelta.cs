using System.Text;
using System.Text.Json.Nodes;

namespace Mycelium.Backend.Services.Archive;

/// <summary>What changed in one file between the last snapshot and this one.</summary>
public record FileDelta(string Path, int Added, int Changed, int Removed)
{
    public bool Any => Added > 0 || Changed > 0 || Removed > 0;
}

/// <summary>
/// Turns a snapshot into the sentence that describes it.
///
/// <para>Worth the effort because it's what makes the archive usable as a history rather than as a
/// backup: <c>git log --oneline</c> becomes a readable account of what the library did, and nobody
/// has to diff two commits to find out whether a night mattered.</para>
///
/// <para>Changes are counted by record identity, not by line, so editing one verdict reads as one
/// change rather than as an addition plus a removal.</para>
/// </summary>
public static class ArchiveDelta
{
    public static FileDelta Compare(ArchiveFile file, string? previous)
    {
        // A file with no key fields (the manifest) can only be reported as "changed or not" — it has no
        // records to count. It's also pure derived output, so it never drives the summary.
        if (file.KeyFields.Count == 0)
        {
            return new FileDelta(file.RelativePath, 0, 0, 0);
        }

        var before = Index(previous, file.KeyFields);
        var after = Index(file.Contents, file.KeyFields);

        var added = after.Keys.Count(k => !before.ContainsKey(k));
        var removed = before.Keys.Count(k => !after.ContainsKey(k));
        var changed = after.Count(pair =>
            before.TryGetValue(pair.Key, out var old) && !string.Equals(old, pair.Value, StringComparison.Ordinal));

        return new FileDelta(file.RelativePath, added, changed, removed);
    }

    /// <summary>
    /// The commit message: a one-line summary that reads well in <c>--oneline</c>, then the per-file
    /// detail underneath.
    /// </summary>
    public static string CommitMessage(DateOnly date, IReadOnlyList<FileDelta> deltas)
    {
        var changed = deltas.Where(d => d.Any).OrderBy(d => d.Path, StringComparer.Ordinal).ToList();

        var headline = Headline(changed);
        var builder = new StringBuilder();
        builder.Append("snapshot ").Append(date.ToString("yyyy-MM-dd"));
        if (headline.Length > 0)
        {
            builder.Append(" — ").Append(headline);
        }

        builder.Append("\n\n");

        // Capped so a first run, or a bulk cleanup that touches every user, can't produce a commit
        // message thousands of lines long.
        const int maxLines = 20;
        foreach (var delta in changed.Take(maxLines))
        {
            builder.Append("  ").Append(delta.Path.PadRight(28)).Append(Counts(delta)).Append('\n');
        }

        if (changed.Count > maxLines)
        {
            builder.Append("  ...and ").Append(changed.Count - maxLines).Append(" more file(s)\n");
        }

        return builder.ToString();
    }

    private static string Headline(IReadOnlyList<FileDelta> changed)
    {
        var parts = new List<string>();

        // Singular where it should be: this line is read in `git log --oneline`, and "1 downloads"
        // reads like a bug in the thing that wrote it.
        void Note(string noun, Func<FileDelta, bool> match)
        {
            var total = changed.Where(match).Sum(d => d.Added + d.Changed + d.Removed);
            if (total > 0)
            {
                parts.Add($"{total} {noun}{(total == 1 ? "" : "s")}");
            }
        }

        Note("artist", d => d.Path == "inventory.jsonl");
        Note("verdict", d => d.Path.StartsWith("taste/", StringComparison.Ordinal));
        Note("download", d => d.Path == "downloads.jsonl");
        Note("decision", d => d.Path == "decisions.jsonl");
        Note("user", d => d.Path == "users.jsonl");
        Note("rating", d => d.Path.StartsWith("stars/", StringComparison.Ordinal));
        Note("playlist", d => d.Path.StartsWith("playlists/", StringComparison.Ordinal));

        return string.Join(", ", parts);
    }

    private static string Counts(FileDelta delta)
    {
        var parts = new List<string>();
        if (delta.Added > 0)
        {
            parts.Add($"+{delta.Added}");
        }

        if (delta.Changed > 0)
        {
            parts.Add($"~{delta.Changed}");
        }

        if (delta.Removed > 0)
        {
            parts.Add($"-{delta.Removed}");
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Key -> line, for one file's contents. A line that won't parse is skipped rather than fatal: the
    /// summary is a convenience, and a malformed leftover from an older schema must not be allowed to
    /// stop a snapshot being taken.
    /// </summary>
    private static Dictionary<string, string> Index(string? contents, IReadOnlyList<string> keyFields)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(contents))
        {
            return result;
        }

        foreach (var line in contents.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(line) is JsonObject obj)
                {
                    result[ArchiveBuilder.KeyOf(obj, keyFields)] = line;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Ignore: see the summary above.
            }
        }

        return result;
    }
}
