using Microsoft.Extensions.Logging;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// One library the app's catalog is synced from (Plex now, Navidrome eventually) for the Artists-page
/// "Library" tab. Each owns a <see cref="Source"/> tag and knows how to report whether an artist is in
/// that library and build deep links to open it there. Adding a library is just adding an
/// implementation (it auto-registers via the assembly scan), mirroring <see cref="ISourceIdentityCorrector"/>.
/// </summary>
public interface ILibraryLinker
{
    string Source { get; }
    string Label { get; }

    /// <summary>The artist's presence + open-in links for this library (Present false when absent).</summary>
    Task<LibrarySource> Get(ArtistKey artist);
}

/// <summary>Plex library linker — builds app.plex.tv deep links from the artist's stored rating keys.</summary>
public class PlexLibraryLinker : ILibraryLinker
{
    public string Source => "plex";
    public string Label => "Plex";

    private readonly IArtistCatalogRepo _catalog;
    private readonly IPlexApi _plex;
    private readonly ILogger<PlexLibraryLinker> _logger;

    public PlexLibraryLinker(IArtistCatalogRepo catalog, IPlexApi plex, ILogger<PlexLibraryLinker> logger)
    {
        _catalog = catalog;
        _plex = plex;
        _logger = logger;
    }

    public async Task<LibrarySource> Get(ArtistKey artist)
    {
        var keys = await _catalog.GetPlexRatingKeys(artist);
        if (keys.Count == 0)
        {
            return new LibrarySource(Source, Label, Present: false, Array.Empty<LibraryLink>());
        }

        // The artist is in Plex; the deep links need the server's machineIdentifier. If Plex is
        // unreachable we still report presence, just without the links (rather than failing the tab).
        string? machineId;
        try
        {
            machineId = await _plex.GetMachineIdentifier();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't fetch Plex machineIdentifier for {Artist}'s links", artist.ArtistName);
            machineId = null;
        }

        if (string.IsNullOrEmpty(machineId))
        {
            return new LibrarySource(Source, Label, Present: true, Array.Empty<LibraryLink>());
        }

        // One link per rating key — a single name usually has one, but can map to several Plex items.
        var links = keys
            .Select((key, i) => new LibraryLink(
                keys.Count > 1 ? $"Open in Plex ({i + 1})" : "Open in Plex",
                PlexDeepLink.ToItem(machineId, key)))
            .ToArray();

        return new LibrarySource(Source, Label, Present: true, links);
    }
}

/// <summary>
/// Builds the library-presence view for the Artists-page "Library" tab: one row per registered
/// <see cref="ILibraryLinker"/> in a fixed display order. Mirrors <see cref="ArtistSourcesService"/>.
/// </summary>
public class LibrarySourcesService
{
    // Libraries render in this order; any future linker not listed falls to the end.
    private static readonly string[] DisplayOrder = { "plex", "navidrome" };

    private readonly IReadOnlyList<ILibraryLinker> _linkers;

    public LibrarySourcesService(IEnumerable<ILibraryLinker> linkers)
    {
        _linkers = linkers
            .OrderBy(l => Array.IndexOf(DisplayOrder, l.Source) is var i && i < 0 ? int.MaxValue : i)
            .ToArray();
    }

    public async Task<ArtistLibraries> Get(ArtistKey artist)
    {
        var rows = new List<LibrarySource>();
        foreach (var linker in _linkers)
        {
            rows.Add(await linker.Get(artist));
        }

        return new ArtistLibraries(artist, rows);
    }
}
