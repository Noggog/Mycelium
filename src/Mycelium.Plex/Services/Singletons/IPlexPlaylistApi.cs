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
    /// A section's vocabulary for one tag field (<c>mood</c>, <c>genre</c>, …) at one metadata type.
    /// Both directions are needed: name to id when building rules, id to name when reading them back for
    /// comparison. Server-wide metadata, so it uses the configured server token, not a user's.
    /// </summary>
    Task<IReadOnlyList<PlexTagEntry>> GetSectionTags(int sectionKey, string field, int type);
}
