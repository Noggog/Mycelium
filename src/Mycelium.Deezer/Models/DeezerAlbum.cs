namespace Mycelium.Deezer.Models;

/// <summary>
/// An album as returned by the Deezer public API (<c>GET /artist/{id}/albums</c>). Field names are
/// lower/snake-case to match the JSON verbatim (Newtonsoft binds by exact name). <c>record_type</c>
/// is one of "album" / "single" / "ep" / "compilation" — the missing-album diff keeps "album" and
/// "ep", dropping singles/compilations to avoid drowning the feed.
/// </summary>
public class DeezerAlbum
{
    public long id { get; set; }
    public string? title { get; set; }
    public string? record_type { get; set; }

    /// <summary>
    /// How many tracks the release has, as the listing/search endpoints report it. Shown on a
    /// collection row so a one-track "album" is recognisable as the stray it is before it's queued.
    /// Zero when Deezer didn't supply it (the discography listing omits it on some rows).
    /// </summary>
    public int nb_tracks { get; set; }

    /// <summary>
    /// The album's album-artist — present on <c>GET /album/{id}</c> but NOT on the
    /// <c>GET /artist/{id}/albums</c> discography listing (there an album is implicitly the listed
    /// artist's). For a collaboration this is the credited release act (e.g. a duo name), which is
    /// what a downloader tags the files with and what the library files the album under.
    /// </summary>
    public DeezerArtist? artist { get; set; }

    /// <summary>
    /// The album's barcode, on <c>GET /album/{id}</c> only. One of possibly several: Deezer answers
    /// <c>/album/upc:{barcode}</c> for barcodes other than this one, so a MusicBrainz barcode that
    /// doesn't equal it can still be the same album.
    /// </summary>
    public string? upc { get; set; }

    /// <summary>Record label, on <c>GET /album/{id}</c> only.</summary>
    public string? label { get; set; }

    /// <summary>
    /// Whether the album can be streamed from where the request was made — Deezer answers per the
    /// caller's region. On <c>GET /album/{id}</c> only; null when Deezer didn't say.
    /// </summary>
    public bool? available { get; set; }

    /// <summary>
    /// Every credited artist with their role ("Main", "Featured"), on <c>GET /album/{id}</c> only. A
    /// Deezer artist page lists albums the artist is merely featured on; this tells the two apart.
    /// </summary>
    public List<DeezerContributor>? contributors { get; set; }

    /// <summary>
    /// Release date as Deezer supplies it — "2023-05-19" on the discography listing (an older or
    /// sparse release can carry just a year, or nothing at all).
    /// </summary>
    public string? release_date { get; set; }

    // Deezer ships several cover sizes; we prefer the largest available.
    public string? cover_xl { get; set; }
    public string? cover_big { get; set; }
    public string? cover_medium { get; set; }

    /// <summary>Best available cover image URL, largest first, or null if Deezer supplied none.</summary>
    public string? BestCoverUrl => cover_xl ?? cover_big ?? cover_medium;

    /// <summary>
    /// The release year, parsed off the leading four digits of <see cref="release_date"/>. Null when
    /// Deezer gave no date (or a malformed one) — the UI simply omits the year then.
    /// </summary>
    public int? Year =>
        release_date is { Length: >= 4 } d && int.TryParse(d.AsSpan(0, 4), out var year) && year > 0
            ? year
            : null;

    /// <summary>
    /// The full release date, for the questions a year can't answer — chiefly whether an album
    /// followed a single, where a January single and a November album are the same <see cref="Year"/>.
    /// Null whenever Deezer gave less than a whole date: a bare year, an empty string, the "0000-00-00"
    /// it uses for an undated release. Those still yield a <see cref="Year"/>, so the two are kept
    /// separately rather than one derived from the other.
    /// </summary>
    public DateOnly? ReleaseDate =>
        DateOnly.TryParseExact(
            release_date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date)
            ? date
            : null;
}

/// <summary>One credited artist on a <see cref="DeezerAlbum"/>.</summary>
public class DeezerContributor
{
    public long id { get; set; }
    public string? name { get; set; }

    /// <summary>"Main" or "Featured".</summary>
    public string? role { get; set; }
}

/// <summary>
/// What <c>GET /album/upc:{barcode}</c> answered. <see cref="Album"/> is null when Deezer has no album
/// with that barcode — as opposed to a null <see cref="DeezerUpcLookup"/>, which is Deezer not
/// answering at all.
/// </summary>
public record DeezerUpcLookup(DeezerAlbum? Album);

/// <summary>Envelope Deezer wraps album-list responses in: <c>{ "data": [ ... ] }</c>.</summary>
public class DeezerAlbumList
{
    public List<DeezerAlbum> data { get; set; } = new();

    /// <summary>
    /// Absolute URL of the next page, or null on the last one — read only as a "there is more" flag,
    /// the same way the track listing uses it. Absent on the endpoints that don't page (a discography
    /// listing asks for the whole thing in one go), which reads as null: no next page.
    /// </summary>
    public string? next { get; set; }
}
