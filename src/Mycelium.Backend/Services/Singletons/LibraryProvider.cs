using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

public interface ILibraryProvider
{
    Task<ArtistMetadata[]> GetAllArtistMetadata();

    /// <summary>
    /// The artist library enriched with each artist's resolved Deezer identity (for the Artists
    /// page: link-out, fan count, and spotting/fixing misassociations). Reads only the local
    /// catalog — no live Deezer calls.
    /// </summary>
    Task<ArtistListItem[]> GetArtistList();
}

/// <summary>One Artists-page row: the library artist, its genre tags, and whatever Deezer identity
/// it resolved to.</summary>
public record ArtistListItem(
    ArtistKey ArtistKey,
    string? ArtistImageUrl,
    IReadOnlyList<string> Genres,
    long? DeezerId,
    string? DeezerName,
    int? DeezerFans,
    string? DeezerLink,
    bool DeezerOverride);

/// <summary>
/// Serves the artist library for daily reads from the local catalog store — not from
/// Plex. Keeping reads off the live Plex path is what lets the app stay usable when
/// Plex is offline; the catalog is refreshed separately by <see cref="CatalogRefresher"/>.
/// </summary>
public class LibraryProvider : ILibraryProvider
{
    private readonly IArtistCatalogRepo _catalog;

    public LibraryProvider(IArtistCatalogRepo catalog)
    {
        _catalog = catalog;
    }

    public async Task<ArtistMetadata[]> GetAllArtistMetadata()
    {
        return (await _catalog.GetAllPresent())
            .Select(x => new ArtistMetadata(x.ArtistKey, ImageOf(x)))
            .ToArray();
    }

    public async Task<ArtistListItem[]> GetArtistList()
    {
        return (await _catalog.GetAllPresent())
            .Select(x => new ArtistListItem(
                x.ArtistKey,
                ImageOf(x),
                x.Genres ?? Array.Empty<string>(),
                x.Deezer?.Id,
                x.Deezer?.Name,
                x.Deezer?.Fans,
                x.Deezer?.Link,
                x.DeezerOverride))
            .ToArray();
    }

    /// <summary>The Deezer photo when there is one, else the one Plex holds.</summary>
    private static string? ImageOf(CatalogArtist artist) =>
        artist.ArtistImageUrl ?? PlexArtistImage.Url(artist.ArtistKey.ArtistName, artist.PlexThumb);
}
