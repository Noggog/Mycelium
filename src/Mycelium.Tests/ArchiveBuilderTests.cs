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
        IEnumerable<JsonObject>? purchases = null,
        IEnumerable<JsonObject>? blocks = null,
        IEnumerable<JsonObject>? matchOverrides = null,
        IEnumerable<JsonObject>? trackRatings = null,
        IEnumerable<JsonObject>? playlists = null,
        IEnumerable<JsonObject>? libraryTracks = null) =>
        new(
            (users ?? []).ToList(),
            (plexLinks ?? []).ToList(),
            (artists ?? []).ToList(),
            (artistVerdicts ?? []).ToList(),
            (purchases ?? []).ToList(),
            (blocks ?? []).ToList(),
            (matchOverrides ?? []).ToList(),
            (trackRatings ?? []).ToList(),
            (playlists ?? []).ToList(),
            (libraryTracks ?? []).ToList());

    private static JsonObject User(string subject, string username) =>
        new() { ["_id"] = subject, ["username"] = username };

    private static JsonObject Artist(string name, params string[] albums) =>
        new() { ["_id"] = name, ["albums"] = new JsonArray(albums.Select(a => (JsonNode)a!).ToArray()) };

    private static string File(IReadOnlyList<ArchiveFile> files, string path) =>
        files.Single(f => f.RelativePath == path).Contents;

    private static IEnumerable<string> Paths(IReadOnlyList<ArchiveFile> files) =>
        files.Select(f => f.RelativePath);

    // ---- layout ----

    [Fact]
    public void The_library_is_laid_out_as_a_directory_per_artist_and_a_file_per_album()
    {
        var files = new ArchiveBuilder().Build(Input(artists: [Artist("Radiohead", "Kid A", "Amnesiac")]));

        Paths(files).Should().Contain("Library/Radiohead/metadata.yaml");
        Paths(files).Should().Contain("Library/Radiohead/Kid A.yaml");
        Paths(files).Should().Contain("Library/Radiohead/Amnesiac.yaml");
    }

    [Fact]
    public void An_artist_with_no_albums_still_gets_a_metadata_file()
    {
        // Their identity pins and anyone's verdict on them are worth keeping even with nothing owned.
        var files = new ArchiveBuilder().Build(Input(artists: [Artist("Obscure Act")]));

        Paths(files).Should().Contain("Library/Obscure Act/metadata.yaml");
    }

    [Fact]
    public void A_slash_in_a_title_becomes_a_filename_not_a_directory()
    {
        // 579 albums in the real library contain one. Without escaping this would silently create
        // "Library/.../Gorgeous /Fantasy.yaml" — a nested directory, not an album.
        var files = new ArchiveBuilder().Build(Input(artists: [Artist("AC/DC", "Gorgeous / Fantasy")]));

        Paths(files).Should().Contain("Library/AC%2FDC/Gorgeous %2F Fantasy.yaml");

        // ...and the real names survive inside the file, which is what a reader should trust.
        var album = File(files, "Library/AC%2FDC/Gorgeous %2F Fantasy.yaml");
        album.Should().Contain("artist: \"AC/DC\"");
        album.Should().Contain("album: \"Gorgeous / Fantasy\"");
    }

    // ---- album contents ----

    [Fact]
    public void An_album_gathers_everything_true_of_it_into_one_file()
    {
        // The point of the layout: one album's story in one place, so a diff is legible.
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            artists:
            [
                new JsonObject
                {
                    ["_id"] = "Radiohead",
                    ["albums"] = new JsonArray("Kid A"),
                    ["albumQuality"] = new JsonArray(
                        new JsonObject { ["title"] = "Kid A", ["quality"] = "Lossless" }),
                },
            ],
            purchases:
            [
                new JsonObject
                {
                    ["artist"] = "Radiohead", ["album"] = "Kid A", ["addedBy"] = "kelsey",
                    ["sentAt"] = "2026-08-25T08:31:00Z", ["acquiredQuality"] = "Lossless",
                },
            ],
            libraryTracks:
            [
                new JsonObject
                {
                    ["artist"] = "Radiohead", ["album"] = "Kid A", ["title"] = "Idioteque",
                    ["trackNumber"] = 8L, ["file"] = "/music/kida/08.flac",
                },
            ],
            trackRatings:
            [
                new JsonObject { ["userId"] = "sub-1", ["file"] = "/music/kida/08.flac", ["stars"] = 4.5 },
            ]));

        var album = File(files, "Library/Radiohead/Kid A.yaml");
        album.Should().Contain("quality: \"Lossless\"");
        album.Should().Contain("acquiredBy: \"kelsey\"");
        album.Should().Contain("title: \"Idioteque\"");  // track listing
        album.Should().Contain("kelsey: 4.5");                // that person's star rating
    }

    [Fact]
    public void An_album_carries_no_verdicts_of_its_own()
    {
        // A thumbs-up on an album in Mycelium means "fetch this", not "this is good" — for an album the
        // library already holds, `acquiredBy` is what that decision actually produced. Song stars are
        // the real per-person judgement.
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            artists: [Artist("Radiohead", "Kid A")]));

        File(files, "Library/Radiohead/Kid A.yaml").Should().NotContain("verdict");
    }

    [Fact]
    public void An_acquisition_nobody_asked_for_by_hand_names_nobody()
    {
        // Most downloads happen automatically off a like, and have no one to credit; an empty field on
        // those would be noise on almost every album.
        var files = new ArchiveBuilder().Build(Input(
            artists: [Artist("Radiohead", "Kid A")],
            purchases:
            [
                new JsonObject { ["artist"] = "Radiohead", ["album"] = "Kid A", ["sentAt"] = "2026-08-25T08:31:00Z" },
            ]));

        File(files, "Library/Radiohead/Kid A.yaml").Should().NotContain("acquiredBy");
    }

    [Fact]
    public void A_song_carries_only_its_title_and_its_ratings()
    {
        // The track number is implicit in the running order, and the file path is this server's own
        // namespace — it wouldn't resolve on whatever system reads this archive.
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            artists: [Artist("A", "B")],
            libraryTracks:
            [
                new JsonObject
                {
                    ["artist"] = "A", ["album"] = "B", ["title"] = "Only Title",
                    ["trackNumber"] = 4L, ["file"] = "/media/music/a/b/04.flac",
                },
            ],
            trackRatings:
            [
                new JsonObject { ["userId"] = "sub-1", ["file"] = "/media/music/a/b/04.flac", ["stars"] = 3.5 },
            ]));

        var album = File(files, "Library/A/B.yaml");
        album.Should().Contain("title: \"Only Title\"").And.Contain("kelsey: 3.5");
        album.Should().NotContain("trackNumber").And.NotContain("/media/music");
    }

    [Fact]
    public void A_rating_survives_even_when_the_track_listing_does_not_mention_it()
    {
        // The listing and the ratings come from separate Plex reads. If the listing fails — or simply
        // disagrees about a name — keying songs off it alone would silently drop the ratings, which are
        // the least reconstructable thing in here. Found the hard way: a real snapshot came back with
        // every album songless because the listing sweep hadn't run.
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "noggog")],
            artists: [Artist("American Head Charge", "The War of Art")],
            libraryTracks: [],
            trackRatings:
            [
                new JsonObject
                {
                    ["userId"] = "sub-1", ["artist"] = "American Head Charge",
                    ["album"] = "The War of Art", ["title"] = "Just So You Know",
                    ["trackNumber"] = 3L, ["file"] = "/m/ahc/03.flac", ["stars"] = 4.5,
                },
            ]));

        var album = File(files, "Library/American Head Charge/The War of Art.yaml");
        album.Should().Contain("title: \"Just So You Know\"").And.Contain("noggog: 4.5");
    }

    [Fact]
    public void A_track_in_both_the_listing_and_the_ratings_is_listed_once()
    {
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "noggog")],
            artists: [Artist("A", "B")],
            libraryTracks:
            [
                new JsonObject
                {
                    ["artist"] = "A", ["album"] = "B", ["title"] = "Song", ["file"] = "/m/1.flac",
                },
            ],
            trackRatings:
            [
                new JsonObject
                {
                    ["userId"] = "sub-1", ["artist"] = "A", ["album"] = "B", ["title"] = "Song",
                    ["file"] = "/m/1.flac", ["stars"] = 4.0,
                },
            ]));

        var album = File(files, "Library/A/B.yaml");
        album.Split('\n').Count(l => l.Contains("title: \"Song\"")).Should().Be(1);
        album.Should().Contain("noggog: 4");
    }

    [Fact]
    public void Songs_are_listed_in_running_order()
    {
        var files = new ArchiveBuilder().Build(Input(
            artists: [Artist("A", "B")],
            libraryTracks:
            [
                new JsonObject { ["artist"] = "A", ["album"] = "B", ["title"] = "Third", ["trackNumber"] = 3L },
                new JsonObject { ["artist"] = "A", ["album"] = "B", ["title"] = "First", ["trackNumber"] = 1L },
                new JsonObject { ["artist"] = "A", ["album"] = "B", ["title"] = "Second", ["trackNumber"] = 2L },
            ]));

        var album = File(files, "Library/A/B.yaml");
        album.IndexOf("First", StringComparison.Ordinal)
            .Should().BeLessThan(album.IndexOf("Second", StringComparison.Ordinal));
        album.IndexOf("Second", StringComparison.Ordinal)
            .Should().BeLessThan(album.IndexOf("Third", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unrated_song_carries_no_ratings_stanza()
    {
        // Most songs are unrated; an empty map on each would be thousands of lines saying nothing.
        var files = new ArchiveBuilder().Build(Input(
            artists: [Artist("A", "B")],
            libraryTracks:
            [
                new JsonObject
                {
                    ["artist"] = "A", ["album"] = "B", ["title"] = "Unloved", ["file"] = "/music/x.flac",
                },
            ]));

        File(files, "Library/A/B.yaml").Should().NotContain("ratings");
    }

    [Fact]
    public void Two_people_rating_the_same_song_both_appear_under_it()
    {
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey"), User("sub-2", "noggog")],
            artists: [Artist("A", "B")],
            libraryTracks:
            [
                new JsonObject
                {
                    ["artist"] = "A", ["album"] = "B", ["title"] = "Shared", ["file"] = "/music/x.flac",
                },
            ],
            trackRatings:
            [
                new JsonObject { ["userId"] = "sub-1", ["file"] = "/music/x.flac", ["stars"] = 4.5 },
                new JsonObject { ["userId"] = "sub-2", ["file"] = "/music/x.flac", ["stars"] = 2.0 },
            ]));

        var album = File(files, "Library/A/B.yaml");
        album.Should().Contain("kelsey: 4.5").And.Contain("noggog: 2");
    }

    // ---- artist contents ----

    [Fact]
    public void The_artist_file_keeps_identities_and_verdicts_and_drops_churn()
    {
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            artists:
            [
                new JsonObject
                {
                    ["_id"] = "Radiohead",
                    ["albums"] = new JsonArray(),
                    ["genres"] = new JsonArray("Alternative"),
                    ["musicBrainzMbid"] = "a74b1b7f-71a5-4011-9441-d0b5e4122711",
                    ["deezerId"] = 399L,
                    ["deezerOverride"] = true,
                    // All of this is re-derivable and moves constantly.
                    ["lastSeenAt"] = "2026-08-31T06:00:00Z",
                    ["present"] = true,
                    ["plexRatingKeys"] = new JsonArray(1841L),
                    ["deezerFans"] = 5_100_000L,
                    ["imageUrl"] = "https://cdn.example/x.jpg",
                },
            ],
            artistVerdicts:
            [
                new JsonObject { ["userId"] = "sub-1", ["artist"] = "Radiohead", ["status"] = "Liked" },
            ]));

        var artist = File(files, "Library/Radiohead/metadata.yaml");
        artist.Should().Contain("a74b1b7f-71a5-4011-9441-d0b5e4122711");  // the forever-stable id
        artist.Should().Contain("pinned: true");                          // a hand-made correction
        artist.Should().Contain("kelsey:").And.Contain("verdict: \"Liked\"");
        artist.Should().NotContain("lastSeenAt").And.NotContain("plexRatingKeys");
        artist.Should().NotContain("deezerFans").And.NotContain("imageUrl").And.NotContain("present");
        // Genres are mirrored from the media server and rewritten on every sync — re-derivable, so out.
        artist.Should().NotContain("genres").And.NotContain("Alternative");
    }

    [Fact]
    public void An_unpinned_identity_says_nothing_about_pinning()
    {
        // A "pinned": false on 3,000 artists is noise.
        var files = new ArchiveBuilder().Build(Input(artists:
        [
            new JsonObject
            {
                ["_id"] = "Radiohead", ["albums"] = new JsonArray(),
                ["deezerId"] = 399L, ["deezerOverride"] = false,
            },
        ]));

        File(files, "Library/Radiohead/metadata.yaml").Should().NotContain("pinned");
    }

    [Fact]
    public void A_confirmed_verdict_keeps_its_stickiness()
    {
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            artists: [Artist("Nickelback")],
            artistVerdicts:
            [
                new JsonObject
                {
                    ["userId"] = "sub-1", ["artist"] = "Nickelback", ["status"] = "Disliked",
                    ["dislikeConfirmed"] = true,
                },
            ]));

        File(files, "Library/Nickelback/metadata.yaml").Should().Contain("confirmed: true");
    }

    [Fact]
    public void Pending_queue_rows_are_not_taste_and_are_dropped()
    {
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            artists: [Artist("Suggested")],
            artistVerdicts:
            [
                new JsonObject { ["userId"] = "sub-1", ["artist"] = "Suggested", ["status"] = "Pending" },
            ]));

        File(files, "Library/Suggested/metadata.yaml").Should().NotContain("ratings");
    }

    // ---- users ----

    [Fact]
    public void A_plex_link_is_archived_without_its_token()
    {
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            plexLinks:
            [
                new JsonObject
                {
                    ["_id"] = "sub-1", ["accountId"] = "plex-99", ["username"] = "kelsey_plex",
                    ["serverToken"] = "SECRET-TOKEN-VALUE",
                },
            ]));

        var users = File(files, "users.yaml");
        users.Should().Contain("plex-99").And.Contain("kelsey_plex");
        users.Should().NotContain("SECRET-TOKEN-VALUE").And.NotContain("serverToken");
    }

    [Fact]
    public void The_identity_providers_subject_is_not_stored()
    {
        // It means nothing outside the provider that issued it, and a rebuild reissues it anyway.
        var user = User("8f3c-opaque-subject", "kelsey");
        user["email"] = "kelsey@example.com";
        user["lastLoginAt"] = "2026-08-31T09:00:00Z";

        var users = File(new ArchiveBuilder().Build(Input(users: [user])), "users.yaml");

        users.Should().Contain("kelsey");
        users.Should().NotContain("8f3c-opaque-subject");
        users.Should().NotContain("example.com");
        users.Should().NotContain("lastLoginAt");
    }

    // ---- decisions ----

    [Fact]
    public void A_block_records_who_placed_it_by_name_not_by_subject()
    {
        // The block endpoint stores the OIDC subject, unlike the purchase rows which store a username.
        // Caught on real data: without the crosswalk this lands as an opaque hash.
        var files = new ArchiveBuilder().Build(Input(
            users: [User("bf777d0ab621d890", "kelseydoolittle056")],
            blocks:
            [
                new JsonObject
                {
                    ["artist"] = "Alabama Shakes", ["album"] = "Sound & Color",
                    ["blockedBy"] = "bf777d0ab621d890",
                },
            ]));

        var decisions = File(files, "decisions.yaml");
        decisions.Should().Contain("blockedBy: \"kelseydoolittle056\"");
        decisions.Should().NotContain("bf777d0ab621d890");
    }

    [Fact]
    public void A_blocker_who_is_not_a_known_subject_is_left_alone()
    {
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            blocks: [new JsonObject { ["artist"] = "A", ["album"] = "B", ["blockedBy"] = "justin" }]));

        File(files, "decisions.yaml").Should().Contain("blockedBy: \"justin\"");
    }

    // ---- playlists ----

    [Fact]
    public void A_hand_built_playlist_keeps_its_ordered_tracks()
    {
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            playlists:
            [
                new JsonObject
                {
                    ["userId"] = "sub-1", ["title"] = "Driving", ["smart"] = false,
                    ["tracks"] = new JsonArray(
                        new JsonObject
                        {
                            ["position"] = 1L, ["artist"] = "Radiohead", ["album"] = "Kid A",
                            ["title"] = "Idioteque", ["file"] = "/media/music/a.flac",
                        },
                        new JsonObject
                        {
                            ["position"] = 2L, ["artist"] = "Portishead", ["album"] = "Dummy",
                            ["title"] = "Roads", ["file"] = "/media/music/b.flac",
                        }),
                },
            ]));

        var playlists = File(files, "playlists/kelsey.yaml");
        playlists.Should().Contain("Idioteque").And.Contain("Roads");
        // Order is the running order, so the stored position adds nothing; the file path is the source
        // server's namespace and wouldn't resolve anywhere else.
        playlists.Should().NotContain("position").And.NotContain("/media/music");
        playlists.IndexOf("Idioteque", StringComparison.Ordinal)
            .Should().BeLessThan(playlists.IndexOf("Roads", StringComparison.Ordinal));
    }

    [Fact]
    public void A_playlist_track_is_identified_the_same_way_a_song_is()
    {
        // artist + album + title, so an entry can be found again on a system that never knew this one.
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            playlists:
            [
                new JsonObject
                {
                    ["userId"] = "sub-1", ["title"] = "Driving", ["smart"] = false,
                    ["tracks"] = new JsonArray(new JsonObject
                    {
                        ["artist"] = "Radiohead", ["album"] = "Kid A", ["title"] = "Idioteque",
                    }),
                },
            ]));

        File(files, "playlists/kelsey.yaml").Should().Contain(
            """
                - album: "Kid A"
                  artist: "Radiohead"
                  title: "Idioteque"
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_smart_playlist_keeps_its_rules_rather_than_a_snapshot_of_its_members()
    {
        var files = new ArchiveBuilder().Build(Input(
            users: [User("sub-1", "kelsey")],
            playlists:
            [
                new JsonObject
                {
                    ["userId"] = "sub-1", ["title"] = "4 stars up", ["smart"] = true,
                    ["rules"] = "track.userRating>>7",
                    ["tracks"] = new JsonArray(new JsonObject { ["title"] = "stale" }),
                },
            ]));

        var playlists = File(files, "playlists/kelsey.yaml");
        playlists.Should().Contain("track.userRating>>7");
        playlists.Should().NotContain("stale");
    }

    // ---- determinism ----

    [Fact]
    public void The_same_input_always_produces_the_same_bytes()
    {
        // The commit-only-on-change rule depends on this end to end.
        JsonObject Radiohead() => new()
        {
            ["_id"] = "Radiohead",
            ["albums"] = new JsonArray("Kid A"),
            ["genres"] = new JsonArray("Alternative", "Rock"),
        };

        var first = new ArchiveBuilder().Build(Input(artists: [Radiohead()]));
        var second = new ArchiveBuilder().Build(Input(artists: [Radiohead()]));

        first.Select(f => f.RelativePath + "\n" + f.Contents)
            .Should().Equal(second.Select(f => f.RelativePath + "\n" + f.Contents));
    }
}
