using FluentAssertions;
using Mycelium.Backend.Services.Archive;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The commit subject, which is the whole message.
///
/// <para>Its job is the part git can't derive on its own: that a file under <c>Library/</c> is an
/// album rather than an artist, and how much of each moved. Everything git already knows — which
/// paths, added or removed, when — is left to git, so <c>git log --oneline</c> stays scannable and
/// <c>--name-status</c> remains the authority on detail.</para>
/// </summary>
public class ArchiveDeltaTests
{
    private static ArchiveChange Album(string artist, string album, FileChange change = FileChange.Modified) =>
        new($"Library/{artist}/{album}.yaml", change);

    private static ArchiveChange Artist(string artist, FileChange change = FileChange.Modified) =>
        new($"Library/{artist}/metadata.yaml", change);

    [Fact]
    public void The_whole_message_is_one_line_that_reads_in_git_log()
    {
        var message = ArchiveDelta.CommitMessage(
            [
                Album("Radiohead", "Kid A"),
                Album("Radiohead", "Amnesiac", FileChange.Added),
                Artist("Portishead"),
                new ArchiveChange("users.yaml", FileChange.Modified),
            ]);

        message.Should().Be("2 albums, 1 artist, users");
    }

    [Fact]
    public void Nothing_that_git_already_records_is_repeated()
    {
        // The message used to list every changed path with a +/-/~ marker. That is precisely what
        // `git log --name-status` prints, without a truncation cap and without any chance of
        // disagreeing with what was actually committed.
        var message = ArchiveDelta.CommitMessage(
            [
                Album("Radiohead", "Kid A", FileChange.Added),
                Album("Radiohead", "Pablo Honey", FileChange.Removed),
            ]);

        message.Should().NotContain("\n");
        message.Should().NotContain("Library/");
    }

    [Fact]
    public void An_artist_file_is_counted_apart_from_its_albums()
    {
        // "3 albums changed" and "3 artists changed" are different events; lumping them as "files"
        // would lose the distinction that makes the log readable — and is the one thing here git
        // cannot work out for itself.
        ArchiveDelta.CommitMessage([Artist("Radiohead"), Album("Radiohead", "Kid A")])
            .Should().Be("1 album, 1 artist");
    }

    [Fact]
    public void Singular_and_plural_are_both_right()
    {
        // This line is read in `git log --oneline`; "1 albums" reads like a bug in whatever wrote it.
        ArchiveDelta.CommitMessage([Album("A", "B")])
            .Should().Contain("1 album").And.NotContain("1 albums");

        ArchiveDelta.CommitMessage([Album("A", "B"), Album("A", "C")]).Should().Contain("2 albums");
    }

    [Fact]
    public void The_single_file_sections_are_named_rather_than_counted()
    {
        // There is exactly one decisions.yaml and one users.yaml, so "1 user file" spends three words
        // to say what one says, and the count could never be anything else.
        ArchiveDelta.CommitMessage(
            [
                new ArchiveChange("decisions.yaml", FileChange.Modified),
                new ArchiveChange("users.yaml", FileChange.Modified),
            ])
            .Should().Be("decisions, users");
    }

    [Fact]
    public void Playlists_are_counted_by_file_because_that_is_per_person()
    {
        // One file per person, so two changed files is two people's playlists moving — not two
        // playlists, which the count would otherwise imply.
        ArchiveDelta.CommitMessage(
            [
                new ArchiveChange("playlists/kelsey.yaml", FileChange.Modified),
                new ArchiveChange("playlists/noggog.yaml", FileChange.Added),
            ])
            .Should().Be("2 playlist files");
    }

    [Fact]
    public void Nothing_changed_still_produces_a_sane_subject()
    {
        // Not normally committed at all — the archiver skips an unchanged tree — but an empty subject
        // is a commit git will refuse.
        ArchiveDelta.CommitMessage([]).Should().Be("snapshot");
    }

    [Fact]
    public void A_huge_first_run_is_still_one_short_line()
    {
        var changes = Enumerable.Range(0, 200)
            .Select(i => Album("Artist", $"Album {i:D3}", FileChange.Added))
            .ToList();

        ArchiveDelta.CommitMessage(changes).Should().Be("200 albums");
    }
}
