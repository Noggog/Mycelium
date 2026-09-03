using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Mycelium.Backend.Services.Archive;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The per-user takeout: the archive, cut to one person.
///
/// <para>Worth testing hard for a reason the nightly snapshot isn't — this one leaves the building.
/// A snapshot that keeps too much is a private repository with an extra field in it; a takeout that
/// keeps too much hands one user another user's ratings, playlists and download history. Every test
/// below that names a second person is really asking the same question: did anything of theirs come
/// along?</para>
/// </summary>
public class TakeoutTests
{
    private const string Mine = "oidc-subject-mine";
    private const string Theirs = "oidc-subject-theirs";

    // ---- fixtures ----

    private static JsonObject User(string subject, string username) =>
        new() { ["_id"] = subject, ["username"] = username };

    private static JsonObject Artist(string name, params string[] albums) =>
        new() { ["_id"] = name, ["albums"] = new JsonArray(albums.Select(a => (JsonNode)a!).ToArray()) };

    private static JsonObject Verdict(string subject, string artist, string status) =>
        new() { ["userId"] = subject, ["artist"] = artist, ["status"] = status };

    private static JsonObject Rating(string subject, string artist, string album, string title, double stars) =>
        new()
        {
            ["userId"] = subject, ["artist"] = artist, ["album"] = album,
            ["title"] = title, ["file"] = $"/music/{artist}/{album}/{title}.flac", ["stars"] = stars,
        };

    private static JsonObject Playlist(string subject, string title) =>
        new() { ["userId"] = subject, ["title"] = title, ["smart"] = false, ["tracks"] = new JsonArray() };

    /// <summary>A library two people have both been busy in.</summary>
    private static ArchiveInput Shared() => new(
        Users: [User(Mine, "noggog"), User(Theirs, "kelsey")],
        PlexLinks:
        [
            new JsonObject { ["_id"] = Mine, ["username"] = "noggog-plex", ["serverToken"] = "secret" },
            new JsonObject { ["_id"] = Theirs, ["username"] = "kelsey-plex" },
        ],
        Artists: [Artist("Radiohead", "Kid A"), Artist("Boards of Canada", "Geogaddi")],
        ArtistVerdicts:
        [
            Verdict(Mine, "Radiohead", "Liked"),
            Verdict(Theirs, "Radiohead", "Disliked"),
            Verdict(Theirs, "Boards of Canada", "Liked"),
        ],
        Purchases:
        [
            new JsonObject { ["artist"] = "Radiohead", ["album"] = "Kid A", ["addedBy"] = "noggog" },
            new JsonObject
            {
                ["artist"] = "Boards of Canada", ["album"] = "Geogaddi", ["addedBy"] = "kelsey",
            },
        ],
        Blocks:
        [
            new JsonObject { ["artist"] = "Nickelback", ["album"] = "Silver Side Up", ["blockedBy"] = Mine },
            new JsonObject { ["artist"] = "Creed", ["album"] = "Human Clay", ["blockedBy"] = Theirs },
        ],
        MatchOverrides:
        [
            new JsonObject { ["matchArtist"] = "Radiohead", ["deezerTitle"] = "Kid A", ["libraryTitle"] = "Kid A." },
        ],
        TrackRatings:
        [
            Rating(Mine, "Radiohead", "Kid A", "Idioteque", 5),
            Rating(Theirs, "Radiohead", "Kid A", "Idioteque", 2),
            Rating(Theirs, "Boards of Canada", "Geogaddi", "Dawn Chorus", 4),
        ],
        Playlists: [Playlist(Mine, "Late night"), Playlist(Theirs, "Driving")],
        LibraryTracks:
        [
            new JsonObject
            {
                ["artist"] = "Radiohead", ["album"] = "Kid A", ["title"] = "Idioteque",
                ["file"] = "/music/Radiohead/Kid A/Idioteque.flac", ["trackNumber"] = 8,
            },
        ]);

    private static IReadOnlyList<ArchiveFile> Files(string subject) =>
        new ArchiveBuilder().Build(ArchiveScope.ForUser(Shared(), subject));

    private static string File(IReadOnlyList<ArchiveFile> files, string path) =>
        files.Single(f => f.RelativePath == path).Contents;

    private static string Everything(IReadOnlyList<ArchiveFile> files) =>
        string.Join("\n", files.Select(f => f.Contents));

    // ---- what is kept ----

    [Fact]
    public void The_whole_library_is_kept_even_where_the_user_has_no_opinion()
    {
        // The point of the export is your taste *against the library it refers to*. An artist list cut
        // to what you rated would drop the records you own and never got round to, which is most of
        // them, and leave the ratings floating with nothing to attach to.
        var paths = Files(Mine).Select(f => f.RelativePath).ToList();

        paths.Should().Contain("Library/Radiohead/Kid A.yaml");
        paths.Should().Contain("Library/Boards of Canada/Geogaddi.yaml");
        paths.Should().Contain("Library/Boards of Canada/metadata.yaml");
    }

    [Fact]
    public void Their_own_verdicts_stars_playlists_and_acquisitions_all_survive()
    {
        var files = Files(Mine);

        File(files, "Library/Radiohead/metadata.yaml").Should().Contain("Liked");
        File(files, "Library/Radiohead/Kid A.yaml").Should().Contain("acquiredBy: \"noggog\"");
        File(files, "Library/Radiohead/Kid A.yaml").Should().Contain("noggog: 5");
        File(files, "playlists/noggog.yaml").Should().Contain("Late night");
        File(files, "decisions.yaml").Should().Contain("Nickelback");
        File(files, "users.yaml").Should().Contain("noggog");
    }

    [Fact]
    public void Library_wide_match_corrections_are_kept()
    {
        // Nobody's opinion — a correction to how a release is identified, which is a fact about the
        // library and is needed to read the rest of the export.
        File(Files(Mine), "decisions.yaml").Should().Contain("Kid A.");
    }

    // ---- what is left behind ----

    [Fact]
    public void No_trace_of_the_other_person_appears_anywhere_in_the_export()
    {
        // The single assertion this whole feature stands on. Swept across every file rather than
        // checked field by field, so a new field that starts carrying a username is caught by a test
        // written before it existed.
        var everything = Everything(Files(Mine));

        everything.Should().NotContain("kelsey");
        everything.Should().NotContain(Theirs);
    }

    [Fact]
    public void Another_persons_verdict_on_a_shared_artist_is_dropped_while_the_artist_remains()
    {
        var metadata = File(Files(Mine), "Library/Boards of Canada/metadata.yaml");

        // They liked it, I never saw it. The artist file is here; the verdict on it is not.
        metadata.Should().Contain("Boards of Canada");
        metadata.Should().NotContain("ratings");
    }

    [Fact]
    public void Another_persons_stars_are_dropped_from_a_song_we_both_rated()
    {
        var album = File(Files(Mine), "Library/Radiohead/Kid A.yaml");

        album.Should().Contain("Idioteque");
        album.Should().Contain("noggog: 5");
        album.Should().NotContain(": 2");
    }

    [Fact]
    public void Only_the_callers_playlist_file_is_written()
    {
        Files(Mine).Select(f => f.RelativePath)
            .Where(p => p.StartsWith("playlists/"))
            .Should().Equal("playlists/noggog.yaml");
    }

    [Fact]
    public void An_acquisition_credited_to_nobody_belongs_to_no_takeout()
    {
        // Most downloads arrive automatically off a like and credit nobody. Handing them to whoever
        // asks would put "you acquired this" on records nobody chose.
        var input = Shared() with
        {
            Purchases = [new JsonObject { ["artist"] = "Radiohead", ["album"] = "Kid A" }],
        };

        var files = new ArchiveBuilder().Build(ArchiveScope.ForUser(input, Mine));
        File(files, "Library/Radiohead/Kid A.yaml").Should().NotContain("acquiredBy");
    }

    [Fact]
    public void A_plex_link_never_carries_its_token()
    {
        // Same rule as the archive's, and it matters more here: the archive lives in a private
        // repository, whereas this file is handed to a browser and lands in a downloads folder.
        Everything(Files(Mine)).Should().NotContain("secret");
    }

    [Fact]
    public void A_block_recorded_under_a_username_rather_than_a_subject_is_still_theirs()
    {
        // Rows written before the block endpoint stored subjects hold a username instead. Matching only
        // one spelling would quietly withhold a person's own history from them.
        var input = Shared() with
        {
            Blocks = [new JsonObject { ["artist"] = "Nickelback", ["blockedBy"] = "NOGGOG" }],
        };

        var files = new ArchiveBuilder().Build(ArchiveScope.ForUser(input, Mine));
        File(files, "decisions.yaml").Should().Contain("Nickelback");
    }

    [Fact]
    public void An_account_with_no_ratings_at_all_still_gets_the_library()
    {
        // A new user pressing the button gets an honest empty answer, not an error.
        var files = new ArchiveBuilder().Build(ArchiveScope.ForUser(Shared(), "oidc-subject-nobody"));

        files.Select(f => f.RelativePath).Should().Contain("Library/Radiohead/Kid A.yaml");
        files.Select(f => f.RelativePath).Should().NotContain(p => p.StartsWith("playlists/"));
        Everything(files).Should().NotContain("kelsey");
    }

    // ---- the summary and the zip ----

    [Fact]
    public async Task The_summary_counts_the_rows_the_export_actually_writes()
    {
        var dump = Dump(Shared());
        var summary = await new TakeoutBuilder(dump, new ArchiveBuilder()).Summary(Mine);

        summary.Artists.Should().Be(2);
        summary.Albums.Should().Be(2);
        summary.Liked.Should().Be(1);
        summary.Disliked.Should().Be(0);
        summary.SongRatings.Should().Be(1);
        summary.Playlists.Should().Be(1);
        summary.Acquisitions.Should().Be(1);
        summary.Blocks.Should().Be(1);
        summary.FileName.Should().StartWith("mycelium-takeout-noggog-").And.EndWith(".zip");
    }

    [Fact]
    public async Task The_zip_holds_the_readme_and_every_built_file()
    {
        var built = await new TakeoutBuilder(Dump(Shared()), new ArchiveBuilder()).Build(Mine);

        using var buffer = new MemoryStream();
        TakeoutBuilder.WriteZip(buffer, built.Files);

        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var entries = zip.Entries.Select(e => e.FullName).ToList();

        entries.Should().Contain("README.md");
        entries.Should().Contain("Library/Radiohead/Kid A.yaml");
        entries.Should().HaveCount(built.Files.Count + 1);

        // The README is the takeout's, not the git archive's — the reader is the person the data is
        // about, and the opening line is what tells them so.
        using var reader = new StreamReader(zip.GetEntry("README.md")!.Open(), Encoding.UTF8);
        (await reader.ReadToEndAsync()).Should().StartWith("# Your Mycelium data");
    }

    [Fact]
    public void The_download_is_named_for_whoever_asked_and_when()
    {
        TakeoutBuilder.FileNameFor("noggog", new DateOnly(2026, 9, 2))
            .Should().Be("mycelium-takeout-noggog-2026-09-02.zip");

        // Names go into a Content-Disposition header, so anything outside [a-z0-9_-] is dropped rather
        // than escaped — and a name left with nothing at all falls back to the bare form.
        TakeoutBuilder.FileNameFor("Jo/e Bloggs\"", new DateOnly(2026, 9, 2))
            .Should().Be("mycelium-takeout-joebloggs-2026-09-02.zip");
        TakeoutBuilder.FileNameFor("你好", new DateOnly(2026, 9, 2))
            .Should().Be("mycelium-takeout-2026-09-02.zip");
        TakeoutBuilder.FileNameFor(null, new DateOnly(2026, 9, 2))
            .Should().Be("mycelium-takeout-2026-09-02.zip");
    }

    private static FakeArchiveDump Dump(ArchiveInput input) => new FakeArchiveDump()
        .Set(ArchiveCollections.Users, input.Users.ToArray())
        .Set(ArchiveCollections.PlexLinks, input.PlexLinks.ToArray())
        .Set(ArchiveCollections.Artists, input.Artists.ToArray())
        .Set(ArchiveCollections.ArtistVerdicts, input.ArtistVerdicts.ToArray())
        .Set(ArchiveCollections.Purchases, input.Purchases.ToArray())
        .Set(ArchiveCollections.Blocks, input.Blocks.ToArray())
        .Set(ArchiveCollections.MatchOverrides, input.MatchOverrides.ToArray())
        .Set(ArchiveCollections.TrackRatings, input.TrackRatings.ToArray())
        .Set(ArchiveCollections.Playlists, input.Playlists.ToArray())
        .Set(ArchiveCollections.LibraryTracks, input.LibraryTracks.ToArray());
}
