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

    /// <summary>
    /// How much work to spend working out what format the owned albums are in. Plex exposes codecs
    /// only on tracks, so this is always a second read on top of the album listing — the question is
    /// how big a one.
    /// </summary>
    public enum QualityRead
    {
        /// <summary>
        /// Fill in only the albums we have no answer for — one targeted read each (~14ms). After the
        /// initial catch-up that is just whatever has newly arrived, from any source: a Mycelium
        /// download, or files someone dropped straight into the library. The routine setting.
        /// </summary>
        GapFill,

        /// <summary>
        /// Re-derive every album from a paged sweep of the whole library (~82k tracks, ~22s). The
        /// catch-up read, and the way to recompute from scratch if the stored answers are ever
        /// suspect. Operator-triggered from the dev panel, not scheduled.
        /// </summary>
        Full,

        /// <summary>
        /// Don't read quality at all; leave whatever is stored. For passes that only need to know
        /// whether an album has <em>appeared</em> — chiefly the settle poll that runs every few
        /// minutes for hours after a download.
        /// </summary>
        Skip,
    }

    /// <summary>
    /// The most albums one gap-fill pass will resolve. A bound rather than a budget: before the
    /// catch-up sweep every album is unknown, and without a cap the first "cheap" sync would quietly
    /// become 8,000 sequential Plex calls. Anything left over is picked up by the next pass, and the
    /// shortfall is logged rather than passed over in silence.
    /// </summary>
    private const int GapFillLimit = 250;

    public async Task<CatalogSyncResult> Refresh(QualityRead quality = QualityRead.GapFill)
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
            albums = await WithQuality(albums, quality);
            await _catalog.SyncAlbums(albums, qualityKnown: quality != QualityRead.Skip);

            _logger.LogInformation(
                "Catalog refresh: {Upserted} upserted ({Arrived} newly present), {MarkedAbsent} marked absent, " +
                "{TotalPresent} present, {AlbumArtists} artists with albums (quality: {Quality})",
                result.Upserted, result.NewlyPresent.Count, result.MarkedAbsent, result.TotalPresent,
                albums.Length, quality);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Attaches each album's audio quality to the freshly-pulled list, carrying forward what is
    /// already stored so a pass only pays for what it doesn't yet know.
    ///
    /// <para>The result is written back in full (<c>qualityKnown: true</c>), which is why the carry
    /// forward matters: writing only the newly-resolved albums would erase every previous answer.</para>
    /// </summary>
    private async Task<ArtistAlbums[]> WithQuality(ArtistAlbums[] albums, QualityRead mode)
    {
        if (mode == QualityRead.Skip)
        {
            return albums;
        }

        // Stored answers, by artist then title — what we already know and needn't ask Plex about.
        var stored = await _catalog.GetOwnedAlbums();
        AudioQuality? Known(string artist, string title) =>
            stored.TryGetValue(artist, out var byTitle) && byTitle.TryGetValue(title, out var q) ? q : null;

        Dictionary<int, AudioQuality?> resolved;
        if (mode == QualityRead.Full)
        {
            resolved = await _libraryQuery.QueryAllAlbumQuality();
        }
        else
        {
            var unknown = albums
                .SelectMany(a => a.Albums.Where(al => Known(a.Artist.ArtistName, al.Title) is null))
                .Select(al => al.PlexRatingKey)
                .Where(key => key != 0)
                .Distinct()
                .ToList();

            if (unknown.Count > GapFillLimit)
            {
                _logger.LogInformation(
                    "Catalog refresh: {Unknown} album(s) have no audio quality recorded; resolving {Limit} "
                    + "this pass, the rest on later ones (run the dev panel's full sweep to do them all at once)",
                    unknown.Count, GapFillLimit);
            }

            resolved = await _libraryQuery.QueryAlbumQuality(unknown.Take(GapFillLimit).ToList());
        }

        return albums
            .Select(a => new ArtistAlbums(
                a.Artist,
                a.Albums
                    .Select(al => al with
                    {
                        // Freshly read wins (a Full sweep is a deliberate re-derivation); otherwise
                        // keep what was already stored.
                        Quality = resolved.TryGetValue(al.PlexRatingKey, out var q)
                            ? q
                            : Known(a.Artist.ArtistName, al.Title),
                    })
                    .ToArray()))
            .ToArray();
    }
}
