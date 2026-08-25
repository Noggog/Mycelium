using Mycelium.Interfaces;
using Mycelium.Plex.Services;
using Mycelium.Plex.Services.Singletons;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// <see cref="IArtistTagger"/> over Plex. Stamps a user's like/dislike onto the artist in Plex as a Mood
/// tag (e.g. "noggog_liked"), so a taste verdict made in the app is visible in Plex and filterable by a
/// music smart playlist via the "Artist Mood" field.
///
/// <para><b>Why Mood.</b> Plex will filter artists on genre, mood, style, country and collection — and
/// nothing else; Label isn't among them, so a label can't drive a smart playlist. Of the five, Mood is the
/// one that tags the artist without creating a library object: a Collection appears in the library's
/// Collections tab, and no Plex setting hides it there (the per-collection display mode only governs
/// inline display in the main library view).</para>
///
/// <para><b>Reconciling, one pass.</b> We read the artist's current moods and write back the keeper added
/// and the drops removed — preserving genres (a separate field) and any other user's tags. Critically it
/// also preserves hand-applied moods on the same field, which existing smart collections filter on. Doing
/// the add and remove in the same scan means a rating (which both stamps the new verdict and strips the
/// opposite) costs one read, not two. A name can map to more than one Plex item (Plex joins collaborators
/// into a single ';'-delimited title), so every item the name appears in is updated, matching how the rest
/// of the app reads names.</para>
///
/// <para><b>Targeted, with a scan fallback.</b> The catalog stores each artist's Plex rating key(s)
/// (captured on every refresh), so the hot path reads those keys and fetches just those items instead
/// of pulling the whole ~1800-artist library. When no keys are stored (cold cache / artist not in the
/// catalog) or a stored key has gone stale (returns no item), it falls back to the legacy name scan,
/// which the next catalog refresh repairs.</para>
///
/// <para><b>Best-effort.</b> Failures are logged, never thrown — tagging is a side effect of rating and
/// must not fail the rating itself.</para>
/// </summary>
public class PlexArtistTagger : IArtistTagger
{
    private readonly IPlexApi _plexApi;
    private readonly IArtistCatalogRepo _catalog;
    private readonly ILogger<PlexArtistTagger> _logger;

    public PlexArtistTagger(IPlexApi plexApi, IArtistCatalogRepo catalog, ILogger<PlexArtistTagger> logger)
    {
        _plexApi = plexApi;
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>
    /// Computes the mood set for one Plex item — see <see cref="MoodTags.Reconcile"/>, which the album
    /// tagger shares so both write the same delta.
    /// </summary>
    internal static IReadOnlyList<string>? ReconcileMoods(
        string[] existing, string? add, IReadOnlyCollection<string> remove) =>
        MoodTags.Reconcile(existing, add, remove);

    public async Task SetTags(string artistName, string? add, IReadOnlyCollection<string> remove)
    {
        var addTag = string.IsNullOrWhiteSpace(add) ? null : add;
        if (string.IsNullOrWhiteSpace(artistName) || (addTag == null && remove.Count == 0))
        {
            return;
        }

        try
        {
            var keys = await _catalog.GetPlexRatingKeys(new ArtistKey(artistName));
            if (keys.Count == 0)
            {
                // Cold cache, or an artist not in the catalog (e.g. a thumbed not-in-library related
                // artist) — fall back to the name scan, exactly as before the optimization.
                await SetTagsByScan(artistName, addTag, remove);
                return;
            }

            var library = await _plexApi.ResolveLibrary();
            foreach (var key in keys)
            {
                var item = await _plexApi.GetMusicArtist(key);
                if (item == null)
                {
                    // Stale key (library rebuild, remove+re-add). Repair this op via the scan; the next
                    // catalog refresh rewrites the correct keys.
                    _logger.LogInformation(
                        "Stored Plex key {Key} for {Artist} no longer resolves; falling back to scan",
                        key, artistName);
                    await SetTagsByScan(artistName, addTag, remove);
                    return;
                }

                await ApplyMoods(library.Key, item, addTag, remove);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update Plex moods on {Artist}", artistName);
        }
    }

    /// <summary>
    /// The legacy path: pull the whole library, name-match, and reconcile. Kept as the fallback for
    /// cold cache / stale keys / artists absent from the catalog.
    /// </summary>
    private async Task SetTagsByScan(string artistName, string? addTag, IReadOnlyCollection<string> remove)
    {
        var library = await _plexApi.ResolveLibrary();
        var matches = (await _plexApi.GetMusicArtists(library.Key))
            .Where(a => ArtistNames.Split(a.Title)
                .Any(n => string.Equals(n, artistName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (matches.Length == 0)
        {
            _logger.LogInformation("No Plex artist matched {Artist}; skipped tag update", artistName);
            return;
        }

        foreach (var artist in matches)
        {
            await ApplyMoods(library.Key, artist, addTag, remove);
        }
    }

    private async Task ApplyMoods(
        int libraryKey, PlexMusicArtist artist, string? addTag, IReadOnlyCollection<string> remove)
    {
        var existing = artist.Moods();
        var next = ReconcileMoods(existing, addTag, remove);
        if (next == null)
        {
            return; // already in the desired state on this item
        }

        // Plex tag edits are add-only unless removals are spelled out explicitly, so send the delta. Drops
        // carry the casing Plex actually stores, so a stale tag whose case differs from the app's generated
        // form (e.g. "Noggog_liked" vs "noggog_liked") still matches and is removed.
        var toAdd = next.Where(c => !existing.Contains(c, StringComparer.OrdinalIgnoreCase)).ToArray();
        var toRemove = existing.Where(c => !next.Contains(c, StringComparer.OrdinalIgnoreCase)).ToArray();

        await _plexApi.SetArtistMoods(libraryKey, artist.RatingKey, toAdd, toRemove);
        _logger.LogInformation(
            "Updated Plex moods on {Artist} ({Key}): +[{Add}] -[{Remove}]",
            artist.Title, artist.RatingKey, string.Join(", ", toAdd), string.Join(", ", toRemove));
    }
}
