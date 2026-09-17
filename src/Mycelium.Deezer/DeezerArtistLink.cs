using System.Text.RegularExpressions;

namespace Mycelium.Deezer;

/// <summary>
/// Reads a Deezer artist id out of whatever a user pastes into an artist picker — a copied address bar
/// (with or without the locale segment, with or without share tracking) or a bare id. The artist twin
/// of <see cref="DeezerAlbumLink"/>, and like it never follows short <c>page.link</c> redirects.
/// </summary>
public static class DeezerArtistLink
{
    // Anchored on the "artist" path segment so an album or playlist URL can't be read as an artist id.
    private static readonly Regex ArtistUrl = new(
        @"(?:^|/)artist/(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BareId = new(@"^\d+$", RegexOptions.Compiled);

    /// <summary>The artist id in a pasted Deezer artist URL, or null when it holds none.</summary>
    public static long? TryParseUrl(string? pasted)
    {
        var match = ArtistUrl.Match(pasted?.Trim() ?? "");
        return match.Success ? Positive(match.Groups[1].Value) : null;
    }

    /// <summary>
    /// The text as a bare id, or null. Kept apart from <see cref="TryParseUrl"/> because a bare number is
    /// also a plausible artist name ("311", "1349"), so callers should look it up alongside a name
    /// search rather than instead of one.
    /// </summary>
    public static long? TryParseBareId(string? pasted)
    {
        var text = pasted?.Trim() ?? "";
        return BareId.IsMatch(text) ? Positive(text) : null;
    }

    private static long? Positive(string digits) =>
        long.TryParse(digits, out var id) && id > 0 ? id : null;
}
