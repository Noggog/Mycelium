using Mycelium.Interfaces;
using Mycelium.Plex.Services;
using Mycelium.Plex.Services.Singletons;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// Dev/maintenance for the per-user Plex like/dislike mood tags written by <see cref="PlexArtistTagger"/>.
/// Lets us wipe the managed tags back to a clean slate and rebuild them from the stored ratings, so
/// iterating on the tagging logic doesn't leave orphaned tags scattered across the Plex library.
///
/// <para>The wipe takes exactly the verdict tags — the "_liked"/"_disliked" suffix namespace (see
/// <see cref="ArtistTag.IsVerdict"/>) — because they're the ones <see cref="ReapplyFromRatings"/> can put
/// back. Every other mood is left alone: provider-supplied descriptors, hand-applied tags like the
/// "ambient"/"heavy" moods driving existing smart collections, and the "&lt;user&gt;_added" credits, which
/// are permanent and reconstructible from nothing — clearing one would destroy it for good. The clear also
/// strips managed <em>collections</em>, since an earlier version of the tagger wrote the verdicts as
/// collection memberships; that's how those get swept out of the library (a collection Plex empties this
/// way is deleted). Current tags are read from the section listing (Plex returns moods inline, and
/// collections with includeCollections=1), and each write sends only a delta so it never drops tags it
/// didn't intend to.</para>
/// </summary>
public class PlexTagMaintenance
{
    private readonly PlexApi _plexApi;
    private readonly IUserQueueRepo _queue;
    private readonly IUserRepo _users;
    private readonly ILogger<PlexTagMaintenance> _logger;

    public PlexTagMaintenance(
        PlexApi plexApi, IUserQueueRepo queue, IUserRepo users, ILogger<PlexTagMaintenance> logger)
    {
        _plexApi = plexApi;
        _queue = queue;
        _users = users;
        _logger = logger;
    }

    /// <summary>The outcome of a <see cref="Rebuild"/>: artists wiped, then (artist, tag) pairs applied.</summary>
    public readonly record struct RebuildResult(int Cleared, int Applied);

    /// <summary>Wipe then reapply — the canonical "nuke the tags and rebuild from ratings" operation.</summary>
    public async Task<RebuildResult> Rebuild()
    {
        var cleared = await ClearManagedTags();
        var applied = await ReapplyFromRatings();
        return new RebuildResult(cleared, applied);
    }

    /// <summary>
    /// Strips every verdict ("_liked"/"_disliked") tag from every artist in the library — moods, plus the
    /// collections the pre-mood tagger left behind — preserving all other moods and collections, the
    /// permanent "&lt;user&gt;_added" credits among them. A
    /// collection emptied this way is deleted by Plex, which is what clears them out of the library's
    /// Collections tab. Returns the number of artists changed.
    /// </summary>
    public async Task<int> ClearManagedTags()
    {
        var library = await _plexApi.ResolveLibrary();
        var changed = 0;
        foreach (var artist in await _plexApi.GetMusicArtists(library.Key))
        {
            var moods = artist.Moods().Where(ArtistTag.IsVerdict).ToArray();
            var collections = artist.Collections().Where(ArtistTag.IsVerdict).ToArray();
            if (moods.Length == 0 && collections.Length == 0)
            {
                continue; // no verdict tags on this artist
            }

            // Plex only drops tags via an explicit removal, so strip the managed ones by name (their
            // stored casing), leaving every other tag in both fields untouched.
            if (moods.Length > 0)
            {
                await _plexApi.SetArtistMoods(library.Key, artist.RatingKey, Array.Empty<string>(), moods);
            }
            if (collections.Length > 0)
            {
                await _plexApi.SetArtistCollections(
                    library.Key, artist.RatingKey, Array.Empty<string>(), collections);
            }
            changed++;
        }

        _logger.LogInformation("Cleared verdict Plex tags from {Count} artist(s)", changed);
        return changed;
    }

    /// <summary>
    /// Reapplies like/dislike moods from the stored ratings of every user that has any. The tag prefix
    /// comes from each user's stored username (the same source the live rating path uses); users with no
    /// usable username are skipped. Tags are accumulated per artist and diffed against the artist's current
    /// moods, so one edit per artist carries every applicable tag (and an already-present tag is a no-op).
    /// Returns the number of (artist, tag) applications.
    /// </summary>
    public async Task<int> ReapplyFromRatings()
    {
        var library = await _plexApi.ResolveLibrary();
        var artists = await _plexApi.GetMusicArtists(library.Key);
        var byName = BuildNameIndex(artists);

        // ratingKey -> the managed mood tags that should be present on that artist.
        var wanted = new Dictionary<int, HashSet<string>>();
        var applied = 0;

        foreach (var userId in await _queue.GetAllUserIds())
        {
            var user = await _users.Get(userId);
            foreach (var rating in await _queue.GetRated(userId))
            {
                // GetRated is "everything not pending", so it also carries snoozed rows — a deferred
                // decision, which ArtistTag.For would otherwise fold into "_disliked". Only a thumb
                // earns a tag, matching what the live rating path writes.
                if (rating.Status is not (DiscoveryStatus.Liked or DiscoveryStatus.Disliked))
                {
                    continue;
                }

                var tag = ArtistTag.For(user?.Username, rating.Status);
                if (tag == null || !byName.TryGetValue(rating.Artist.ArtistName, out var items))
                {
                    continue;
                }

                foreach (var item in items)
                {
                    if (!wanted.TryGetValue(item.RatingKey, out var tags))
                    {
                        wanted[item.RatingKey] = tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }

                    if (tags.Add(tag))
                    {
                        applied++;
                    }
                }
            }
        }

        var byKey = artists.ToDictionary(a => a.RatingKey);
        foreach (var (ratingKey, tags) in wanted)
        {
            var current = byKey[ratingKey].Moods();
            var toAdd = tags.Where(t => !current.Contains(t, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (toAdd.Length > 0)
            {
                await _plexApi.SetArtistMoods(library.Key, ratingKey, toAdd, Array.Empty<string>());
            }
        }

        _logger.LogInformation(
            "Reapplied {Applied} Plex mood tag(s) across {Artists} artist(s)", applied, wanted.Count);
        return applied;
    }

    /// <summary>Indexes Plex artist items by each name encoded in their (possibly ';'-joined) title.</summary>
    private static Dictionary<string, List<PlexMusicArtist>> BuildNameIndex(PlexMusicArtist[] artists)
    {
        var index = new Dictionary<string, List<PlexMusicArtist>>(StringComparer.OrdinalIgnoreCase);
        foreach (var artist in artists)
        {
            foreach (var name in ArtistNames.Split(artist.Title))
            {
                if (!index.TryGetValue(name, out var list))
                {
                    index[name] = list = new List<PlexMusicArtist>();
                }
                list.Add(artist);
            }
        }
        return index;
    }
}
