namespace Mycelium.Plex.Services.Smart;

/// <summary>
/// A whole smart-playlist definition as Plex stores it: the metadata type the query runs over, the
/// non-rule query options, and the rule tree.
///
/// <para><b>On <see cref="Type"/>.</b> It's a Plex metadata type code — 8 artist, 9 album, 10 track.
/// For an audio playlist the choice is cosmetic: the playlist materialises track leaves either way
/// (a <c>type=8</c> query whose only filter is <c>track.userRating&gt;&gt;6</c> yields exactly the same
/// tracks as the <c>type=10</c> form). It only changes what a <c>sort</c> key refers to — under
/// <c>type=8</c>, <c>sort=titleSort</c> orders by <em>artist</em> title. Plex's own filter editor writes
/// <c>type=8</c>, so that's what we generate; matching deliberately ignores this field.</para>
///
/// <para><see cref="Options"/> holds the remaining non-rule params (<c>sort</c>, <c>limit</c>,
/// <c>group</c>, <c>having</c>) in the order they appeared, so a parsed filter can be written back
/// without losing them.</para>
/// </summary>
public sealed record PlexSmartFilter(
    int Type,
    PlexFilter? Rules,
    IReadOnlyList<KeyValuePair<string, string>> Options)
{
    public PlexSmartFilter(int Type, PlexFilter? Rules)
        : this(Type, Rules, Array.Empty<KeyValuePair<string, string>>())
    {
    }

    /// <summary>The metadata type code for artists — what Plex's filter editor writes for audio.</summary>
    public const int ArtistType = 8;

    public const int AlbumType = 9;
    public const int TrackType = 10;

    /// <summary>
    /// The field prefix a bare (undotted) field name belongs to, given the query's type. Plex's editor
    /// always writes fields fully qualified; only API-written filters use the bare form (e.g.
    /// <c>lastViewedAt&gt;&gt;=-2w</c> on a <c>type=10</c> query), where it means the queried type's own
    /// field. Used by <see cref="PlexFilterCanonicalizer"/> so the two spellings compare equal.
    /// </summary>
    public static string ScopePrefix(int type) => type switch
    {
        ArtistType => "artist.",
        AlbumType => "album.",
        TrackType => "track.",
        _ => "",
    };
}
