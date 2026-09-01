using System.Diagnostics;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend;
using Mycelium.Backend.Services.Archive;
using Mycelium.Interfaces;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// End-to-end over a real git repository in a temp directory. Worth doing for real rather than
/// against a mock: the behaviour that matters here — that an unchanged night commits nothing — lives
/// in git's exit codes, not in our code, and a mocked <see cref="IGitRepository"/> would assert only
/// that we believe what we already wrote.
/// </summary>
public sealed class MetadataArchiverTests : IDisposable
{
    private readonly string _repo = Path.Combine(
        Path.GetTempPath(), "mycelium-archive-tests", Guid.NewGuid().ToString("N"));

    private MetadataArchiveConfig Config() => new(
        RepoPath: _repo,
        Remote: null,
        Branch: "main",
        SnapshotAt: new TimeOnly(8, 0),
        CommitName: "Mycelium Tests",
        CommitEmail: "tests@localhost",
        GitBinary: "git",
        CommandTimeout: TimeSpan.FromMinutes(1));

    private MetadataArchiver Archiver(FakeArchiveDump dump)
    {
        var config = Config();
        return new MetadataArchiver(
            dump,
            new GitRepository(config, NullLogger<GitRepository>.Instance),
            new ArchiveBuilder(),
            config,
            NullLogger<MetadataArchiver>.Instance);
    }

    private static FakeArchiveDump Dump() => new FakeArchiveDump()
        .Set("users", new JsonObject { ["_id"] = "sub-1", ["username"] = "kelsey" })
        .Set("artists", new JsonObject
        {
            ["_id"] = "Radiohead",
            ["albums"] = new JsonArray("Kid A"),
        });

    private string Git(params string[] args) => RunGit(_repo, args);

    private static string RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    [Fact]
    public async Task A_first_snapshot_creates_the_repository_and_commits()
    {
        var result = await Archiver(Dump()).Snapshot();

        result.Outcome.Should().Be(GitOutcome.Committed);
        result.CommitSha.Should().NotBeNullOrWhiteSpace();

        File.Exists(Path.Combine(_repo, "users.yaml")).Should().BeTrue();
        File.Exists(Path.Combine(_repo, "decisions.yaml")).Should().BeTrue();
        File.Exists(Path.Combine(_repo, "Library", "Radiohead", "metadata.yaml")).Should().BeTrue();
        File.Exists(Path.Combine(_repo, "Library", "Radiohead", "Kid A.yaml")).Should().BeTrue();

        Git("log", "--oneline").Should().Contain("snapshot");
    }

    [Fact]
    public async Task A_night_where_nothing_changed_commits_nothing()
    {
        // The rule the whole history hangs on. Without it there would be 365 empty commits a year and
        // the real changes would be unfindable.
        var dump = Dump();
        await Archiver(dump).Snapshot();

        var second = await Archiver(dump).Snapshot();

        second.Outcome.Should().Be(GitOutcome.NoChanges);
        Git("log", "--oneline").Trim().Split('\n').Should().HaveCount(1);
    }

    [Fact]
    public async Task A_changed_verdict_produces_exactly_one_more_commit()
    {
        var dump = Dump();
        await Archiver(dump).Snapshot();

        dump.Set("userQueue", new JsonObject
        {
            ["userId"] = "sub-1",
            ["artist"] = "Radiohead",
            ["status"] = "Liked",
        });

        var result = await Archiver(dump).Snapshot();

        result.Outcome.Should().Be(GitOutcome.Committed);
        Git("log", "--oneline").Trim().Split('\n').Should().HaveCount(2);
        File.ReadAllText(Path.Combine(_repo, "Library", "Radiohead", "metadata.yaml"))
            .Should().Contain("kelsey").And.Contain("Liked");
    }

    [Fact]
    public async Task The_commit_message_says_what_changed()
    {
        var dump = Dump();
        await Archiver(dump).Snapshot();

        dump.Set("purchases", new JsonObject
        {
            ["artist"] = "Radiohead",
            ["album"] = "Kid A",
            ["addedBy"] = "kelsey",
            ["sentAt"] = "2026-08-25T08:31:00Z",
        });
        await Archiver(dump).Snapshot();

        // git log is meant to be the readable history, not a list of identical subjects.
        Git("log", "-1", "--pretty=%s").Should().Contain("1 album");
        Git("log", "-1", "--pretty=%b").Should().Contain("Library/Radiohead/Kid A.yaml");
    }

    [Fact]
    public async Task An_album_that_leaves_the_library_leaves_the_archive()
    {
        // A record sold or deleted should stop appearing. The removal is still in the history, which is
        // the point of keeping this in git.
        var dump = Dump();
        await Archiver(dump).Snapshot();
        File.Exists(Path.Combine(_repo, "Library", "Radiohead", "Kid A.yaml")).Should().BeTrue();

        dump.Set("artists", new JsonObject { ["_id"] = "Radiohead", ["albums"] = new JsonArray() });
        await Archiver(dump).Snapshot();

        File.Exists(Path.Combine(_repo, "Library", "Radiohead", "Kid A.yaml")).Should().BeFalse();
        // ...but the artist stays: their identity pins and everyone's verdicts still matter.
        File.Exists(Path.Combine(_repo, "Library", "Radiohead", "metadata.yaml")).Should().BeTrue();
    }

    [Fact]
    public async Task An_artist_who_leaves_entirely_takes_their_directory_with_them()
    {
        var dump = Dump();
        await Archiver(dump).Snapshot();

        dump.Set("artists");
        await Archiver(dump).Snapshot();

        Directory.Exists(Path.Combine(_repo, "Library", "Radiohead")).Should().BeFalse();
    }

    [Fact]
    public async Task The_archive_explains_its_own_format()
    {
        // Whoever reads this repository may have no access to the code that wrote it.
        await Archiver(Dump()).Snapshot();

        var readme = File.ReadAllText(Path.Combine(_repo, "README.md"));
        readme.Should().Contain("Library/");
        readme.Should().Contain("username");
        readme.Should().Contain("MusicBrainz");
        readme.Should().Contain("Keep it private");
    }

    [Fact]
    public async Task A_readme_that_already_exists_is_never_overwritten()
    {
        // Once it's in the repository it belongs to whoever owns the repository.
        await Archiver(Dump()).Snapshot();
        var readme = Path.Combine(_repo, "README.md");
        File.WriteAllText(readme, "# Mine\n");

        await Archiver(Dump().Set("artists", new JsonObject { ["_id"] = "ABBA" })).Snapshot();

        File.ReadAllText(readme).Should().Be("# Mine\n");
    }

    [Fact]
    public async Task Files_the_archive_does_not_own_are_left_alone()
    {
        // The repository is the user's, not ours. A README or notes they add must survive a snapshot.
        await Archiver(Dump()).Snapshot();
        var readme = Path.Combine(_repo, "README.md");
        File.WriteAllText(readme, "# My archive\n");

        await Archiver(Dump().Set("artists", new JsonObject { ["_id"] = "ABBA" })).Snapshot();

        File.Exists(readme).Should().BeTrue();
    }

    [Fact]
    public async Task A_configured_remote_actually_receives_the_commit()
    {
        // The push path is otherwise never exercised — every other test runs with no remote. A bare
        // repository on disk stands in for Forgejo: same protocol handshake from git's point of view,
        // minus the network and the credentials.
        var remote = Path.Combine(_repo + "-remote.git");
        Directory.CreateDirectory(remote);
        RunGit(remote, "init", "--bare", "--initial-branch", "main");

        var config = Config() with { Remote = remote };
        var archiver = new MetadataArchiver(
            Dump(),
            new GitRepository(config, NullLogger<GitRepository>.Instance),
            new ArchiveBuilder(),
            config,
            NullLogger<MetadataArchiver>.Instance);

        var result = await archiver.Snapshot();

        result.Outcome.Should().Be(GitOutcome.Committed);
        result.Pushed.Should().BeTrue();
        RunGit(remote, "log", "--oneline").Should().Contain("snapshot");

        Directory.Delete(remote, recursive: true);
    }

    [Fact]
    public async Task An_unreachable_remote_still_commits_locally()
    {
        // The local repository is already a complete copy, so a remote that's down is a degraded state
        // and not a failed snapshot. Nothing about the archive may depend on the network.
        //
        // A path that isn't a repository rather than an unroutable address: git rejects it instantly,
        // where a dead IP would sit until the command timeout and put a minute on every test run. The
        // code path under test is the same — push exits non-zero and the snapshot carries on.
        var config = Config() with
        {
            Remote = Path.Combine(_repo + "-absent.git"),
            CommandTimeout = TimeSpan.FromSeconds(20),
        };
        var archiver = new MetadataArchiver(
            Dump(),
            new GitRepository(config, NullLogger<GitRepository>.Instance),
            new ArchiveBuilder(),
            config,
            NullLogger<MetadataArchiver>.Instance);

        var result = await archiver.Snapshot();

        result.Outcome.Should().Be(GitOutcome.Committed);
        result.Pushed.Should().BeFalse();
        Git("log", "--oneline").Should().Contain("snapshot");
    }

    [Fact]
    public async Task No_archive_path_configured_means_the_feature_is_simply_off()
    {
        var config = Config() with { RepoPath = null };
        var archiver = new MetadataArchiver(
            Dump(),
            new GitRepository(config, NullLogger<GitRepository>.Instance),
            new ArchiveBuilder(),
            config,
            NullLogger<MetadataArchiver>.Instance);

        var result = await archiver.Snapshot();

        result.Outcome.Should().Be(GitOutcome.Failed);
        Directory.Exists(_repo).Should().BeFalse();
    }

    [Fact]
    public async Task A_secret_never_reaches_the_repository()
    {
        // Belt and braces over the builder's own test: git history is forever, so this is checked
        // against what actually landed on disk.
        var dump = Dump().Set("plexLinks", new JsonObject
        {
            ["_id"] = "sub-1",
            ["accountId"] = "plex-99",
            ["serverToken"] = "SECRET-TOKEN-VALUE",
        });

        await Archiver(dump).Snapshot();

        foreach (var file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.Combine(_repo, ".git"))))
        {
            File.ReadAllText(file).Should().NotContain("SECRET-TOKEN-VALUE");
        }
    }

    public void Dispose()
    {
        if (!Directory.Exists(_repo))
        {
            return;
        }

        try
        {
            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
