using Mycelium.Interfaces;
using Mycelium.Plex.Services;
using Mycelium.Plex.Services.Singletons;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// Stamps "&lt;user&gt;_recommended" onto the artists the library <em>already has</em> that a user's
/// liked artists point at — the <see cref="FeedKind.RecommendedLibraryArtist"/> section of the
/// discovery feed, written into Plex so it can be played rather than only swiped.
///
/// <para><b>Why only owned artists.</b> A Plex mood lives on a Plex item, and the recommendation
/// <em>queue</em> is by construction everything the library doesn't have (see
/// <c>DiscoveryEngine.ExpandFrom</c>, which drops owned names) — there is no item to tag and nothing
/// to play if there were. The owned-but-unrated artists the frontier vouches for are the half of
/// "recommended to me" that Plex can actually act on.</para>
///
/// <para><b>A reconcile, not an append.</b> The set is derived — it changes whenever a like moves the
/// frontier, and an artist drops out of it the moment it's thumbed — so every pass computes what
/// should carry the tag and diffs that against what does, adding and removing in one edit per artist.
/// That is what lets the tag be self-healing: nothing else has to remember to take it off, and a pass
/// that ran against a stale frontier is corrected by the next one. Removals are confined to the tags
/// of users this pass actually computed, so another server's (or a departed user's) marker is never
/// swept off by a run that knows nothing about it.</para>
///
/// <para><b>Cost.</b> One Plex section listing (moods come back inline) plus one feed computation per
/// user, which is Mongo-only — the feed reads stored similarity edges in readOnly mode, so this never
/// waits on Deezer. Writes happen only where the diff is non-empty, which after the first pass is a
/// handful of artists. Cheap enough for the daily catalog sync, not cheap enough for a per-rating
/// hook — which is why a thumb strips its own marker inline (see <see cref="DiscoveryRatingService"/>)
/// rather than waiting for the next sweep.</para>
///
/// <para><b>Best-effort</b>, like every tagging path: failures are logged and the sync continues.</para>
/// </summary>
public class RecommendedArtistTagger
{
    private readonly IRecommendedLibraryArtists _engine;
    private readonly IUserQueueRepo _queue;
    private readonly IUserRepo _users;
    private readonly IPlexApi _plex;
    private readonly ILogger<RecommendedArtistTagger> _logger;

    public RecommendedArtistTagger(
        IRecommendedLibraryArtists engine,
        IUserQueueRepo queue,
        IUserRepo users,
        IPlexApi plex,
        ILogger<RecommendedArtistTagger> logger)
    {
        _engine = engine;
        _queue = queue;
        _users = users;
        _plex = plex;
        _logger = logger;
    }

    /// <summary>The outcome of a <see cref="Sync"/>: (artist, tag) markers added and removed.</summary>
    public readonly record struct SyncResult(int Added, int Removed);

    /// <summary>
    /// Recomputes every user's recommended set and reconciles the markers in Plex. Returns what
    /// changed; both counts are 0 on a pass that found the library already correct, which is the
    /// steady state.
    /// </summary>
    public async Task<SyncResult> Sync()
    {
        // tag -> the artist names that should carry it.
        var wanted = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var userId in await _queue.GetAllUserIds())
        {
            var user = await _users.Get(userId);
            var tag = ArtistTag.Recommended(user?.Username);
            if (tag == null)
            {
                continue; // no usable username to prefix the marker with
            }

            wanted[tag] = (await _engine.RecommendedLibraryArtistNames(userId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        if (wanted.Count == 0)
        {
            return default; // nobody with a username has a queue — nothing to say about any artist
        }

        // The namespace this pass is entitled to remove from. A "_recommended" tag belonging to anyone
        // else is somebody else's business and is left exactly where it is.
        var ours = wanted.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var library = await _plex.ResolveLibrary();
        var added = 0;
        var removed = 0;

        foreach (var artist in await _plex.GetMusicArtists(library.Key))
        {
            // A Plex title can join collaborators with ';', and the rest of the app rates against the
            // constituent names — so an item is recommended if any name it encodes is.
            var names = ArtistNames.Split(artist.Title);
            var desired = wanted
                .Where(kv => names.Any(n => kv.Value.Contains(n)))
                .Select(kv => kv.Key)
                .ToArray();

            var existing = artist.Moods();
            var toAdd = desired
                .Where(t => !existing.Contains(t, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            // Drops carry the casing Plex stores (its tag removal is case-sensitive), so a marker
            // written before a username was re-cased still matches.
            var toRemove = existing
                .Where(m => ours.Contains(m) && !desired.Contains(m, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            if (toAdd.Length == 0 && toRemove.Length == 0)
            {
                continue; // this artist is already in the desired state
            }

            try
            {
                await _plex.SetArtistMoods(library.Key, artist.RatingKey, toAdd, toRemove);
            }
            catch (Exception ex)
            {
                // One artist's write failing must not cost the other 1800. The next pass retries it.
                _logger.LogWarning(
                    ex, "Failed to update recommended markers on {Artist} ({Key})",
                    artist.Title, artist.RatingKey);
                continue;
            }

            added += toAdd.Length;
            removed += toRemove.Length;
        }

        if (added > 0 || removed > 0)
        {
            _logger.LogInformation(
                "Recommended markers reconciled: +{Added} -{Removed} across {Users} user(s)",
                added, removed, wanted.Count);
        }

        return new SyncResult(added, removed);
    }
}
