using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// The Library Catalog sync job: pulls artists from Plex and upserts them into the
/// local catalog store. This is the only path that touches Plex — daily reads go
/// through <see cref="ILibraryProvider"/> against the stored catalog instead.
/// Single-flight: the daily sync and the download settle pass can both ask for a refresh, and two
/// overlapping whole-library reads would only duplicate work against Plex, so a second caller waits
/// for the one in progress.
/// </summary>
public class CatalogRefresher
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly ILibraryQuery _libraryQuery;
    private readonly IArtistCatalogRepo _catalog;
    private readonly ILogger<CatalogRefresher> _logger;

    public CatalogRefresher(
        ILibraryQuery libraryQuery,
        IArtistCatalogRepo catalog,
        ILogger<CatalogRefresher> logger)
    {
        _libraryQuery = libraryQuery;
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<CatalogSyncResult> Refresh()
    {
        await _gate.WaitAsync();
        try
        {
            var artists = await _libraryQuery.QueryAllArtistMetadata();
            var syncedAt = DateTimeOffset.UtcNow;
            var result = await _catalog.SyncFromLibrary(artists, syncedAt);

            // Owned albums come from the same Plex library; store them so the missing-album diff has a
            // local source of truth (and only after the artist upsert, so the docs exist to attach to).
            var albums = await _libraryQuery.QueryAllAlbums();
            await _catalog.SyncAlbums(albums);

            _logger.LogInformation(
                "Catalog refresh: {Upserted} upserted ({Arrived} newly present), {MarkedAbsent} marked absent, " +
                "{TotalPresent} present, {AlbumArtists} artists with albums",
                result.Upserted, result.NewlyPresent.Count, result.MarkedAbsent, result.TotalPresent, albums.Length);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
