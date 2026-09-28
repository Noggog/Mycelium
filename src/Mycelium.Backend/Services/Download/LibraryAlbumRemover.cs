using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Download;

/// <summary>What removing an album did, or why it did nothing.</summary>
/// <param name="Refusal">Why nothing was touched; null when the album was removed.</param>
/// <param name="FilesMoved">How many files left the library.</param>
/// <param name="MovedTo">The trash folder they went to — where to look to undo it.</param>
/// <param name="LikesCleared">How many users' likes on the album were withdrawn.</param>
public record AlbumRemoval(string? Refusal, int FilesMoved = 0, string? MovedTo = null, int LikesCleared = 0)
{
    public bool Removed => Refusal is null;

    public static AlbumRemoval Refused(string why) => new(why);
}

/// <summary>
/// Takes an owned album out of the library for good: its files go to the <see cref="LibraryTrash"/>
/// (never deleted — the manifest makes it reversible), and every user's like on it is withdrawn.
///
/// <para>The likes are the part that makes it stick. Deleting an album by hand leaves its likes
/// behind, and <see cref="PurchaseService.Reconcile"/> reads a liked album that has left the library
/// as one to fetch again — so the downloader dutifully puts back whatever was just removed. The
/// caller also blocks the album, so it isn't offered to anyone afterwards either.</para>
///
/// <para>All-or-nothing on the files, for the same reason <see cref="UpgradeSwap"/> is: an album
/// half in the library and half in the trash is worse than one left alone, so a partial move is put
/// back and refused, and nothing else changes.</para>
/// </summary>
public class LibraryAlbumRemover
{
    private readonly ILibraryQuery _library;
    private readonly IArtistCatalogRepo _catalog;
    private readonly LibraryPathMap _paths;
    private readonly LibraryTrash _trash;
    private readonly IUserAlbumRatingRepo _albumRatings;
    private readonly ILibraryScanner _scanner;
    private readonly ILogger<LibraryAlbumRemover> _logger;

    public LibraryAlbumRemover(
        ILibraryQuery library,
        IArtistCatalogRepo catalog,
        LibraryPathMap paths,
        LibraryTrash trash,
        IUserAlbumRatingRepo albumRatings,
        ILibraryScanner scanner,
        ILogger<LibraryAlbumRemover> logger)
    {
        _library = library;
        _catalog = catalog;
        _paths = paths;
        _trash = trash;
        _albumRatings = albumRatings;
        _scanner = scanner;
        _logger = logger;
    }

    public async Task<AlbumRemoval> Remove(string artist, string album, string? removedBy)
    {
        if (!_paths.IsConfigured)
        {
            return AlbumRemoval.Refused("PLEX_PATH_MAP is not set, so the album's files can't be located");
        }

        if (await UpgradeMatchKeeper.AlbumRatingKey(_catalog, new[] { artist }, album) is not { } key)
        {
            return AlbumRemoval.Refused("the library has no record of this album");
        }

        var files = await _library.QueryAlbumFiles(key);
        if (files.Length == 0)
        {
            return AlbumRemoval.Refused("the library reports no files for this album");
        }

        var local = files.Select(f => (Plex: f, Local: _paths.ToLocal(f))).ToList();
        var unmapped = local.Where(f => f.Local is null).Select(f => f.Plex).ToList();
        if (unmapped.Count > 0)
        {
            return AlbumRemoval.Refused(
                $"{unmapped.Count} file(s) lie outside the mapped prefixes "
                + $"({string.Join(", ", _paths.PlexPrefixes)}); first is {unmapped[0]}");
        }

        var present = local.Select(f => f.Local!).Where(File.Exists).ToList();
        if (present.Count == 0)
        {
            return AlbumRemoval.Refused(
                "the mapped paths don't exist from here — check PLEX_PATH_MAP against the mounts");
        }

        // The whole folder when the album owns it, so cover art and the like don't linger as a husk.
        var albumDir = AlbumFolders.FolderOf(present, _paths);
        var moving = albumDir is not null ? AlbumFolders.Owning(albumDir, present, _logger) : present;
        var result = _trash.MoveAside(
            moving,
            $"{artist} - {album}",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString("x"));
        if (result.Moved < moving.Count)
        {
            var restored = _trash.Restore(result.Destination);
            _logger.LogError(
                "Removing {Artist} — {Album}: only {Moved} of {Total} file(s) could be moved aside, so "
                + "nothing was removed; {Restored} went back to the library",
                artist, album, result.Moved, moving.Count, restored);
            return AlbumRemoval.Refused(
                $"moved only {result.Moved} of {moving.Count} file(s) aside"
                + (restored == result.Moved
                    ? ""
                    : $"; {result.Moved - restored} could not be put back and are in {result.Destination}"));
        }

        if (albumDir is not null)
        {
            AlbumFolders.PruneEmpty(albumDir, _paths, _logger);
        }

        var cleared = await ClearLikes(artist, album);
        await _scanner.RequestScan();

        _logger.LogInformation(
            "{User} removed {Artist} — {Album} from the library: {Moved} file(s) moved to {Destination}, "
            + "{Cleared} like(s) withdrawn",
            removedBy ?? "(unattributed)", artist, album, result.Moved, result.Destination, cleared);
        return new AlbumRemoval(null, result.Moved, result.Destination, cleared);
    }

    /// <summary>
    /// Withdraws every user's like on the album, matched at record granularity — a like placed on
    /// Deezer's "Blue Rev (Deluxe)" is a like on the copy Plex files as "Blue Rev".
    /// </summary>
    private async Task<int> ClearLikes(string artist, string album)
    {
        var record = AlbumTitleMatcher.NormalizeRecord(album);
        var likes = (await _albumRatings.GetAllLikedByUser())
            .Where(l => ArtistNameComparer.Instance.Equals(l.Rating.Artist.ArtistName, artist)
                        && AlbumTitleMatcher.NormalizeRecord(l.Rating.Album.AlbumName) == record)
            .ToList();
        foreach (var like in likes)
        {
            await _albumRatings.Clear(like.UserId, like.Rating.Artist.ArtistName, like.Rating.Album.AlbumName);
        }
        return likes.Count;
    }
}
