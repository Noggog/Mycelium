using Mycelium.Deezer.Models;

namespace Mycelium.Deezer.Services;

/// <summary>
/// Thin client over the keyless Deezer public API. Every call degrades gracefully (returns
/// null / empty) on a miss or transport error rather than throwing, so ingestion can keep
/// going when Deezer is flaky.
/// </summary>
public interface IDeezerApi
{
    /// <summary>Resolve an artist name to its Deezer artist (strongest match), or null if none.</summary>
    Task<DeezerArtist?> SearchArtist(string artistName);

    /// <summary>
    /// Search Deezer for artists by name, in relevance order (empty if none/error). Used to offer
    /// the user a choice when the top hit is wrong (the "Correct association" picker).
    /// </summary>
    Task<DeezerArtist[]> SearchArtists(string query, int limit);

    /// <summary>Fetch a Deezer artist by its id (name, fans, image, link), or null if none/error.</summary>
    Task<DeezerArtist?> GetArtist(long artistId);

    /// <summary>Deezer's "related artists" for the given artist id (empty if none/error).</summary>
    Task<DeezerArtist[]> GetRelated(long artistId);

    /// <summary>
    /// The artist's most popular tracks (for their ~30s preview URLs), most popular first.
    /// Empty if none/error.
    /// </summary>
    Task<DeezerTrack[]> GetTopTracks(long artistId, int limit);

    /// <summary>The artist's albums (their discography). Empty if none/error.</summary>
    Task<DeezerAlbum[]> GetAlbums(long artistId);

    /// <summary>
    /// A single album by its id, including its album-artist (the discography listing omits that).
    /// Null if none/error. Used to learn the real credited act for a collaboration album.
    /// </summary>
    Task<DeezerAlbum?> GetAlbum(long albumId);

    /// <summary>
    /// An album's tracks, in track order. Empty if none/error. Two callers: preview URLs for the UI,
    /// and the expected track count the download verifier checks a finished grab against. Deezer pages
    /// this endpoint at 25, so the implementation walks the pages — a partial page-1 count would make
    /// a long album look complete when it isn't.
    /// </summary>
    Task<DeezerTrack[]> GetAlbumTracks(long albumId);
}
