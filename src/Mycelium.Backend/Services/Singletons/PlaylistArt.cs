namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// The cover art a starter playlist is given — the hand-picked images that ship with the app.
///
/// <para><b>Why embedded rather than files or a database.</b> The set is fixed and versioned with the
/// catalog that names it, so it travels in the same artifact: no volume to mount, no path to resolve,
/// nothing to go missing in a container. It is also the <em>only</em> copy — the same bytes are posted
/// to Plex as a playlist poster and served to the browser for the row thumbnail, so the cover on the
/// page and the cover in Plex can't drift apart.</para>
///
/// <para><b>Ids are public.</b> They travel to the SPA in the survey and come back on the art route,
/// so a request names one of <see cref="Known"/> rather than a path — a caller must never be able to
/// pick which resource gets opened.</para>
/// </summary>
public static class PlaylistArt
{
    /// <summary>The 3★ starter: a neon wireframe corridor running to a vanishing point.</summary>
    public const string ThreeStar = "three-star";

    /// <summary>The 4★ starter: a synthwave range under a low sun.</summary>
    public const string FourStar = "four-star";

    /// <summary>The 5★ starter: a supercar on a wet neon street.</summary>
    public const string FiveStar = "five-star";

    /// <summary>Frontier: a starship leaving orbit.</summary>
    public const string Frontier = "frontier";

    /// <summary>Deep Frontier: a starship at warp, a long way out.</summary>
    public const string DeepFrontier = "deep-frontier";

    /// <summary>What both the Plex upload and the HTTP response say the bytes are.</summary>
    public const string ContentType = "image/jpeg";

    /// <summary>Everything <see cref="Open"/> will serve. Anything else is not art, whoever asks.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        ThreeStar,
        FourStar,
        FiveStar,
        Frontier,
        DeepFrontier,
    };

    /// <summary>Where the SPA loads a cover from. Matches the route in <c>Program.cs</c>.</summary>
    public static string UrlFor(string id) => $"/api/playlists/art/{id}";

    /// <summary>
    /// The bytes of one cover, or null when <paramref name="id"/> is null or names none. The caller
    /// owns the stream.
    /// </summary>
    public static Stream? Open(string? id) =>
        id is not null && Known.Contains(id)
            ? typeof(PlaylistArt).Assembly.GetManifestResourceStream($"Posters/{id}.jpg")
            : null;
}
