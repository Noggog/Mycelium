namespace Mycelium.Deezer.Models;

/// <summary>
/// A track as returned by the Deezer public API. <c>preview</c> is a ~30-second MP3 URL that plays
/// in a plain HTML5 &lt;audio&gt; element with no auth/login (the robust alternative to the widget).
/// Field names are lower-case to match the JSON verbatim (Newtonsoft binds by exact name).
/// </summary>
public class DeezerTrack
{
    public long id { get; set; }
    public string? title { get; set; }
    public string? preview { get; set; }
    public string? link { get; set; }
}

/// <summary>
/// Envelope Deezer wraps track-list responses in: <c>{ "data": [ ... ], "total": n, "next": url }</c>.
/// Deezer serves at most 25 tracks per page, so a long album/compilation arrives split — see
/// <see cref="Services.IDeezerApi.GetAlbumTracks"/>, which walks the pages.
/// </summary>
public class DeezerTrackList
{
    public List<DeezerTrack> data { get; set; } = new();

    /// <summary>Count across every page, not just this one. Absent (0) on endpoints that don't page.</summary>
    public int total { get; set; }

    /// <summary>
    /// Absolute URL of the next page, or null on the last one. Used only as a "there is more" flag —
    /// the next request is rebuilt from the configured base URI so a DEEZER_BASE_URI override
    /// (tests, a proxy) isn't escaped by a URL Deezer hard-codes to its own host.
    /// </summary>
    public string? next { get; set; }
}
