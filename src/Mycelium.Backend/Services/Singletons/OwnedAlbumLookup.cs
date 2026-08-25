using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// "Does the library have this record, and under what name?" — asked once, from the catalog's owned
/// albums plus the recorded merges, and reused across the rows of one request.
///
/// <para>The name half is what makes this more than a boolean. Plex renames what it imports, so the
/// title we asked for and the title on the shelf routinely differ: "Watch The Throne (Deluxe)" arrives
/// as "Watch the Throne". Ownership forgives that through
/// <see cref="AlbumTitleMatcher.NormalizeRecord"/>, and anything that then wants to <em>act</em> on the
/// copy we have — deep link it, tag it — needs the library's own spelling to find it by.</para>
///
/// <para>Merges are consulted last, for the renames the normalizer cannot reach: Deezer's "DOOM
/// (Original Game Soundtrack)" against Plex's "Doom: Original Game Soundtrack" is one record and no
/// rule will ever say so, which is what <see cref="IAlbumMatchOverrideRepo"/> exists to record. The
/// purchase reconcile has always honoured them; so must anything that asks the same question, or a
/// merged album would close out on the buy list and still never be tagged.</para>
/// </summary>
public sealed class OwnedAlbumLookup
{
    private readonly Dictionary<string, Dictionary<string, AudioQuality?>> _owned;

    // (match artist + canonical Deezer title) -> the library title the merge points at.
    private readonly Dictionary<string, string> _overrides;

    private OwnedAlbumLookup(
        Dictionary<string, Dictionary<string, AudioQuality?>> owned, Dictionary<string, string> overrides)
    {
        _owned = owned;
        _overrides = overrides;
    }

    /// <summary>Reads both stores once. Two Mongo reads, however many albums are then asked about.</summary>
    public static async Task<OwnedAlbumLookup> Load(IArtistCatalogRepo catalog, IAlbumMatchOverrideRepo overrides) =>
        new(await catalog.GetOwnedAlbums(), await LoadOverrides(overrides));

    private static async Task<Dictionary<string, string>> LoadOverrides(IAlbumMatchOverrideRepo overrides)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var o in await overrides.GetAll())
        {
            map[AlbumOverrideKey.For(o.MatchArtist, o.DeezerTitle)] = o.LibraryTitle;
        }

        return map;
    }

    /// <summary>
    /// Every album title the library holds under <paramref name="artist"/>, in the library's own
    /// spelling; empty when it holds none. For listing what is on the shelf rather than asking about
    /// one record.
    /// </summary>
    public IReadOnlyCollection<string> TitlesFor(string artist) =>
        _owned.TryGetValue(artist, out var albums) ? albums.Keys : Array.Empty<string>();

    /// <summary>Whether the library holds this record under <paramref name="artist"/>.</summary>
    public bool Owns(string artist, string album) => LibraryTitle(artist, album) is not null;

    /// <summary>
    /// The library's own spelling of this record, or null when it doesn't hold it. Exact title first
    /// (the common case, and the cheapest), then record granularity, then a recorded merge.
    /// </summary>
    public string? LibraryTitle(string artist, string album)
    {
        if (!_owned.TryGetValue(artist, out var albums) || albums.Count == 0)
        {
            return null;
        }

        if (albums.ContainsKey(album))
        {
            return album;
        }

        var wanted = AlbumTitleMatcher.NormalizeRecord(album);
        foreach (var title in albums.Keys)
        {
            if (AlbumTitleMatcher.NormalizeRecord(title) == wanted)
            {
                return title;
            }
        }

        // A merge the user (or the reconcile) recorded: the library title it names is authoritative,
        // but only if the library still has it — a merge outlives the album it pointed at.
        return _overrides.TryGetValue(AlbumOverrideKey.For(artist, album), out var merged)
               && albums.ContainsKey(merged)
            ? merged
            : null;
    }
}
