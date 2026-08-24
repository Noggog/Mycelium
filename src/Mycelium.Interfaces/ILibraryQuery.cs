namespace Mycelium.Interfaces;

public interface ILibraryQuery
{
    Task<ArtistMetadata[]> QueryAllArtistMetadata();

    /// <summary>
    /// Every owned artist's album titles and Plex rating keys. Cheap — one listing call. Carries no
    /// audio quality; that is read separately, because Plex exposes codecs only on tracks.
    /// </summary>
    Task<ArtistAlbums[]> QueryAllAlbums();

    /// <summary>
    /// The audio quality of specific albums, by Plex rating key — one targeted read each (~14ms).
    /// Used to fill in albums we have no answer for yet, which after the initial catch-up is just
    /// whatever has newly arrived. An album that returns no tracks is absent from the result: that
    /// is "don't know", not "bad".
    /// </summary>
    Task<Dictionary<int, AudioQuality?>> QueryAlbumQuality(IReadOnlyCollection<int> albumRatingKeys);

    /// <summary>
    /// The audio quality of every album in the library, via a paged sweep of all its tracks
    /// (~82k tracks, ~22s). The catch-up read: worth it once, and whenever quality needs re-deriving
    /// from scratch. Routine upkeep uses <see cref="QueryAlbumQuality"/> instead.
    /// </summary>
    Task<Dictionary<int, AudioQuality?>> QueryAllAlbumQuality();
}
