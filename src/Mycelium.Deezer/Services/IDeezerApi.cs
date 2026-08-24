using Mycelium.Deezer.Models;

namespace Mycelium.Deezer.Services;

/// <summary>
/// Thin client over the keyless Deezer public API. Every call degrades gracefully (returns
/// null / empty) on a miss or transport error rather than throwing, so ingestion can keep
/// going when Deezer is flaky.
/// </summary>
public interface IDeezerApi
{
    /// <summary>
    /// Resolve an artist name to its Deezer artist (strongest match), or null if none. Null also
    /// covers "Deezer didn't answer", so a caller that <em>records</em> the outcome — a cached miss,
    /// a persisted "not on Deezer" — must use <see cref="SearchArtists"/> and read the distinction.
    /// </summary>
    Task<DeezerArtist?> SearchArtist(string artistName);

    /// <summary>
    /// Search Deezer for artists by name, in relevance order. An empty array is Deezer answering with
    /// nothing (a real miss); <c>null</c> means the call never got an answer — a transport error, or
    /// one of Deezer's 200-wrapped API errors such as the rate-limit quota. Used to offer the user a
    /// choice when the top hit is wrong (the "Correct association" picker), and by name resolution,
    /// which must not cache a quota blip as "this artist doesn't exist".
    /// </summary>
    Task<DeezerArtist[]?> SearchArtists(string query, int limit);

    /// <summary>Fetch a Deezer artist by its id (name, fans, image, link), or null if none/error.</summary>
    Task<DeezerArtist?> GetArtist(long artistId);

    /// <summary>Deezer's "related artists" for the given artist id (empty if none/error).</summary>
    Task<DeezerArtist[]> GetRelated(long artistId);

    /// <summary>
    /// The artist's most popular tracks (for their ~30s preview URLs), most popular first.
    /// Empty if none/error.
    /// </summary>
    Task<DeezerTrack[]> GetTopTracks(long artistId, int limit);

    /// <summary>
    /// The artist's albums (their discography). An empty array is Deezer answering with nothing (the
    /// artist really has no releases listed); <c>null</c> means the call never got an answer — a
    /// transport error, or the rate-limit quota. The distinction matters because the caller persists
    /// the diff this feeds: an unanswered call read as "no albums" erases the artist's missing-album
    /// rows and shows the user an empty discography.
    /// </summary>
    Task<DeezerAlbum[]?> GetAlbums(long artistId);

    /// <summary>
    /// Albums found by searching for the artist's name — the backfill for what
    /// <see cref="GetAlbums"/> leaves out. Deezer's discography listing is not the whole catalog: it
    /// omits releases Deezer itself credits to that artist (all of Against Me!'s post-2011 era; 87 of
    /// Walk Off The Earth's 154 releases), and those come back from album search. The search matches
    /// on name, so the results are for <em>everyone</em> with a similar one — the caller keeps only the
    /// rows whose <see cref="DeezerAlbum.artist"/> is the id it resolved. Paged internally, because the
    /// first page of a common name is mostly other acts.
    ///
    /// <c>null</c> means a page never got an answer, with the same force as in <see cref="GetAlbums"/>:
    /// a short result set is indistinguishable from "there is nothing else", and the caller persists
    /// that difference. Never partially truthful — either the whole walk answered or it's null.
    /// </summary>
    Task<DeezerAlbum[]?> SearchArtistAlbums(string artistName);

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
