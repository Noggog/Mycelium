using Microsoft.Extensions.Logging;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// app.plex.tv deep links to library items. Shared by every "open it in Plex" affordance — the
/// Artists-page Library tab (<see cref="PlexLibraryLinker"/>) and the merge picker's suggestions
/// (<see cref="PlexAlbumLinker"/>) — so they can't drift into two URL shapes.
/// </summary>
public static class PlexDeepLink
{
    /// <summary>
    /// One item: the server segment + the url-encoded /library/metadata/{key} path. Opens in the Plex
    /// web app (and hands off to the desktop/mobile app if installed).
    /// </summary>
    public static string ToItem(string machineId, int ratingKey)
    {
        var key = Uri.EscapeDataString($"/library/metadata/{ratingKey}");
        return $"https://app.plex.tv/desktop/#!/server/{machineId}/details?key={key}";
    }
}

/// <summary>
/// Fills in the "open in Plex" links on albums: the merge picker's suggestions, so a near-miss title
/// ("Doom: Original Game Soundtrack" offered for Deezer's "DOOM (Original Game Soundtrack)") can be
/// eyeballed against the real album before the merge is recorded, and the owned rows of an artist's
/// discography, so the "In library" marker opens the copy it's claiming we have.
///
/// Best-effort throughout: an album whose Plex rating key isn't captured yet (catalog synced before
/// keys were stored), or a Plex server we can't reach to identify, just yields the album without a
/// link rather than failing the request.
/// </summary>
public class PlexAlbumLinker
{
    private readonly IArtistCatalogRepo _catalog;
    private readonly IPlexApi _plex;
    private readonly ILogger<PlexAlbumLinker> _logger;

    public PlexAlbumLinker(IArtistCatalogRepo catalog, IPlexApi plex, ILogger<PlexAlbumLinker> logger)
    {
        _catalog = catalog;
        _plex = plex;
        _logger = logger;
    }

    /// <summary>The same options with <see cref="LibraryAlbumOption.PlexUrl"/> set where one exists.</summary>
    public async Task<LibraryAlbumOption[]> WithLinks(LibraryAlbumOption[] options)
    {
        if (options.Length == 0) return options;

        var machineId = await MachineIdentifier();
        if (string.IsNullOrEmpty(machineId)) return options;

        // Only the artists actually offered — the suggestion list is short, and a whole-library search
        // is capped, so this stays a bounded lookup rather than a second full catalog read.
        var artists = options.Select(o => o.Artist).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var keys = await _catalog.GetAlbumPlexRatingKeys(artists);

        return options
            .Select(o => keys.TryGetValue(o.Artist, out var albums) && albums.TryGetValue(o.Album, out var key)
                ? o with { PlexUrl = PlexDeepLink.ToItem(machineId, key) }
                : o)
            .ToArray();
    }

    /// <summary>
    /// The same discography with <see cref="ArtistAlbumItem.PlexUrl"/> set on the owned rows, so the
    /// "In library" marker can open the copy we have. Missing rows are left alone — there's nothing in
    /// Plex to point at.
    ///
    /// Matched on the canonical title rather than the literal one: an owned row surfaces Deezer's
    /// spelling of the title ("DOOM (Original Game Soundtrack)") while the rating key is stored under
    /// Plex's ("Doom: Original Game Soundtrack"), and those are the same album by
    /// <see cref="AlbumTitleMatcher"/>'s definition — the one the ownership flag itself was decided by.
    /// </summary>
    public async Task<IReadOnlyList<ArtistAlbumItem>> WithLinks(IReadOnlyList<ArtistAlbumItem> albums)
    {
        if (!albums.Any(a => a.Owned)) return albums;

        var machineId = await MachineIdentifier();
        if (string.IsNullOrEmpty(machineId)) return albums;

        // A discography is one artist's, but a row can be credited to a collaborator, so key off the
        // rows themselves rather than assuming a single name.
        var artists = albums
            .Where(a => a.Owned)
            .Select(a => a.Artist.ArtistName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var keys = await _catalog.GetAlbumPlexRatingKeys(artists);
        var byCanonicalTitle = keys.ToDictionary(
            e => e.Key,
            e => Canonicalize(e.Value),
            StringComparer.OrdinalIgnoreCase);

        return albums
            .Select(a => a.Owned
                && byCanonicalTitle.TryGetValue(a.Artist.ArtistName, out var titles)
                && titles.TryGetValue(AlbumTitleMatcher.NormalizeRecord(a.Album), out var key)
                    ? a with { PlexUrl = PlexDeepLink.ToItem(machineId, key) }
                    : a)
            .ToArray();
    }

    /// <summary>
    /// Album titles re-keyed by their canonical record form. Several titles can canonicalize to one key
    /// ("The Burgh Island EP" and "The Burgh Island", or a deluxe filed beside the plain LP); either
    /// copy is a fine thing to open, so the first wins rather than the lookup throwing. Record rather
    /// than listing granularity for the same reason ownership is: the row says "(Deluxe)" and Plex's
    /// copy of it doesn't, and an In Library badge that links nowhere is worse than one that opens the
    /// copy we have.
    /// </summary>
    private static Dictionary<string, int> Canonicalize(Dictionary<string, int> keysByTitle)
    {
        var canonical = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (title, key) in keysByTitle)
        {
            canonical.TryAdd(AlbumTitleMatcher.NormalizeRecord(title), key);
        }

        return canonical;
    }

    /// <summary>The server id every deep link is built from, or null when Plex can't be reached.</summary>
    private async Task<string?> MachineIdentifier()
    {
        try
        {
            return await _plex.GetMachineIdentifier();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't fetch Plex machineIdentifier for album links");
            return null;
        }
    }
}
