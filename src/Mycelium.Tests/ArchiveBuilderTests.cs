using System.Text.Json.Nodes;
using FluentAssertions;
using Mycelium.Backend.Services.Archive;
using Xunit;

namespace Mycelium.Tests;

public class ArchiveBuilderTests
{
    private static ArchiveInput Input(
        IEnumerable<JsonObject>? users = null,
        IEnumerable<JsonObject>? plexLinks = null,
        IEnumerable<JsonObject>? artists = null,
        IEnumerable<JsonObject>? artistVerdicts = null,
        IEnumerable<JsonObject>? albumVerdicts = null,
        IEnumerable<JsonObject>? purchases = null,
        IEnumerable<JsonObject>? blocks = null,
        IEnumerable<JsonObject>? matchOverrides = null,
        IEnumerable<JsonObject>? trackRatings = null,
        IEnumerable<JsonObject>? playlists = null) =>
        new(
            (users ?? []).ToList(),
            (plexLinks ?? []).ToList(),
            (artists ?? []).ToList(),
            (artistVerdicts ?? []).ToList(),
            (albumVerdicts ?? []).ToList(),
            (purchases ?? []).ToList(),
            (blocks ?? []).ToList(),
            (matchOverrides ?? []).ToList(),
            (trackRatings ?? []).ToList(),
            (playlists ?? []).ToList());

    private static JsonObject User(string subject, string username) =>
        new() { ["_id"] = subject, ["username"] = username };

    private static string File(IReadOnlyList<ArchiveFile> files, string path) =>
        files.Single(f => f.RelativePath == path).Contents;

    // ---- credentials ----

    [Fact]
    public void A_plex_link_is_archived_without_its_token()
    {
        // serverToken is a live credential. Anything committed to git is committed for ever, and
        // re-linking is a 30-second PIN flow — so the archive records that a link exists, never the key.
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            plexLinks:
            [
                new JsonObject
                {
                    ["_id"] = "sub-1",
                    ["accountId"] = "plex-99",
                    ["username"] = "kelsey_plex",
                    ["serverToken"] = "SECRET-TOKEN-VALUE",
                },
            ]));

        var users = File(files, "users.jsonl");
        users.Should().Contain("plex-99").And.Contain("kelsey_plex");
        users.Should().NotContain("SECRET-TOKEN-VALUE");
        users.Should().NotContain("serverToken");
    }

    [Fact]
    public void Emails_are_not_archived()
    {
        // Not needed to restore anything — username is the key — and they only make the repo more
        // sensitive than it has to be.
        var user = User("sub-1", "kelsey");
        user["email"] = "kelsey@example.com";

        var files = new ArchiveBuilder().Build(Input(users: [user]));

        File(files, "users.jsonl").Should().NotContain("example.com");
    }

    [Fact]
    public void Last_login_is_not_archived()
    {
        // It moves every time anyone opens the app, so archiving it would commit noise nightly for
        // every active user and drown the changes that mattered.
        var user = User("sub-1", "kelsey");
        user["lastLoginAt"] = "2026-08-25T09:00:00Z";

        var files = new ArchiveBuilder().Build(Input(users: [user]));

        File(files, "users.jsonl").Should().NotContain("lastLoginAt");
    }

    // ---- identity ----

    [Fact]
    public void Taste_is_filed_under_the_username_not_the_oidc_subject()
    {
        // Subjects are reissued if the identity provider is rebuilt, which would orphan every rating in
        // the system. The subject is still recorded in users.jsonl so an exact restore stays possible.
        var files = new ArchiveBuilder().Build(Input(
            users: [User("8f3c-uuid-subject", "kelsey")],
            artistVerdicts:
            [
                new JsonObject
                {
                    ["userId"] = "8f3c-uuid-subject",
                    ["artist"] = "Portishead",
                    ["status"] = "Liked",
                },
            ]));

        files.Select(f => f.RelativePath).Should().Contain("taste/kelsey.jsonl");
        File(files, "users.jsonl").Should().Contain("8f3c-uuid-subject");
    }

    [Fact]
    public void Two_users_whose_names_reduce_alike_do_not_share_a_file()
    {
        // Silently merging two people's taste into one file would lose one of them outright.
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-a", "kelsey@example.com"), User("sub-b", "Kelsey")],
            artistVerdicts:
            [
                new JsonObject { ["userId"] = "sub-a", ["artist"] = "Portishead", ["status"] = "Liked" },
                new JsonObject { ["userId"] = "sub-b", ["artist"] = "Massive Attack", ["status"] = "Liked" },
            ]));

        var taste = files.Where(f => f.RelativePath.StartsWith("taste/")).ToList();
        taste.Should().HaveCount(2);
        taste.Select(f => f.RelativePath).Distinct().Should().HaveCount(2);
    }

    // ---- what is kept and what is dropped ----

    [Fact]
    public void Pending_queue_rows_are_not_taste_and_are_dropped()
    {
        // Pending is the recommendation queue, rebuilt by the replenisher from the similarity graph.
        // Archiving it would churn the file constantly while preserving nothing anyone chose.
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            artistVerdicts:
            [
                new JsonObject { ["userId"] = "sub-1", ["artist"] = "Decided", ["status"] = "Liked" },
                new JsonObject { ["userId"] = "sub-1", ["artist"] = "Suggested", ["status"] = "Pending" },
            ]));

        var taste = File(files, "taste/kelsey.jsonl");
        taste.Should().Contain("Decided");
        taste.Should().NotContain("Suggested");
    }

    [Fact]
    public void A_confirmed_verdict_keeps_its_stickiness()
    {
        // "I meant it" is a hand-made decision and nothing can re-derive it. The two directional flags
        // collapse to one because a row only has one verdict.
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            artistVerdicts:
            [
                new JsonObject
                {
                    ["userId"] = "sub-1",
                    ["artist"] = "Nickelback",
                    ["status"] = "Disliked",
                    ["dislikeConfirmed"] = true,
                },
            ]));

        File(files, "taste/kelsey.jsonl").Should().Contain("\"confirmed\": true");
    }

    [Fact]
    public void A_confirm_flag_for_the_other_direction_is_ignored()
    {
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            artistVerdicts:
            [
                new JsonObject
                {
                    ["userId"] = "sub-1",
                    ["artist"] = "Nickelback",
                    ["status"] = "Disliked",
                    ["likeConfirmed"] = true,
                },
            ]));

        File(files, "taste/kelsey.jsonl").Should().NotContain("confirmed");
    }

    [Fact]
    public void Volatile_plex_and_deezer_fields_are_dropped_from_the_inventory()
    {
        // Rating keys are server-local handles re-captured on every sync; lastSeenAt and present move
        // every sync; the fan count drifts daily. All of them would rewrite the file nightly, and none
        // means anything on new hardware.
        var files = new ArchiveBuilder().Build(Input(artists:
        [
            new JsonObject
            {
                ["_id"] = "Radiohead",
                ["present"] = true,
                ["albums"] = new JsonArray("Kid A"),
                ["lastSeenAt"] = "2026-08-25T06:00:00Z",
                ["plexRatingKeys"] = new JsonArray(1234L),
                ["deezerFans"] = 5_000_000L,
                ["musicBrainzMbid"] = "a74b1b7f-71a5-4011-9441-d0b5e4122711",
            },
        ]));

        var inventory = File(files, "inventory.jsonl");
        // What we hold is the album list, not a flag about the server that happens to hold it.
        inventory.Should().Contain("Radiohead").And.Contain("Kid A");
        // The MBID is the one identifier that is stable for ever — it is the re-keying anchor.
        inventory.Should().Contain("a74b1b7f-71a5-4011-9441-d0b5e4122711");
        inventory.Should().NotContain("lastSeenAt");
        inventory.Should().NotContain("plexRatingKeys");
        inventory.Should().NotContain("deezerFans");
        inventory.Should().NotContain("present");
    }

    [Fact]
    public void Download_attribution_is_kept()
    {
        // Mongo keeps only the current purchase row and the reconcile may delete it, so the git history
        // *is* the download history. addedBy is the only "who" on the row.
        var files = new ArchiveBuilder().Build(Input(purchases:
        [
            new JsonObject
            {
                ["_id"] = "album:radiohead kid a",
                ["artist"] = "Radiohead",
                ["album"] = "Kid A",
                ["status"] = "InLibrary",
                ["addedBy"] = "kelsey",
                ["score"] = 12.5,
            },
        ]));

        var downloads = File(files, "downloads.jsonl");
        downloads.Should().Contain("kelsey").And.Contain("InLibrary");
        // Recommendation machinery that moves on its own; it would obscure the history.
        downloads.Should().NotContain("score");
    }

    // ---- stars ----

    [Fact]
    public void Star_ratings_are_filed_per_user_and_keep_the_file_path()
    {
        // The path is the point: rating keys don't survive a Plex rebuild, so it's the only identity a
        // rating can be re-attached by on a system that never knew this server.
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            trackRatings:
            [
                new JsonObject
                {
                    ["userId"] = "sub-1",
                    ["artist"] = "Radiohead",
                    ["album"] = "Kid A",
                    ["title"] = "Idioteque",
                    ["trackNumber"] = 8L,
                    ["file"] = "/music/Radiohead/Kid A/08 Idioteque.flac",
                    ["stars"] = 4.5,
                },
            ]));

        var stars = File(files, "stars/kelsey.jsonl");
        stars.Should().Contain("Idioteque").And.Contain("/music/Radiohead/Kid A/08 Idioteque.flac");
        // Stars, not Plex's 0-10 — that is the concept any other system would recognise.
        stars.Should().Contain("\"stars\": 4.5");
    }

    [Fact]
    public void Two_tracks_sharing_a_title_on_one_album_stay_distinct()
    {
        // They key by path as well as by the readable triple, so a duplicate title can't collapse one
        // rating into the other.
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            trackRatings:
            [
                new JsonObject
                {
                    ["userId"] = "sub-1", ["artist"] = "A", ["album"] = "B", ["title"] = "Reprise",
                    ["file"] = "/music/a/b/03 Reprise.flac", ["stars"] = 3.0,
                },
                new JsonObject
                {
                    ["userId"] = "sub-1", ["artist"] = "A", ["album"] = "B", ["title"] = "Reprise",
                    ["file"] = "/music/a/b/09 Reprise.flac", ["stars"] = 5.0,
                },
            ]));

        File(files, "stars/kelsey.jsonl").TrimEnd('\n').Split('\n').Should().HaveCount(2);
    }

    // ---- playlists ----

    [Fact]
    public void A_hand_built_playlist_keeps_its_ordered_tracks()
    {
        // This is the least reconstructable thing in the system: a smart playlist rebuilds itself from
        // its rules anywhere, but a curated running order is human work that exists nowhere else.
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            playlists:
            [
                new JsonObject
                {
                    ["userId"] = "sub-1",
                    ["title"] = "Driving",
                    ["smart"] = false,
                    ["tracks"] = new JsonArray(
                        new JsonObject
                        {
                            ["position"] = 1L, ["artist"] = "Radiohead", ["album"] = "Kid A",
                            ["title"] = "Idioteque", ["file"] = "/music/a.flac",
                        },
                        new JsonObject
                        {
                            ["position"] = 2L, ["artist"] = "Portishead", ["album"] = "Dummy",
                            ["title"] = "Roads", ["file"] = "/music/b.flac",
                        }),
                },
            ]));

        var playlists = File(files, "playlists/kelsey.jsonl");
        playlists.Should().Contain("Driving").And.Contain("Idioteque").And.Contain("Roads");
        // Order is part of what the playlist is, so the positions travel with it.
        playlists.Should().Contain("\"position\": 1").And.Contain("\"position\": 2");
    }

    [Fact]
    public void A_smart_playlist_keeps_its_rules_rather_than_a_snapshot_of_its_members()
    {
        // The rules are the durable thing; the membership is only their current answer, and archiving
        // that would preserve something that goes stale while losing something that doesn't.
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            playlists:
            [
                new JsonObject
                {
                    ["userId"] = "sub-1",
                    ["title"] = "4 stars and up",
                    ["smart"] = true,
                    ["rules"] = "track.userRating>>7",
                    ["tracks"] = new JsonArray(),
                },
            ]));

        var playlists = File(files, "playlists/kelsey.jsonl");
        playlists.Should().Contain("track.userRating>>7");
        playlists.Should().Contain("\"smart\": true");
    }

    // ---- format ----

    [Fact]
    public void Records_are_sorted_so_one_change_is_one_line()
    {
        var files = new ArchiveBuilder().Build(Input(artists:
        [
            new JsonObject { ["_id"] = "Zappa" },
            new JsonObject { ["_id"] = "ABBA" },
            new JsonObject { ["_id"] = "Metallica" },
        ]));

        var lines = File(files, "inventory.jsonl").TrimEnd('\n').Split('\n');
        lines.Should().HaveCount(3);
        lines[0].Should().Contain("ABBA");
        lines[1].Should().Contain("Metallica");
        lines[2].Should().Contain("Zappa");
    }

    [Fact]
    public void An_artist_name_containing_a_slash_needs_no_escaping()
    {
        // The reason the archive is JSON Lines rather than a file per artist: names are the primary
        // keys and contain "/", unicode, and case variants that collide on some filesystems.
        var files = new ArchiveBuilder().Build(Input(artists: [new JsonObject { ["_id"] = "AC/DC" }]));

        File(files, "inventory.jsonl").Should().Contain("AC/DC");
        files.Select(f => f.RelativePath).Should().NotContain(p => p.Contains("AC"));
    }

    [Fact]
    public void The_same_input_always_produces_the_same_bytes()
    {
        // The commit-only-on-change rule depends on this end to end, not just in the serializer.
        var artists = new JsonObject { ["_id"] = "Radiohead", ["albums"] = new JsonArray("Kid A") };

        var first = new ArchiveBuilder().Build(Input(artists: [artists.DeepClone().AsObject()]));
        var second = new ArchiveBuilder().Build(Input(artists: [artists.DeepClone().AsObject()]));

        first.Select(f => f.RelativePath + "\n" + f.Contents)
            .Should().Equal(second.Select(f => f.RelativePath + "\n" + f.Contents));
    }

    [Fact]
    public void The_manifest_carries_counts_but_no_timestamp()
    {
        // A timestamp in a tracked file would change every run, so every night would produce a commit
        // and the whole commit-only-on-change design would quietly stop working.
        var files = new ArchiveBuilder().Build(Input(artists:
        [
            new JsonObject { ["_id"] = "ABBA" },
            new JsonObject { ["_id"] = "Zappa" },
        ]));

        var manifest = File(files, "MANIFEST.json");
        manifest.Should().Contain("\"schemaVersion\": 1");
        manifest.Should().Contain("\"inventory.jsonl\": 2");
        manifest.Should().NotContainAny("generated", "Generated", "timestamp", "At\":");
    }
}
