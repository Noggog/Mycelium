namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// The artist photo Plex holds, as a URL a browser can load. Plex serves images only to a token, so
/// the URL points at this app's proxy (<c>GET /api/artists/plex-image</c>) rather than at Plex.
///
/// <para>Only ever a fallback: the Deezer photo in the catalog wins when there is one. What this covers
/// is the owned artist Deezer has never heard of — a radio show, a private press, a local band — which
/// would otherwise go without a picture everywhere.</para>
/// </summary>
public static class PlexArtistImage
{
    /// <summary>The edge the proxy asks Plex to scale to — the largest any surface draws an artist at.</summary>
    public const int Size = 400;

    /// <summary>
    /// The proxy URL for an artist whose catalog row carries <paramref name="plexThumb"/>, or null when
    /// it carries none. The thumb path embeds the time Plex last changed the photo; passing it as
    /// <c>v</c> makes a changed photo a new URL, so the proxy can let browsers cache for a long time.
    /// </summary>
    public static string? Url(string artistName, string? plexThumb)
    {
        if (string.IsNullOrWhiteSpace(plexThumb))
        {
            return null;
        }

        var version = plexThumb[(plexThumb.LastIndexOf('/') + 1)..];
        return $"/api/artists/plex-image?artist={Uri.EscapeDataString(artistName)}&v={Uri.EscapeDataString(version)}";
    }
}
