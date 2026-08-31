namespace Mycelium.Interfaces;

/// <summary>What a snapshot attempt did. <see cref="NoChanges"/> is the common case, not a failure.</summary>
public enum GitOutcome
{
    /// <summary>The working tree matched the last commit, so nothing was written.</summary>
    NoChanges,

    /// <summary>A commit was made.</summary>
    Committed,

    /// <summary>Git refused, or wasn't usable. <see cref="GitCommitResult.Error"/> says why.</summary>
    Failed,
}

/// <param name="Outcome">What happened.</param>
/// <param name="CommitSha">The new commit, when one was made.</param>
/// <param name="Pushed">
/// Whether the commit reached the remote. False with <see cref="GitOutcome.Committed"/> is a normal,
/// non-fatal state: no remote is configured, or the remote was unreachable. The local repository is
/// already a complete copy — the remote is redundancy, not the product.
/// </param>
public record GitCommitResult(GitOutcome Outcome, string? CommitSha = null, bool Pushed = false, string? Error = null);

/// <summary>
/// The seam over the <c>git</c> binary, so the archive's logic can be tested without a repository on
/// disk (and so a machine without git degrades to "archiving is off" rather than to a crash).
///
/// <para>A real <c>git</c> rather than an embedded library on purpose: the whole point of the archive
/// is that the data is ours in a format we can operate by hand, and that only holds if the repository
/// is an ordinary one you can walk into and <c>git log</c>, <c>git revert</c> or <c>git bisect</c>
/// with no special tooling.</para>
/// </summary>
public interface IGitRepository
{
    /// <summary>
    /// Makes sure the configured path is a usable repository on the configured branch, creating it on
    /// first run. False means archiving can't proceed this pass (the reason is logged) — callers skip
    /// rather than throw, so a misconfigured archive never takes the app down.
    /// </summary>
    Task<bool> EnsureInitialized();

    /// <summary>
    /// Stages everything and commits, if and only if the tree actually differs from the last commit.
    /// The emptiness check is the point: an unconditional nightly commit buries the real changes under
    /// hundreds of empty ones.
    /// </summary>
    Task<GitCommitResult> CommitAll(string message);
}
