using Mycelium.Plex.Services.Smart;

namespace Mycelium.Plex.Services.Singletons;

/// <summary>
/// A playlist as the server reports it. <see cref="Content"/> is the stored rule query and is only
/// populated by the per-playlist read — the collection listing omits it — and only for smart playlists.
/// </summary>
/// <remarks>
/// <see cref="RatingKey"/> is a string because that is how Plex serialises it in JSON, and nothing here
/// does arithmetic on it.
/// </remarks>
public record PlexPlaylist(string RatingKey, string Title, bool Smart, int LeafCount, string? Content)
{
    /// <summary>
    /// The section and rules this playlist selects over, or false when it has no parseable rule query
    /// (a non-smart playlist, or a filter shape the parser doesn't model). Callers surveying a whole
    /// account skip rather than fail on those.
    /// </summary>
    public bool TryGetFilter(out int sectionKey, out PlexSmartFilter filter) =>
        PlexFilterParser.TryParseContent(Content, out sectionKey, out filter);
}

/// <summary>
/// One track in a hand-built playlist, at its position in the running order.
/// </summary>
/// <param name="Position">
/// 1-based index in the playlist. Order is part of what a curated playlist *is*, so it is stored
/// rather than left to whatever sequence the rows happen to come back in.
/// </param>
/// <param name="File">
/// The backing file in the server's own path namespace — the identity that outlives this Plex install,
/// since rating keys are reissued by a rebuild and files are not.
/// </param>
public record PlexPlaylistItem(
    int Position, string? Artist, string? Album, string? Title, string? File);

/// <summary>One entry of a section's tag vocabulary — <see cref="Key"/> is the numeric id rules store.</summary>
public record PlexTagEntry(string Key, string Title);

/// <summary>
/// Playlist reads and writes, split out from <see cref="IPlexApi"/> because every call is made <b>on
/// behalf of a specific Plex account</b> and so takes that account's token explicitly.
///
/// <para>This is the crux of the feature: playlists, track ratings, play counts and last-played are all
/// per-account in Plex. Creating a "4 star" playlist with the server owner's token would file it in the
/// owner's sidebar and filter it by the owner's ratings — for every user. Passing the user's own token
/// makes both correct at once, with no change to the rules themselves.</para>
///
/// <para>Tag reads (<see cref="GetSectionTags"/>) are the exception: a section's tag vocabulary is
/// shared library metadata, identical for everyone, so it uses the app's configured server token.</para>
/// </summary>
public interface IPlexPlaylistApi
{
    /// <summary>
    /// Every smart audio playlist visible to <paramref name="token"/>'s account, each with its rules
    /// loaded. Costs one request to list plus one per playlist — the listing endpoint won't return
    /// rules, whatever it's asked.
    /// </summary>
    Task<PlexPlaylist[]> GetSmartAudioPlaylists(string token);

    /// <summary>
    /// <b>Every</b> audio playlist visible to <paramref name="token"/>'s account — hand-built ones as
    /// well as smart ones — each with its rules loaded where it has any.
    ///
    /// <para>Distinct from <see cref="GetSmartAudioPlaylists"/>, which filters to <c>smart=1</c> server
    /// side because the stock-playlist feature only ever manages rule-driven playlists. This one exists
    /// for the metadata archive, and there the hand-built playlists are the ones that matter most: a
    /// smart playlist is a query and will rebuild itself anywhere, whereas a manually curated track
    /// list is human work that nothing can reconstruct.</para>
    /// </summary>
    Task<PlexPlaylist[]> GetAudioPlaylists(string token);

    /// <summary>
    /// The tracks of one playlist, in playlist order, with enough identity to be re-created elsewhere.
    ///
    /// <para>Only meaningful for a non-smart playlist: a smart playlist's membership is the live result
    /// of its rules, so storing a snapshot of it would archive an answer where the question is the
    /// durable thing.</para>
    /// </summary>
    Task<PlexPlaylistItem[]> GetPlaylistItems(string token, string ratingKey);

    /// <summary>Creates a smart audio playlist owned by <paramref name="token"/>'s account.</summary>
    Task<PlexPlaylist> CreateSmartPlaylist(
        string token, string title, int sectionKey, PlexSmartFilter filter);

    /// <summary>
    /// Replaces an existing smart playlist's rules in place, keeping its rating key, title and any
    /// artwork. Used to bring a drifted playlist back in line with the definition that made it.
    /// </summary>
    Task<PlexPlaylist> UpdateSmartPlaylistFilter(
        string token, string ratingKey, int sectionKey, PlexSmartFilter filter);

    /// <summary>
    /// Sets a playlist's summary — the description Plex shows beside it in every client.
    ///
    /// <para>Separate from the create call rather than a parameter of it: Plex's playlist creation
    /// takes a title and a rule query and nothing else, and the summary is an ordinary metadata edit
    /// on the playlist that comes back.</para>
    /// </summary>
    Task SetPlaylistSummary(string token, string ratingKey, string summary);

    /// <summary>
    /// Uploads <paramref name="image"/> as the poster of a playlist and selects it, so it is the cover
    /// every client shows.
    ///
    /// <para>Posting the bytes rather than handing Plex a <c>?url=</c> to fetch: the URL form needs
    /// Plex to be able to reach us over the network, which is an extra thing to be true for a
    /// decoration.</para>
    /// </summary>
    Task UploadPlaylistPoster(string token, string ratingKey, Stream image, string contentType);

    /// <summary>
    /// A section's vocabulary for one tag field (<c>mood</c>, <c>genre</c>, …) at one metadata type.
    /// Both directions are needed: name to id when building rules, id to name when reading them back for
    /// comparison. Server-wide metadata, so it uses the configured server token, not a user's.
    /// </summary>
    Task<IReadOnlyList<PlexTagEntry>> GetSectionTags(int sectionKey, string field, int type);
}
