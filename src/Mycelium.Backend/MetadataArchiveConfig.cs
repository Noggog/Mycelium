using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Mycelium.Backend;

/// <summary>
/// Where the metadata archive lives and who it commits as. Read once from the environment in
/// <see cref="MainModule"/> and registered as an instance, so the archive services stay env-free and
/// unit-testable — the same shape as <see cref="DailySyncSchedule"/> and the download config.
///
/// <para>Declared in the root namespace, not in <c>Services/Archive</c>, so the Autofac assembly scan
/// can't shadow the registered instance with a reflected one it has no constructor arguments for. See
/// the note on <c>LibraryScannerConfig</c>.</para>
/// </summary>
/// <param name="RepoPath">
/// The archive checkout. Null or empty turns the whole feature off — the app runs exactly as it did
/// before, which is what should happen on a deployment that never configured it.
/// </param>
/// <param name="Remote">Optional push target. Absent means commit locally and stop.</param>
/// <param name="Branch">Branch to commit on; created on first run.</param>
/// <param name="SnapshotAt">
/// Wall-clock hour of the nightly pass. Defaulted past the catalog and album syncs so it archives a
/// freshly-synced library rather than racing it.
/// </param>
/// <param name="GitBinary">Resolved on PATH unless overridden.</param>
/// <param name="CommandTimeout">
/// Per-git-invocation ceiling. Generous: the only slow command is a push over a slow link, and a
/// hung git must not wedge a background service forever.
/// </param>
public record MetadataArchiveConfig(
    string? RepoPath,
    string? Remote,
    string Branch,
    TimeOnly SnapshotAt,
    string CommitName,
    string CommitEmail,
    string GitBinary,
    TimeSpan CommandTimeout)
{
    /// <summary>Whether archiving is configured at all.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(RepoPath);

    /// <summary>
    /// The remote with any embedded credential stripped — <c>https://user:token@host/repo.git</c>
    /// becomes <c>https://***@host/repo.git</c>.
    ///
    /// <para>The ordinary way to authenticate a push to a self-hosted forge is to put an access token
    /// in the URL, which means <see cref="Remote"/> is a secret. Logs are rolled to disk and read by
    /// people who are debugging something else, so nothing may print it as-is. Use this anywhere a
    /// remote reaches a log, a response body or an exception message.</para>
    /// </summary>
    public string? SafeRemote => Redact(Remote);

    /// <summary>
    /// Strips <c>user:password@</c> out of any URLs in <paramref name="text"/>. Applied to whole
    /// strings rather than just to the configured remote, because git echoes the URL back in its own
    /// error output — so a push failure would otherwise leak what the config was careful not to.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Redact(string? text) =>
        text is null ? null : CredentialInUrl.Replace(text, "$1***@");

    // Matches the "scheme://" then everything up to an "@" that isn't a slash or whitespace.
    private static readonly Regex CredentialInUrl =
        new(@"([a-zA-Z][a-zA-Z0-9+.\-]*://)[^/@\s]+@", RegexOptions.Compiled);
}
