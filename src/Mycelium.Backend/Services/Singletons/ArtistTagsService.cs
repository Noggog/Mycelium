using Microsoft.Extensions.Logging;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// Reads and edits the descriptor tags a library artist carries in Plex — genres, styles and moods —
/// behind the Browse page's "Tags" tab. Plex is the store of record here (the same fields smart
/// collections filter on), so reads go live to the artist's Plex item(s) rather than to the catalog;
/// only genres are mirrored back into the catalog, since the artist list renders those.
///
/// <para><b>The app's own moods are invisible here.</b> Two kinds share the Mood field with the
/// descriptors: like/dislike verdicts ("&lt;user&gt;_liked"/"_disliked", see <see cref="PlexArtistTagger"/>)
/// and the permanent "&lt;user&gt;_added" credits stamped on a record when an acquisition lands. Neither is
/// a descriptor — one is rating state owned by the thumbs, the other is history — so <see cref="Get"/>
/// filters both out (<see cref="ArtistTag.IsManaged"/>) and <see cref="Update"/> refuses to add or remove
/// one. Otherwise the tab would offer a second, desynced way to change a rating, and a way to hand
/// yourself credit for a record you didn't bring in.</para>
///
/// <para><b>Delta writes.</b> Edits go through the same add/remove Plex tag edit the tagger uses, so a
/// change to one tag never disturbs the rest of the field (including the managed verdict moods the tab
/// can't see). A name can map to several Plex items (';'-joined collaborator titles), so a read unions
/// across them and a write applies to every one — matching how the rest of the app treats names.</para>
/// </summary>
public class ArtistTagsService
{
    /// <summary>The Plex tag fields the tab edits, as they appear in the API's <c>field</c> parameter.</summary>
    public const string GenreField = "genre";
    public const string StyleField = "style";
    public const string MoodField = "mood";

    private static readonly string[] Fields = { GenreField, StyleField, MoodField };

    private readonly IArtistCatalogRepo _catalog;
    private readonly IPlexApi _plex;
    private readonly ILogger<ArtistTagsService> _logger;

    public ArtistTagsService(IArtistCatalogRepo catalog, IPlexApi plex, ILogger<ArtistTagsService> logger)
    {
        _catalog = catalog;
        _plex = plex;
        _logger = logger;
    }

    public static bool IsKnownField(string? field) =>
        field != null && Fields.Contains(field, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The artist's current genres, styles and moods, unioned across every Plex item the name maps to.
    /// Present is false when the artist isn't in Plex (nothing to tag) — the tab then says so instead of
    /// offering an editor that would write nowhere.
    /// </summary>
    public async Task<ArtistTags> Get(ArtistKey artist)
    {
        var items = await GetPlexItems(artist);
        if (items.Count == 0)
        {
            return Empty(artist);
        }

        return new ArtistTags(
            artist,
            Present: true,
            Genres: Union(items.Select(i => i.Genres())),
            Styles: Union(items.Select(i => i.Styles())),
            // The app's own moods — verdicts and "_added" credits — aren't descriptors; never surface them.
            Moods: Union(items.Select(i => i.Moods().Where(m => !ArtistTag.IsManaged(m)))));
    }

    /// <summary>
    /// Applies one tag edit to <paramref name="field"/> on every Plex item the artist maps to, then
    /// returns the artist's tags as they now stand. <paramref name="add"/>/<paramref name="remove"/> are
    /// the user's intent, not a final set: Plex tag edits are deltas, which is what keeps the field's
    /// other tags (including the invisible verdict moods) intact.
    ///
    /// <para>Throws <see cref="ArgumentException"/> for an unknown field or one of the app's own moods
    /// (a verdict or an "_added" credit) — both are caller bugs the endpoint turns into a 400, not
    /// conditions to paper over.</para>
    /// </summary>
    public async Task<ArtistTags> Update(
        ArtistKey artist, string field, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove)
    {
        if (!IsKnownField(field))
        {
            throw new ArgumentException($"Unknown tag field '{field}'", nameof(field));
        }

        var toAdd = Clean(add);
        var toRemove = Clean(remove);
        if (toAdd.Concat(toRemove).Any(ArtistTag.IsManaged))
        {
            throw new ArgumentException(
                "The app's own moods (verdict and \"_added\" tags) aren't editable here", nameof(add));
        }

        var items = await GetPlexItems(artist);
        if (items.Count == 0)
        {
            return Empty(artist);
        }

        var library = await _plex.ResolveLibrary();
        foreach (var item in items)
        {
            // Removals must be spelled exactly as Plex stores them (its tag drop is case-sensitive), so
            // match the user's input against the item's own tags and send back Plex's casing. A tag the
            // item doesn't carry simply isn't sent — the other items in a multi-key artist may still have it.
            var existing = Existing(item, field);
            var drops = existing
                .Where(t => toRemove.Contains(t, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            var adds = toAdd
                .Where(t => !existing.Contains(t, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (adds.Length == 0 && drops.Length == 0)
            {
                continue; // this item is already in the desired state
            }

            await Write(field, library.Key, item.RatingKey, adds, drops);
            _logger.LogInformation(
                "Updated Plex {Field} on {Artist} ({Key}): +[{Add}] -[{Remove}]",
                field, item.Title, item.RatingKey, string.Join(", ", adds), string.Join(", ", drops));
        }

        var updated = await Get(artist);
        if (string.Equals(field, GenreField, StringComparison.OrdinalIgnoreCase))
        {
            // The artist list renders genres off the catalog, so mirror the edit there rather than
            // leaving the page stale until the next Plex sync. Best-effort: the Plex write is the one
            // that matters, and the next sync repairs the catalog either way.
            try
            {
                await _catalog.SetGenres(artist, updated.Genres);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't mirror {Artist}'s genres into the catalog", artist.ArtistName);
            }
        }

        return updated;
    }

    private Task Write(
        string field, int libraryKey, int ratingKey,
        IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove) =>
        field.ToLowerInvariant() switch
        {
            GenreField => _plex.SetArtistGenres(libraryKey, ratingKey, add, remove),
            StyleField => _plex.SetArtistStyles(libraryKey, ratingKey, add, remove),
            _ => _plex.SetArtistMoods(libraryKey, ratingKey, add, remove),
        };

    private static string[] Existing(PlexMusicArtist item, string field) =>
        field.ToLowerInvariant() switch
        {
            GenreField => item.Genres(),
            StyleField => item.Styles(),
            _ => item.Moods(),
        };

    /// <summary>
    /// The Plex item(s) the artist name maps to, fetched by the rating keys the catalog captured on the
    /// last sync. Empty when the artist isn't cataloged (a not-yet-owned recommendation) or its stored
    /// keys have gone stale — the tab then reports "not in your library" rather than guessing, and the
    /// next catalog refresh repairs the keys.
    /// </summary>
    private async Task<IReadOnlyList<PlexMusicArtist>> GetPlexItems(ArtistKey artist)
    {
        var keys = await _catalog.GetPlexRatingKeys(artist);
        var items = new List<PlexMusicArtist>(keys.Count);
        foreach (var key in keys)
        {
            var item = await _plex.GetMusicArtist(key);
            if (item != null)
            {
                items.Add(item);
            }
            else
            {
                _logger.LogInformation(
                    "Stored Plex key {Key} for {Artist} no longer resolves; skipped for tags", key, artist.ArtistName);
            }
        }

        return items;
    }

    private static ArtistTags Empty(ArtistKey artist) =>
        new(artist, Present: false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    /// <summary>Case-insensitive union across a name's Plex items, alphabetical so the tab is stable.</summary>
    private static IReadOnlyList<string> Union(IEnumerable<IEnumerable<string>> sets) =>
        sets.SelectMany(s => s)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] Clean(IReadOnlyCollection<string> tags) =>
        tags.Select(t => t?.Trim() ?? "")
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
