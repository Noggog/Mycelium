using System.Text;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Archive;

/// <summary>The outcome of one snapshot, for logging and for the dev panel.</summary>
public record ArchiveResult(GitOutcome Outcome, string? CommitSha = null, bool Pushed = false, string? Error = null)
{
    public static ArchiveResult Disabled => new(GitOutcome.Failed, Error: "Archiving is not configured");
}

/// <summary>
/// Writes the metadata archive: reads the collections worth keeping, shapes them into files, and
/// commits the result if anything moved.
///
/// <para>Deliberately a pure Mongo→git path. Plex-owned data (star ratings, playlists) is harvested
/// into Mongo by its own pass rather than being read here, so this stays one source, one cadence, one
/// failure mode — and so a Plex outage can't stop the rest of the archive being taken.</para>
///
/// <para>Nothing here throws. A snapshot that fails is logged and retried on the next tick; the app's
/// own behaviour is unaffected either way, because nothing reads the archive back.</para>
/// </summary>
public class MetadataArchiver
{
    /// <summary>
    /// Directories the archive owns outright, so a file that stops being produced (a user who was
    /// deleted, say) is removed rather than left behind for ever. Anything else in the repository —
    /// a README, notes, whatever else you put there — is left alone.
    /// </summary>
    private static readonly string[] ManagedDirectories = ["Library", "playlists"];

    /// <summary>Top-level files the archive owns. Anything else in the repository is left alone.</summary>
    private static readonly string[] ManagedFiles = ["users.yaml", "decisions.yaml"];

    private readonly IArchiveDump _dump;
    private readonly IGitRepository _git;
    private readonly ArchiveBuilder _builder;
    private readonly MetadataArchiveConfig _config;
    private readonly ILogger<MetadataArchiver> _logger;

    public MetadataArchiver(
        IArchiveDump dump,
        IGitRepository git,
        ArchiveBuilder builder,
        MetadataArchiveConfig config,
        ILogger<MetadataArchiver> logger)
    {
        _dump = dump;
        _git = git;
        _builder = builder;
        _config = config;
        _logger = logger;
    }

    /// <summary>Takes one snapshot. Public so it can be unit-tested and triggered without the timer.</summary>
    public async Task<ArchiveResult> Snapshot()
    {
        if (!_config.Enabled)
        {
            return ArchiveResult.Disabled;
        }

        try
        {
            if (!await _git.EnsureInitialized())
            {
                return new ArchiveResult(GitOutcome.Failed, Error: "Archive repository is not usable");
            }

            var files = _builder.Build(await ArchiveCollections.Read(_dump));

            // Written once, on the first snapshot that finds it missing. The archive is meant to be
            // read by something that isn't this app — a migration script for whatever replaces Plex,
            // written by someone without this codebase — and a key to the format is what makes it a
            // record rather than a pile of JSON.
            WriteReadmeIfAbsent();

            // Compared before writing, so the summary describes the change rather than the result.
            var changes = Changes(files);

            Write(files);
            changes.AddRange(Prune(files));

            var message = ArchiveDelta.CommitMessage(DateOnly.FromDateTime(DateTime.Now), changes);
            var commit = await _git.CommitAll(message);

            switch (commit.Outcome)
            {
                case GitOutcome.NoChanges:
                    _logger.LogInformation("Metadata archive: nothing changed since the last snapshot");
                    break;
                case GitOutcome.Committed:
                    _logger.LogInformation(
                        "Metadata archive: committed {Sha} ({Count} file(s) changed); pushed={Pushed}",
                        commit.CommitSha?[..Math.Min(8, commit.CommitSha.Length)],
                        changes.Count, commit.Pushed);
                    break;
                default:
                    _logger.LogError("Metadata archive: commit failed — {Error}", commit.Error);
                    break;
            }

            return new ArchiveResult(commit.Outcome, commit.CommitSha, commit.Pushed, commit.Error);
        }
        catch (Exception ex)
        {
            // A failed snapshot is a missed night, not an outage: the next tick retries against the
            // same data, and nothing in the app reads the archive back.
            _logger.LogError(ex, "Metadata archive snapshot failed; will retry on the next pass");
            return new ArchiveResult(GitOutcome.Failed, Error: ex.Message);
        }
    }

    /// <summary>
    /// Never overwrites: once the README is in the repository it belongs to whoever owns it, and they
    /// may have added to it.
    /// </summary>
    private void WriteReadmeIfAbsent()
    {
        var path = Path.Combine(_config.RepoPath!, ArchiveReadme.FileName);
        if (File.Exists(path))
        {
            return;
        }

        try
        {
            File.WriteAllText(
                path, ArchiveReadme.Contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            // Explanatory, not load-bearing — a snapshot without it is still a good snapshot.
            _logger.LogWarning(ex, "Metadata archive: could not write {File}", ArchiveReadme.FileName);
        }
    }

    private string? ReadExisting(string relativePath)
    {
        var path = Path.Combine(_config.RepoPath!, relativePath);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private void Write(IReadOnlyList<ArchiveFile> files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(_config.RepoPath!, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // UTF-8 without a BOM, and LF endings from the builder. Both matter: a BOM or a CRLF pass
            // would rewrite every file the first time the archive ran on a different host.
            File.WriteAllText(path, file.Contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    /// <summary>
    /// Removes archive files that this snapshot no longer produces — a user whose account was deleted,
    /// say. Scoped to the directories the archive owns plus its own top-level files, so anything else
    /// in the repository is never touched.
    /// </summary>
    private List<ArchiveChange> Prune(IReadOnlyList<ArchiveFile> files)
    {
        var produced = files
            .Select(f => Path.Combine(_config.RepoPath!, f.RelativePath))
            .ToHashSet(StringComparer.Ordinal);

        var candidates = new List<string>();
        foreach (var name in ManagedFiles)
        {
            var path = Path.Combine(_config.RepoPath!, name);
            if (File.Exists(path))
            {
                candidates.Add(path);
            }
        }

        foreach (var directory in ManagedDirectories)
        {
            var path = Path.Combine(_config.RepoPath!, directory);
            if (Directory.Exists(path))
            {
                candidates.AddRange(Directory.EnumerateFiles(path, "*.yaml", SearchOption.AllDirectories));
            }
        }

        var removed = new List<ArchiveChange>();
        foreach (var stale in candidates.Where(c => !produced.Contains(c)))
        {
            try
            {
                File.Delete(stale);
                removed.Add(new ArchiveChange(Relative(stale), FileChange.Removed));
                _logger.LogInformation("Metadata archive: removed stale file {File}", stale);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Metadata archive: could not remove stale file {File}", stale);
            }
        }

        // An artist whose last album went away leaves an empty directory behind, which git won't track
        // but which clutters the tree for anyone browsing it.
        foreach (var directory in ManagedDirectories)
        {
            var root = Path.Combine(_config.RepoPath!, directory);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var child in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(child).Any())
                    {
                        Directory.Delete(child);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Metadata archive: could not remove empty directory {Dir}", child);
                }
            }
        }

        return removed;
    }

    private string Relative(string absolute) =>
        Path.GetRelativePath(_config.RepoPath!, absolute).Replace('\\', '/');

    /// <summary>
    /// Which of the files about to be written differ from what is already on disk. Whole-file
    /// comparison, which is all that's needed now every record is its own document.
    /// </summary>
    private List<ArchiveChange> Changes(IReadOnlyList<ArchiveFile> files)
    {
        var changes = new List<ArchiveChange>();
        foreach (var file in files)
        {
            var existing = ReadExisting(file.RelativePath);
            if (existing is null)
            {
                changes.Add(new ArchiveChange(file.RelativePath, FileChange.Added));
            }
            else if (!string.Equals(existing, file.Contents, StringComparison.Ordinal))
            {
                changes.Add(new ArchiveChange(file.RelativePath, FileChange.Modified));
            }
        }

        return changes;
    }
}
