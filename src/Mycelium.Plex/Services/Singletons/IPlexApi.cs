namespace Mycelium.Plex.Services.Singletons;

/// <summary>
/// The Plex HTTP surface used by the app. Extracted so Plex-touching orchestration (notably the
/// artist tagger's stored-key vs. full-scan paths) can be unit-tested against a mock instead of a
/// live server. <see cref="PlexApi"/> is the only implementation; the <c>PlexModule</c> assembly
/// scan exposes it as this interface and as itself (the same singleton).
/// </summary>
public interface IPlexApi
{
    Task<PlexLibrary[]> GetLibraries();
    Task<PlexMusicArtist[]> GetMusicArtists(int library);

    /// <summary>One artist by rating key, or <c>null</c> when the key no longer resolves.</summary>
    Task<PlexMusicArtist?> GetMusicArtist(int ratingKey);

    Task<PlexMusicAlbum[]> GetMusicAlbums(int library);

    /// <summary>
    /// Every track ("leaf") under an artist rating key, across all their albums, carrying each track's
    /// per-account <c>userRating</c> (Plex's 0–10 scale; null when unrated). Empty when the key no longer
    /// resolves. Used to summarise the user's song ratings for an artist in the discovery readout.
    /// </summary>
    Task<PlexTrack[]> GetArtistTracks(int ratingKey);

    Task<PlexRecentlyAddedItem[]> GetRecentlyAdded(int libraryKey, int maxResults = 5);
    Task RefreshLibrary(int libraryKey);
    /// <summary>Adds/removes Mood tags on an artist in one edit — the app's like/dislike tagging.</summary>
    Task SetArtistMoods(
        int library, int ratingKey, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove);

    /// <summary>The Collection-field twin of <see cref="SetArtistMoods"/>, used only to strip the
    /// like/dislike collections an earlier version of the tagger wrote.</summary>
    Task SetArtistCollections(
        int library, int ratingKey, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove);
    Task<PlexLibrary> ResolveLibrary();

    /// <summary>
    /// The server's stable <c>machineIdentifier</c> (the id app.plex.tv deep links are keyed by), or
    /// <c>null</c> if the server is unreachable. Cached after the first successful fetch.
    /// </summary>
    Task<string?> GetMachineIdentifier();
}
