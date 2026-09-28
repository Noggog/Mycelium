using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Download;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// Taking an owned album out of the library. The files have to leave (to the trash, not deleted) and
/// the likes have to go with them — a liked album that has left the library is one the reconcile
/// downloads again, which is exactly how deleting by hand fails.
/// </summary>
public class LibraryAlbumRemoverTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"mycelium-remove-tests-{Guid.NewGuid():N}");

    private readonly string _library;
    private readonly string _albumDir;
    private readonly ILibraryQuery _query = Substitute.For<ILibraryQuery>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly IUserAlbumRatingRepo _ratings = Substitute.For<IUserAlbumRatingRepo>();
    private readonly ILibraryScanner _scanner = Substitute.For<ILibraryScanner>();

    private const int AlbumKey = 4242;
    private const string PlexRoot = "/plex-media/music";

    public LibraryAlbumRemoverTests()
    {
        _library = Path.Combine(_root, "music");
        _albumDir = Path.Combine(_library, "Alvvays", "Blue Rev");
        Directory.CreateDirectory(_albumDir);
        _catalog.GetAlbumPlexRatingKeys(Arg.Any<IReadOnlyCollection<string>>()).Returns(
            new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alvvays"] = new(StringComparer.OrdinalIgnoreCase) { ["Blue Rev"] = AlbumKey },
            });
        _ratings.GetAllLikedByUser().Returns(Array.Empty<LikedAlbum>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private LibraryAlbumRemover Sut(string? pathMap = $"{PlexRoot}:__LIBRARY__") =>
        new(_query, _catalog,
            new LibraryPathMap(pathMap?.Replace("__LIBRARY__", _library)),
            new LibraryTrash(NullLogger<LibraryTrash>.Instance, new LibraryTrashConfig(Path.Combine(_root, "trash"))),
            _ratings, _scanner, NullLogger<LibraryAlbumRemover>.Instance);

    /// <summary>Puts the album's tracks on disk and tells the fake library where Plex thinks they are.</summary>
    private void OnDisk(params string[] tracks)
    {
        foreach (var name in tracks)
        {
            File.WriteAllText(Path.Combine(_albumDir, name), "audio");
        }
        _query.QueryAlbumFiles(AlbumKey).Returns(
            tracks.Select(n => $"{PlexRoot}/Alvvays/Blue Rev/{n}").ToArray());
    }

    private static LikedAlbum Like(string user, string artist, string album) =>
        new(user, new AlbumRating(new ArtistKey(artist), new AlbumKey(album), null, DiscoveryStatus.Liked));

    [Fact]
    public async Task The_whole_album_folder_goes_to_the_trash()
    {
        OnDisk("01.flac", "02.flac");
        File.WriteAllText(Path.Combine(_albumDir, "cover.jpg"), "art");

        var removal = await Sut().Remove("Alvvays", "Blue Rev", "dev");

        removal.Removed.Should().BeTrue();
        removal.FilesMoved.Should().Be(3);
        // Cover art included, and the emptied folders pruned — no husk left for Plex to show.
        Directory.Exists(Path.Combine(_library, "Alvvays")).Should().BeFalse();
        Directory.EnumerateFiles(removal.MovedTo!).Select(Path.GetFileName)
            .Should().Contain(new[] { "01.flac", "02.flac", "cover.jpg", "manifest.json" });
        await _scanner.Received(1).RequestScan();
    }

    [Fact]
    public async Task Every_users_like_on_the_album_is_withdrawn_and_no_others()
    {
        OnDisk("01.flac");
        _ratings.GetAllLikedByUser().Returns(new[]
        {
            Like("u1", "Alvvays", "Blue Rev"),
            // Deezer's decorated title is the same record as the copy Plex files.
            Like("u2", "Alvvays", "Blue Rev (Deluxe Edition)"),
            Like("u1", "Alvvays", "Antisocialites"),
            Like("u3", "Someone Else", "Blue Rev"),
        });

        var removal = await Sut().Remove("Alvvays", "Blue Rev", "dev");

        removal.LikesCleared.Should().Be(2);
        await _ratings.Received(1).Clear("u1", "Alvvays", "Blue Rev");
        await _ratings.Received(1).Clear("u2", "Alvvays", "Blue Rev (Deluxe Edition)");
        await _ratings.DidNotReceive().Clear(Arg.Any<string>(), Arg.Any<string>(), "Antisocialites");
        await _ratings.DidNotReceive().Clear(Arg.Any<string>(), "Someone Else", Arg.Any<string>());
    }

    [Fact]
    public async Task A_folder_shared_with_another_album_loses_only_this_albums_tracks()
    {
        OnDisk("01.flac");
        File.WriteAllText(Path.Combine(_albumDir, "other-album-01.flac"), "audio");

        var removal = await Sut().Remove("Alvvays", "Blue Rev", "dev");

        removal.FilesMoved.Should().Be(1);
        Directory.EnumerateFiles(_albumDir).Select(Path.GetFileName)
            .Should().BeEquivalentTo("other-album-01.flac");
    }

    [Fact]
    public async Task Without_a_path_map_nothing_is_touched()
    {
        OnDisk("01.flac");
        _ratings.GetAllLikedByUser().Returns(new[] { Like("u1", "Alvvays", "Blue Rev") });

        var removal = await Sut(pathMap: null).Remove("Alvvays", "Blue Rev", "dev");

        removal.Removed.Should().BeFalse();
        File.Exists(Path.Combine(_albumDir, "01.flac")).Should().BeTrue();
        // A refusal must leave the likes alone too: withdrawing them for an album still on disk would
        // quietly drop it from someone's list for no reason.
        await _ratings.DidNotReceive().Clear(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Files_outside_the_mapped_prefixes_refuse_the_whole_removal()
    {
        OnDisk("01.flac");
        _query.QueryAlbumFiles(AlbumKey).Returns(new[]
        {
            $"{PlexRoot}/Alvvays/Blue Rev/01.flac",
            "/somewhere/else/02.flac",
        });

        var removal = await Sut().Remove("Alvvays", "Blue Rev", "dev");

        removal.Removed.Should().BeFalse();
        File.Exists(Path.Combine(_albumDir, "01.flac")).Should().BeTrue();
    }

    [Fact]
    public async Task An_album_the_library_doesnt_list_is_refused()
    {
        var removal = await Sut().Remove("Alvvays", "Antisocialites", "dev");

        removal.Removed.Should().BeFalse();
        await _scanner.DidNotReceive().RequestScan(Arg.Any<bool>());
    }
}
