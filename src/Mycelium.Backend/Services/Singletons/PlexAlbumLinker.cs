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
/// Fills in the "open in Plex" link on the merge picker's suggestions, so a near-miss title ("Doom:
/// Original Game Soundtrack" offered for Deezer's "DOOM (Original Game Soundtrack)") can be eyeballed
/// against the real album before the merge is recorded.
///
/// Best-effort throughout: an album whose Plex rating key isn't captured yet (catalog synced before
/// keys were stored), or a Plex server we can't reach to identify, just yields a suggestion without a
/// link rather than failing the picker.
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

        string? machineId;
        try
        {
            machineId = await _plex.GetMachineIdentifier();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't fetch Plex machineIdentifier for merge suggestion links");
            machineId = null;
        }

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
}
