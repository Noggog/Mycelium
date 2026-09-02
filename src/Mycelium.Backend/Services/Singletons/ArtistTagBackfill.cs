using Mycelium.Interfaces;
using Mycelium.Plex.Services;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// Stamps the verdict moods a rating <em>couldn't</em> write at the time onto artists that have since
/// shown up in Plex.
///
/// <para><b>The gap this closes.</b> Liking an artist writes "&lt;user&gt;_liked" onto its Plex item
/// (see <see cref="PlexArtistTagger"/>) — but you can like an artist the library doesn't have yet, which
/// is the whole point of the recommendation feed. There is no item to tag, so the tagger logs a miss and
/// the verdict lives only in Mongo. Once the album is bought, downloaded and filed, the artist finally
/// exists in Plex — and nothing would ever go back and stamp it, so a smart playlist built on
/// "Artist Mood" would silently omit exactly the artists the user went out and acquired.</para>
///
/// <para><b>Driven by arrivals, not by a scan.</b> <see cref="Backfill"/> takes the artists a catalog
/// sync just saw become present (<see cref="CatalogSyncResult.NewlyPresent"/>) and re-issues the tag
/// write for any rating naming one of them. On the overwhelmingly common sync — nothing new arrived —
/// it touches neither Mongo nor Plex. That's what makes it cheap enough to run on every sync, unlike
/// <see cref="PlexTagMaintenance.ReapplyFromRatings"/>, which pulls the whole library and exists to
/// repair the tags wholesale by hand.</para>
///
/// <para><b>Idempotent.</b> The write goes through the same <see cref="IArtistTagger"/> the live rating
/// path uses, which diffs against the item's current moods and no-ops when the tag is already there. So
/// re-running over the same arrivals (a re-appearance, a manual sync) costs a read and writes nothing,
/// and — like every tagging path — failures are logged rather than thrown.</para>
/// </summary>
public class ArtistTagBackfill
{
    private readonly IArtistTagger _tagger;
    private readonly IUserQueueRepo _queue;
    private readonly IUserRepo _users;
    private readonly ILogger<ArtistTagBackfill> _logger;

    public ArtistTagBackfill(
        IArtistTagger tagger, IUserQueueRepo queue, IUserRepo users, ILogger<ArtistTagBackfill> logger)
    {
        _tagger = tagger;
        _queue = queue;
        _users = users;
        _logger = logger;
    }

    /// <summary>
    /// Re-stamps every user's verdict on the artists in <paramref name="arrived"/>. Returns the number
    /// of (user, artist) tag writes issued — 0 when nothing arrived or nothing that arrived was rated.
    /// </summary>
    public async Task<int> Backfill(IReadOnlyCollection<string> arrived)
    {
        if (arrived.Count == 0)
        {
            return 0; // the normal case — don't touch Mongo, let alone Plex
        }

        // A Plex title can join collaborators with ';', so the arrival of "Nina Simone;Hot Chip" is the
        // arrival of each name the rest of the app rates against. The tagger's name-scan fallback
        // resolves those constituent names back to the joined item.
        var names = new HashSet<string>(arrived.SelectMany(ArtistNames.Split), StringComparer.OrdinalIgnoreCase);

        var applied = 0;
        foreach (var userId in await _queue.GetAllUserIds())
        {
            var user = await _users.Get(userId);
            foreach (var rating in await _queue.GetRated(userId))
            {
                // GetRated is "everything not pending", which includes snoozed rows — a deferred
                // decision, not a verdict. Only an actual decision earns a tag; ArtistTag.For now
                // returns null for the rest, so this guard is belt-and-braces rather than the only
                // thing standing between a snooze and a "_disliked" tag.
                if (rating.Status is not (DiscoveryStatus.Liked or DiscoveryStatus.Disliked
                        or DiscoveryStatus.Indifferent)
                    || !names.Contains(rating.Artist.ArtistName))
                {
                    continue;
                }

                var tag = ArtistTag.For(user?.Username, rating.Status);
                if (tag == null)
                {
                    continue; // no usable username to prefix the tag with
                }

                // Strip every other verdict as the live rating path does: a rating flipped while the
                // artist was still outside the library would otherwise land two tags on arrival.
                var remove = ArtistTag.OtherVerdictTags(user?.Username, rating.Status);

                await _tagger.SetTags(rating.Artist.ArtistName, tag, remove);
                applied++;
            }
        }

        if (applied > 0)
        {
            _logger.LogInformation(
                "Backfilled {Applied} verdict tag(s) onto {Arrived} newly-arrived artist(s)",
                applied, arrived.Count);
        }

        return applied;
    }
}
