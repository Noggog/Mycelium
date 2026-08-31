using FluentAssertions;
using Mycelium.Backend.Services.Archive;
using Xunit;

namespace Mycelium.Tests;

public class ArchiveDeltaTests
{
    private static ArchiveFile Inventory(params string[] lines) =>
        new("inventory.jsonl", string.Concat(lines.Select(l => l + "\n")), ["artist"]);

    private static string Artist(string name, bool present = true) =>
        $$"""{"artist": "{{name}}", "present": {{(present ? "true" : "false")}}}""";

    [Fact]
    public void An_edited_record_counts_as_one_change_not_an_add_and_a_remove()
    {
        // The reason a delta keys records instead of counting lines. "1 changed" is the truth;
        // "1 added, 1 removed" would read as an artist having been swapped for another.
        var previous = Inventory(Artist("Radiohead")).Contents;
        var current = Inventory(Artist("Radiohead", present: false));

        var delta = ArchiveDelta.Compare(current, previous);

        delta.Should().Be(new FileDelta("inventory.jsonl", Added: 0, Changed: 1, Removed: 0));
    }

    [Fact]
    public void Additions_and_removals_are_counted_separately()
    {
        var previous = Inventory(Artist("ABBA"), Artist("Radiohead")).Contents;
        var current = Inventory(Artist("ABBA"), Artist("Zappa"));

        var delta = ArchiveDelta.Compare(current, previous);

        delta.Added.Should().Be(1);
        delta.Removed.Should().Be(1);
        delta.Changed.Should().Be(0);
    }

    [Fact]
    public void A_first_run_reports_everything_as_added()
    {
        var delta = ArchiveDelta.Compare(Inventory(Artist("ABBA"), Artist("Zappa")), previous: null);

        delta.Added.Should().Be(2);
        delta.Any.Should().BeTrue();
    }

    [Fact]
    public void An_unchanged_file_reports_nothing()
    {
        var contents = Inventory(Artist("ABBA")).Contents;

        ArchiveDelta.Compare(Inventory(Artist("ABBA")), contents).Any.Should().BeFalse();
    }

    [Fact]
    public void A_malformed_leftover_line_does_not_stop_the_summary()
    {
        // The summary is a convenience. A stray line from an older schema must never be the reason a
        // night's snapshot doesn't happen.
        var delta = ArchiveDelta.Compare(Inventory(Artist("ABBA")), "this is not json\n");

        delta.Added.Should().Be(1);
    }

    [Fact]
    public void The_message_leads_with_a_summary_that_reads_in_git_log()
    {
        var message = ArchiveDelta.CommitMessage(
            new DateOnly(2026, 8, 25),
            [
                new FileDelta("inventory.jsonl", 12, 0, 0),
                new FileDelta("taste/kelsey.jsonl", 4, 1, 0),
                new FileDelta("downloads.jsonl", 3, 0, 0),
            ]);

        var subject = message.Split('\n')[0];
        subject.Should().Be("snapshot 2026-08-25 — 12 artists, 5 verdicts, 3 downloads");

        // ...and the detail underneath, so a commit explains itself without a diff.
        message.Should().Contain("taste/kelsey.jsonl").And.Contain("+4 ~1");
    }

    [Fact]
    public void Files_that_did_not_change_are_left_out_of_the_message()
    {
        var message = ArchiveDelta.CommitMessage(
            new DateOnly(2026, 8, 25),
            [
                new FileDelta("inventory.jsonl", 1, 0, 0),
                new FileDelta("downloads.jsonl", 0, 0, 0),
            ]);

        message.Should().Contain("inventory.jsonl");
        message.Should().NotContain("downloads.jsonl");
    }

    [Fact]
    public void A_huge_first_run_does_not_produce_a_thousand_line_commit_message()
    {
        var deltas = Enumerable.Range(0, 60)
            .Select(i => new FileDelta($"taste/user{i:D2}.jsonl", 1, 0, 0))
            .ToList();

        var message = ArchiveDelta.CommitMessage(new DateOnly(2026, 8, 25), deltas);

        message.Split('\n').Should().HaveCountLessThan(30);
        message.Should().Contain("and 40 more");
    }
}
