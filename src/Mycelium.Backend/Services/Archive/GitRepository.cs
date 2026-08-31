using System.Diagnostics;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Archive;

/// <summary>
/// <see cref="IGitRepository"/> over the real <c>git</c> binary.
///
/// <para>A CLI rather than an embedded library, because the archive's whole promise is that the data
/// is ours in a form we can work by hand — which only holds if what's on disk is an ordinary
/// repository anyone can walk into and <c>git log</c> or <c>git revert</c> with no special tooling.
/// The cost is one package in the image.</para>
///
/// <para>Process handling follows the shape <c>StreamripDownloader</c> established, and for the same
/// reasons: arguments go through <see cref="ProcessStartInfo.ArgumentList"/> so quoting can never
/// bite, both pipes are read concurrently so a full buffer can't deadlock the child, every call is
/// bounded by a timeout, and nothing throws — a broken archive logs and is skipped, it does not take
/// the app down.</para>
/// </summary>
public class GitRepository : IGitRepository
{
    private readonly MetadataArchiveConfig _config;
    private readonly ILogger<GitRepository> _logger;

    public GitRepository(MetadataArchiveConfig config, ILogger<GitRepository> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<bool> EnsureInitialized()
    {
        if (!_config.Enabled)
        {
            return false;
        }

        var path = _config.RepoPath!;
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metadata archive path {Path} could not be created", path);
            return false;
        }

        if (!Directory.Exists(Path.Combine(path, ".git")))
        {
            _logger.LogInformation("Initialising metadata archive at {Path} on branch {Branch}", path, _config.Branch);
            var init = await Run("init", "--initial-branch", _config.Branch);
            if (!init.Ok)
            {
                _logger.LogError("Could not initialise the metadata archive: {Error}", init.Error);
                return false;
            }
        }

        // A repository on a bind mount is usually owned by a different uid than the one this process
        // runs as, and git refuses to touch a repo it thinks belongs to someone else. Declaring it safe
        // is the documented answer, and it has to happen before any other command rather than being
        // discovered in production.
        var safe = await Run("config", "--local", "--replace-all", "safe.directory", path);
        if (!safe.Ok)
        {
            _logger.LogWarning("Could not mark {Path} as a safe git directory: {Error}", path, safe.Error);
        }

        // Commit identity lives in the repo's own config: there is no global gitconfig in the container,
        // and without one `git commit` fails outright.
        await Run("config", "--local", "user.name", _config.CommitName);
        await Run("config", "--local", "user.email", _config.CommitEmail);

        if (!string.IsNullOrWhiteSpace(_config.Remote))
        {
            // set-url fails when no origin exists yet, so try add first and let whichever applies win.
            var add = await Run("remote", "add", "origin", _config.Remote!);
            if (!add.Ok)
            {
                await Run("remote", "set-url", "origin", _config.Remote!);
            }
        }

        return true;
    }

    public async Task<GitCommitResult> CommitAll(string message)
    {
        if (!_config.Enabled)
        {
            return new GitCommitResult(GitOutcome.Failed, Error: "No archive path configured");
        }

        var add = await Run("add", "--all");
        if (!add.Ok)
        {
            return new GitCommitResult(GitOutcome.Failed, Error: add.Error);
        }

        // `diff --cached --quiet` exits 1 when there is something staged. That inversion is the whole
        // gate: without it every night produces an empty commit and the real changes drown.
        var staged = await Run("diff", "--cached", "--quiet");
        if (staged.Ok)
        {
            return new GitCommitResult(GitOutcome.NoChanges);
        }

        var commit = await Run("commit", "--message", message);
        if (!commit.Ok)
        {
            return new GitCommitResult(GitOutcome.Failed, Error: commit.Error);
        }

        var sha = (await Run("rev-parse", "HEAD")).Output.Trim();
        var pushed = await Push();
        return new GitCommitResult(GitOutcome.Committed, sha, pushed);
    }

    /// <summary>
    /// Best-effort. A push failure is logged and swallowed: the local repository is already a complete
    /// copy of the archive, so an unreachable remote is a degraded state, not a failed snapshot.
    /// </summary>
    private async Task<bool> Push()
    {
        if (string.IsNullOrWhiteSpace(_config.Remote))
        {
            return false;
        }

        var push = await Run("push", "--set-upstream", "origin", _config.Branch);
        if (push.Ok)
        {
            return true;
        }

        _logger.LogWarning("Metadata archive committed locally but could not be pushed: {Error}", push.Error);
        return false;
    }

    private readonly record struct Run_(bool Ok, string Output, string Error);

    private async Task<Run_> Run(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _config.GitBinary,
            WorkingDirectory = _config.RepoPath!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // `remote add` puts the push URL — and therefore its access token — in the arguments.
        _logger.LogDebug("git {Args}", MetadataArchiveConfig.Redact(string.Join(' ', args)));

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            // Read both pipes concurrently so a full buffer can't deadlock the child.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeout = new CancellationTokenSource(_config.CommandTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogError("git {Args} timed out after {Timeout}; killing it",
                    MetadataArchiveConfig.Redact(string.Join(' ', args)), _config.CommandTimeout);
                TryKill(process);
                return new Run_(false, "", "timed out");
            }

            var (stdout, stderr) = await Capture(stdoutTask, stderrTask);
            // git echoes the remote URL back in its own failure messages, so the error is scrubbed on
            // the way out rather than at each of the call sites that log it.
            return new Run_(process.ExitCode == 0, stdout, MetadataArchiveConfig.Redact(stderr.Trim()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch git ({Bin})", _config.GitBinary);
            return new Run_(false, "", ex.Message);
        }
    }

    /// <summary>Awaits the captured output streams, tolerating either one faulting.</summary>
    private static async Task<(string Out, string Err)> Capture(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            return (await stdout, await stderr);
        }
        catch
        {
            return ("", "<unavailable>");
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not kill the git process");
        }
    }
}
