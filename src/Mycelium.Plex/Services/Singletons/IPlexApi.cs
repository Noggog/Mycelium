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

    /// <summary>One album by rating key, carrying its mood tags, or <c>null</c> when the key no longer
    /// resolves. The read half of album mood tagging (see <see cref="SetAlbumMoods"/>).</summary>
    Task<PlexMusicAlbum?> GetMusicAlbum(int ratingKey);

    /// <summary>
    /// Every track in the library, each carrying the album it belongs to and the codec of its media —
    /// the only place Plex exposes what format the files actually are (the album listing has no media
    /// info at all, and asking per album would be one request each).
    ///
    /// <para>Paged internally via <c>X-Plex-Container-Start/Size</c>, since this is the whole library
    /// in one call: measured at ~82k tracks in ~22s over 17 pages. Genre/Image/Mood/Style/Collection
    /// are excluded from the response — they roughly halve the payload and nothing here reads
    /// them.</para>
    /// </summary>
    Task<PlexLibraryTrack[]> GetMusicTracks(int library);

    /// <summary>
    /// Every track in the library that <paramref name="token"/>'s account has given a star rating,
    /// carrying enough identity to mean something outside this Plex server: the artist, album, title
    /// and track number, plus the backing file path.
    ///
    /// <para>Separate from <see cref="GetMusicTracks"/> because that read is deliberately untokenised
    /// and drops <c>userRating</c> — ratings belong to whichever account asks, so a library-wide read
    /// made as the app would report the owner's stars to everyone. Separate from
    /// <see cref="GetArtistTracks"/> because that one answers "how does this user rate this artist"
    /// and returns only a title and a number, which is enough to average and not enough to keep.</para>
    ///
    /// <para>Only rated tracks come back — in a typical library that is a small fraction of the whole,
    /// so one paged sweep per account is cheaper than it sounds and far cheaper than a request per
    /// artist.</para>
    /// </summary>
    Task<PlexRatedTrack[]> GetRatedTracks(int library, string token);

    /// <summary>
    /// The tracks of one album, for reading its codecs without sweeping the whole library. ~14ms per
    /// call against a real server, so resolving a handful of newly-arrived albums is far cheaper than
    /// re-reading all ~82k tracks. Empty when the rating key no longer resolves.
    /// </summary>
    Task<PlexLibraryTrack[]> GetAlbumTracks(int albumRatingKey);

    /// <summary>
    /// Every track ("leaf") under an artist rating key, across all their albums, carrying each track's
    /// per-account <c>userRating</c> (Plex's 0–10 scale; null when unrated). Empty when the key no longer
    /// resolves. Used to summarise the user's song ratings for an artist in the discovery readout.
    /// </summary>
    /// <summary>
    /// All tracks under an artist, read as <paramref name="token"/>. The token is required rather than
    /// optional on purpose: <c>userRating</c> belongs to whichever account asks, so a call site that
    /// forgot to say whose ratings it wanted would silently report the server owner's to everyone.
    /// </summary>
    Task<PlexTrack[]> GetArtistTracks(int ratingKey, string token);

    /// <summary>
    /// Whether this server accepts <paramref name="token"/> — asked with that token in place of the
    /// app's own, so it answers for the token rather than for us. The way to validate a token a user
    /// pasted that plex.tv doesn't recognise: a *server* access token (the kind in a Plex Web URL, and
    /// the only kind a Plex Home managed user can hand you) authenticates here without being a plex.tv
    /// account token at all. Note that it says only yes or no — the server reports the <em>owner's</em>
    /// identity whatever token asks, so it can verify a token but never attribute one.
    /// </summary>
    Task<bool> AcceptsToken(string token);

    Task<PlexRecentlyAddedItem[]> GetRecentlyAdded(int libraryKey, int maxResults = 5);
    Task RefreshLibrary(int libraryKey);
    /// <summary>Adds/removes Mood tags on an artist in one edit — the app's like/dislike tagging.</summary>
    Task SetArtistMoods(
        int library, int ratingKey, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove);

    /// <summary>
    /// Adds/removes Mood tags on an <em>album</em> in one edit — where a taste verdict goes when the
    /// record is credited to an umbrella act rather than to a band, since no artist could carry it.
    /// </summary>
    Task SetAlbumMoods(
        int library, int ratingKey, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove);

    /// <summary>The Genre-field twin of <see cref="SetArtistMoods"/>, for user tag editing.</summary>
    Task SetArtistGenres(
        int library, int ratingKey, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove);

    /// <summary>The Style-field twin of <see cref="SetArtistMoods"/>, for user tag editing.</summary>
    Task SetArtistStyles(
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
