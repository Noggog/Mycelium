using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// <see cref="IAlbumTagger"/> over Plex. Stamps a user's like/dislike onto an <em>album</em> as a Mood
/// tag ("noggog_liked"), the album-level twin of <see cref="PlexArtistTagger"/>.
///
/// <para><b>Why an album ever carries the verdict.</b> Everywhere else the artist does, and that is the
/// right home: a thumb is a statement about an act. A compilation has no act — Deezer credits it to
/// "Various Artists" or to a soundtrack umbrella (see <see cref="UmbrellaArtist"/>) — and stamping
/// <em>that</em> would claim the user likes every compilation in the library at once. So the record
/// itself carries the tag, and a smart playlist reaches it through Plex's "Album Mood" field
/// (<c>album.mood</c>) alongside the artist rule.</para>
///
/// <para><b>Finding the album.</b> Purely from the catalog's stored album rating keys, with no library
/// scan: the artist tagger can fall back to a name sweep of ~1800 artists, but the album equivalent is
/// tens of thousands of rows and would be paid on every miss — and a miss here is the <em>normal</em>
/// case, since a collection is usually rated before it has been downloaded. The title is resolved
/// through <see cref="OwnedAlbumLookup"/> rather than looked up literally, because Plex renames what it
/// imports — "Watch The Throne (Deluxe)" is on the shelf as "Watch the Throne" — and a merge may have
/// been recorded for a rename no rule could reach. Ownership is asked the same way everywhere in this
/// app; asking it differently here would tag the wrong album, or nothing at all.</para>
///
/// <para><b>Best-effort.</b> An album the library doesn't hold yet is not an error — it's a verdict
/// waiting for its download, re-stamped by <see cref="AlbumTagBackfill"/> once it arrives. Failures are
/// logged, never thrown.</para>
/// </summary>
public class PlexAlbumTagger : IAlbumTagger
{
    private readonly IPlexApi _plexApi;
    private readonly IArtistCatalogRepo _catalog;
    private readonly IAlbumMatchOverrideRepo _overrides;
    private readonly ILogger<PlexAlbumTagger> _logger;

    public PlexAlbumTagger(
        IPlexApi plexApi,
        IArtistCatalogRepo catalog,
        IAlbumMatchOverrideRepo overrides,
        ILogger<PlexAlbumTagger> logger)
    {
        _plexApi = plexApi;
        _catalog = catalog;
        _overrides = overrides;
        _logger = logger;
    }

    public async Task SetTags(
        string albumArtist, string albumTitle, string? add, IReadOnlyCollection<string> remove)
    {
        var addTag = string.IsNullOrWhiteSpace(add) ? null : add;
        if (string.IsNullOrWhiteSpace(albumArtist)
            || string.IsNullOrWhiteSpace(albumTitle)
            || (addTag == null && remove.Count == 0))
        {
            return;
        }

        try
        {
            var ratingKey = await ResolveRatingKey(albumArtist, albumTitle);
            if (ratingKey is null)
            {
                // The library doesn't hold this record (yet). Expected for a just-rated collection —
                // the backfill re-stamps it when the download lands.
                _logger.LogInformation(
                    "No library album matched \"{Album}\" ({Artist}); skipped mood update",
                    albumTitle, albumArtist);
                return;
            }

            var item = await _plexApi.GetMusicAlbum(ratingKey.Value);
            if (item is null)
            {
                // Stale key — a library rebuild shifted it. The next catalog sync rewrites the keys and
                // the backfill picks the verdict up again; nothing to repair from here.
                _logger.LogInformation(
                    "Stored Plex album key {Key} for \"{Album}\" ({Artist}) no longer resolves; "
                    + "leaving it to the next catalog sync",
                    ratingKey, albumTitle, albumArtist);
                return;
            }

            var library = await _plexApi.ResolveLibrary();
            var existing = item.Moods();
            var next = MoodTags.Reconcile(existing, addTag, remove);
            if (next is null)
            {
                return; // already in the desired state
            }

            // Delta, not a replace — same contract as the artist tagger, so hand-applied moods and
            // other users' verdicts on the same album survive. Drops carry Plex's own casing.
            var toAdd = next.Where(c => !existing.Contains(c, StringComparer.OrdinalIgnoreCase)).ToArray();
            var toRemove = existing.Where(c => !next.Contains(c, StringComparer.OrdinalIgnoreCase)).ToArray();

            await _plexApi.SetAlbumMoods(library.Key, item.RatingKey, toAdd, toRemove);
            _logger.LogInformation(
                "Updated Plex moods on album \"{Album}\" ({Key}): +[{Add}] -[{Remove}]",
                item.Title, item.RatingKey, string.Join(", ", toAdd), string.Join(", ", toRemove));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Failed to update Plex moods on album \"{Album}\" ({Artist})", albumTitle, albumArtist);
        }
    }

    /// <summary>
    /// The Plex rating key of one owned album, or null when the library has no copy of that record
    /// under that act. The library's own spelling comes from <see cref="OwnedAlbumLookup"/> — which is
    /// what forgives Plex's renaming and honours recorded merges — and the key is then read off the
    /// catalog under exactly that title.
    /// </summary>
    private async Task<int?> ResolveRatingKey(string albumArtist, string albumTitle)
    {
        var lookup = await OwnedAlbumLookup.Load(_catalog, _overrides);
        if (lookup.LibraryTitle(albumArtist, albumTitle) is not { } libraryTitle)
        {
            return null;
        }

        var byArtist = await _catalog.GetAlbumPlexRatingKeys(new[] { albumArtist });
        return byArtist.TryGetValue(albumArtist, out var albums)
               && albums.TryGetValue(libraryTitle, out var key)
            ? key
            : null;
    }
}
