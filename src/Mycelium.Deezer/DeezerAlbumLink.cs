using System.Text.RegularExpressions;

namespace Mycelium.Deezer;

/// <summary>
/// Reads a Deezer album id out of whatever a user pastes. The gesture this serves is copying the
/// address bar, so the accepted forms are the ones a browser actually produces — with or without the
/// locale segment ("/en/album/…"), with or without Deezer's share tracking query — plus a bare id for
/// anyone who already has one.
///
/// Deliberately not resolved over the network: short <c>deezer.page.link</c> redirects and
/// <c>dzr.page.link</c> app links can't be read without a fetch, so they're rejected here rather than
/// turning a paste into an outbound request that may or may not answer. The user can open one and copy
/// the real URL.
/// </summary>
public static class DeezerAlbumLink
{
    // Anchored on the "album" path segment so an artist or playlist URL (/artist/5080, /playlist/123)
    // can't be silently read as an album — those ids are from different keyspaces and would resolve to
    // either nothing or, worse, an unrelated album.
    private static readonly Regex AlbumUrl = new(
        @"(?:^|/)album/(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BareId = new(@"^\d+$", RegexOptions.Compiled);

    /// <summary>
    /// The album id encoded in <paramref name="pasted"/>, or null when it holds none. Whitespace is
    /// trimmed; an id of 0 is rejected (Deezer has no album 0, and the downloader treats 0 as "no id").
    /// </summary>
    public static long? TryParse(string? pasted)
    {
        var text = pasted?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var match = AlbumUrl.Match(text);
        var digits = match.Success ? match.Groups[1].Value : BareId.IsMatch(text) ? text : null;

        return digits is not null && long.TryParse(digits, out var id) && id > 0 ? id : null;
    }
}
