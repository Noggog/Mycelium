using FluentAssertions;
using Mycelium.Backend.Services.Archive;
using Xunit;

namespace Mycelium.Tests;

public class ArchiveDeltaTests
{
    private static ArchiveChange Album(string artist, string album, FileChange change = FileChange.Modified) =>
        new($"Library/{artist}/{album}.yaml", change);

    private static ArchiveChange Artist(string artist, FileChange change = FileChange.Modified) =>
        new($"Library/{artist}/metadata.yaml", change);

    [Fact]
    public void The_message_leads_with_a_summary_that_reads_in_git_log()
    {
        var message = ArchiveDelta.CommitMessage(
            new DateOnly(2026, 8, 31),
            [
                Album("Radiohead", "Kid A"),
                Album("Radiohead", "Amnesiac", FileChange.Added),
                Artist("Portishead"),
                new ArchiveChange("users.yaml", FileChange.Modified),
            ]);

        message.Split('\n')[0].Should().Be("snapshot 2026-08-31 — 2 albums, 1 artist, 1 user file");
    }

    [Fact]
    public void An_artist_file_is_counted_apart_from_its_albums()
    {
        // "3 albums changed" and "3 artists changed" are different events; lumping them as "files"
        // would lose the distinction that makes the log readable.
        var message = ArchiveDelta.CommitMessage(
            new DateOnly(2026, 8, 31), [Artist("Radiohead"), Album("Radiohead", "Kid A")]);

        message.Should().Contain("1 album, 1 artist");
    }

    [Fact]
    public void Singular_and_plural_are_both_right()
    {
        // This line is read in `git log --oneline`; "1 albums" reads like a bug in whatever wrote it.
        ArchiveDelta.CommitMessage(new DateOnly(2026, 8, 31), [Album("A", "B")])
            .Should().Contain("1 album").And.NotContain("1 albums");

        ArchiveDelta.CommitMessage(new DateOnly(2026, 8, 31), [Album("A", "B"), Album("A", "C")])
            .Should().Contain("2 albums");
    }

    [Fact]
    public void The_body_marks_what_happened_to_each_file()
    {
        var message = ArchiveDelta.CommitMessage(
            new DateOnly(2026, 8, 31),
            [
                Album("Radiohead", "Kid A", FileChange.Added),
                Album("Radiohead", "Pablo Honey", FileChange.Removed),
                Album("Portishead", "Dummy"),
            ]);

        message.Should().Contain("+ Library/Radiohead/Kid A.yaml");
        message.Should().Contain("- Library/Radiohead/Pablo Honey.yaml");
        message.Should().Contain("~ Library/Portishead/Dummy.yaml");
    }

    [Fact]
    public void Nothing_changed_produces_a_bare_subject()
    {
        // Not normally committed at all — the archiver skips an unchanged tree — but the message must
        // still be sane if it ever is.
        ArchiveDelta.CommitMessage(new DateOnly(2026, 8, 31), [])
            .Split('\n')[0].Should().Be("snapshot 2026-08-31");
    }

    [Fact]
    public void A_huge_first_run_does_not_produce_a_thousand_line_commit_message()
    {
        var changes = Enumerable.Range(0, 200)
            .Select(i => Album("Artist", $"Album {i:D3}", FileChange.Added))
            .ToList();

        var message = ArchiveDelta.CommitMessage(new DateOnly(2026, 8, 31), changes);

        message.Split('\n').Should().HaveCountLessThan(35);
        message.Should().Contain("and 175 more");
    }
}
