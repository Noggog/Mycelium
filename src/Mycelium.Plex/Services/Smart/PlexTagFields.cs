namespace Mycelium.Plex.Services.Smart;

/// <summary>
/// Which filter fields hold <em>tags</em> rather than plain values, and what scope they belong to.
///
/// <para>Tag fields are the ones whose rules store a numeric id (<c>artist.mood=749936</c>). Reading
/// such a rule back into something comparable — or writing one in the first place — needs that section's
/// tag vocabulary, and the vocabulary is fetched per field <em>and</em> per metadata type. This is the
/// list of fields worth fetching, taken from what the server advertises under <c>Meta.Type[].Field[]</c>
/// with <c>type: "tag"</c>.</para>
/// </summary>
public static class PlexTagFields
{
    private static readonly HashSet<string> TagLeaves = new(StringComparer.OrdinalIgnoreCase)
    {
        "genre", "mood", "style", "collection", "country",
        "format", "subformat", "source", "label", "location",
    };

    private static readonly Dictionary<string, int> ScopeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["artist"] = PlexSmartFilter.ArtistType,
        ["album"] = PlexSmartFilter.AlbumType,
        ["track"] = PlexSmartFilter.TrackType,
    };

    /// <summary>
    /// Splits a qualified field into the tag vocabulary it draws on — e.g. <c>artist.mood</c> becomes
    /// the <c>mood</c> vocabulary at metadata type 8. False for non-tag fields (ratings, dates, counts)
    /// and for anything unscoped or unrecognised.
    /// </summary>
    public static bool TryResolve(string field, out string leaf, out int type)
    {
        leaf = "";
        type = 0;

        var dot = field.IndexOf('.');
        if (dot <= 0 || dot == field.Length - 1)
        {
            return false;
        }

        leaf = field[(dot + 1)..];
        return TagLeaves.Contains(leaf) && ScopeTypes.TryGetValue(field[..dot], out type);
    }

    /// <summary>
    /// Every tag vocabulary the given rule trees reference, deduplicated — the exact set of tag fetches
    /// a comparison needs, and no more.
    /// </summary>
    public static IReadOnlyCollection<(string Field, string Leaf, int Type)> Referenced(
        IEnumerable<PlexFilter?> trees)
    {
        var found = new Dictionary<string, (string, string, int)>(StringComparer.OrdinalIgnoreCase);

        void Walk(PlexFilter? node)
        {
            switch (node)
            {
                case null:
                    return;
                case PlexGroup group:
                    foreach (var child in group.Children)
                    {
                        Walk(child);
                    }

                    return;
                case PlexCondition condition when TryResolve(condition.Field, out var leaf, out var type):
                    // Keyed lowercase to match the canonicaliser, which lowercases fields before lookup.
                    found[condition.Field.ToLowerInvariant()] =
                        (condition.Field.ToLowerInvariant(), leaf, type);
                    return;
            }
        }

        foreach (var tree in trees)
        {
            Walk(tree);
        }

        return found.Values.ToArray();
    }
}
