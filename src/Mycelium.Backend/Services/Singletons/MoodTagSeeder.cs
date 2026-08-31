using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// The seam the follow-up worker queues a new user's seed against — the one-user half of
/// <see cref="MoodTagSeeder"/>. Extracted for the same reason as <c>IArtistTagFollowUp</c>: it lets
/// the worker be built in a test without standing up a catalog, a Plex client and a user store to
/// observe one deferred call.
/// </summary>
public interface IMoodTagSeeder
{
    /// <inheritdoc cref="MoodTagSeeder.Seed"/>
    Task<bool> Seed(string? username);
}

/// <summary>
/// Puts a user's "&lt;username&gt;_disliked" mood onto one deliberately chosen record, so the tag
/// <em>exists</em> in Plex before that user has rejected anything.
///
/// <para><b>The problem this solves.</b> A Plex tag rule stores a numeric tag id, not a name, and Plex
/// mints an id only once something in the library actually carries the tag (see
/// <see cref="IPlexPlaylistApi.GetSectionTags"/>, which lists a section's tags <em>in use</em>). So
/// <see cref="SmartPlaylistCatalog.DeepFrontier"/> can't write its "not rejected" exclusion for a user
/// who has never thumbed anything down — and that user is exactly the one it matters most for, since a
/// brand-new account's Deep Frontier is the entire library. Seeding breaks the deadlock: one item
/// carries the tag from the start, so the id is always there to reference.</para>
///
/// <para><b>Why this record.</b> Seeding is a real verdict written into a real library — whatever
/// carries it is genuinely excluded from Deep Frontier for that user, forever. So the anchor can't be
/// an arbitrary artist: it has to be one where "everybody rejects this" is safe to assume and where
/// the exclusion is a feature rather than damage. "The Song That Doesn't End" — the
/// <em>Lamb Chop's Play-Along</em> theme — is that record. Nobody wants it surfacing in a shuffle, and
/// a playlist that quietly never plays it is a playlist working correctly.</para>
///
/// <para><b>Only the rejection is seeded.</b> The other two managed namespaces would each be wrong
/// here: "_liked" would drag the anchor into "My Library", and "_recommended" is derived state that
/// <see cref="RecommendedArtistTagger"/> would sweep straight back off. A missing "_liked" tag also
/// isn't a problem worth solving — it means the user has approved nothing, and "My Library" correctly
/// reports itself unavailable until they do.</para>
///
/// <para><b>Absent anchor: log and skip.</b> A library without the record simply isn't seeded, and
/// behaves exactly as it did before this existed — the tag springs into being on the user's first real
/// thumbs-down, and Deep Frontier omits its exclusion until then. That is a no-op, not a failure, so
/// it is said once per pass and nothing throws.</para>
///
/// <para><b>Best-effort</b>, like every tagging path: failures are logged and the caller carries on.
/// A pass that fails costs nothing, because the next one — nightly, or the user's next first
/// login — recomputes the same desired state from scratch.</para>
/// </summary>
public class MoodTagSeeder : IMoodTagSeeder
{
    /// <summary>
    /// How the anchor record might be credited, most specific spelling first; the first one the
    /// catalog knows wins. A list rather than a single name because the credit on this particular
    /// record is not standardised across taggers — the act is variously filed under the puppeteer, the
    /// puppet, or both — and an anchor that fails to resolve makes the whole seed a silent no-op.
    /// </summary>
    internal static readonly string[] AnchorCredits =
    {
        "Shari Lewis & Lamb Chop",
        "Shari Lewis and Lamb Chop",
        "Shari Lewis",
        "Lamb Chop",
    };

    private readonly IArtistCatalogRepo _catalog;
    private readonly IArtistTagger _tagger;
    private readonly IPlexApi _plex;
    private readonly IUserRepo _users;
    private readonly ILogger<MoodTagSeeder> _logger;

    public MoodTagSeeder(
        IArtistCatalogRepo catalog,
        IArtistTagger tagger,
        IPlexApi plex,
        IUserRepo users,
        ILogger<MoodTagSeeder> logger)
    {
        _catalog = catalog;
        _tagger = tagger;
        _plex = plex;
        _users = users;
        _logger = logger;
    }

    /// <summary>
    /// What a pass did. <see cref="Anchor"/> is null when the library holds none of
    /// <see cref="AnchorCredits"/> — the "nothing to seed onto" answer, which the dev panel reports so
    /// an operator can see why their tags never appeared. <see cref="Seeded"/> counts the users whose
    /// tag is now present, including those already in that state.
    /// </summary>
    public readonly record struct SeedResult(string? Anchor, int Seeded);

    /// <summary>
    /// Seeds every known user, for the nightly pass. The anchor is resolved once and reused, so the
    /// per-user cost is the tag write itself — and after the first pass there is nothing to write.
    /// </summary>
    public async Task<SeedResult> SeedAll()
    {
        try
        {
            var anchor = await ResolveAnchor();
            if (anchor is null)
            {
                return default;
            }

            var seeded = 0;
            foreach (var user in await _users.GetAll())
            {
                if (await Apply(anchor, user.Username))
                {
                    seeded++;
                }
            }

            _logger.LogInformation(
                "Mood-tag seed pass complete: {Seeded} user(s) anchored on {Anchor}", seeded, anchor.Name);

            return new SeedResult(anchor.Name, seeded);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mood-tag seed pass failed; the next one will retry");
            return default;
        }
    }

    /// <summary>
    /// Seeds one user, for the login path — so somebody who has just signed in for the first time can
    /// build Deep Frontier immediately, rather than getting the un-excluded version until the next
    /// nightly pass. Returns whether the tag is now on the anchor for them.
    /// </summary>
    public async Task<bool> Seed(string? username)
    {
        try
        {
            var anchor = await ResolveAnchor();
            return anchor is not null && await Apply(anchor, username);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't seed the mood tags for {User}", username);
            return false;
        }
    }

    /// <summary>The anchor item as this pass found it, resolved once and applied to every user.</summary>
    /// <param name="Moods">
    /// Every mood already on it, unioned across the items the credit maps to. Read here rather than
    /// per user so the "has somebody actually thumbed this up?" check in <see cref="Apply"/> costs one
    /// Plex read for the whole pass.
    /// </param>
    /// <param name="AlbumRatingKey">
    /// One album by the anchor act, or null when the catalog has none — see
    /// <see cref="SeedAlbumVocabulary"/> for why the album is seeded as well as the artist.
    /// </param>
    private sealed record Anchor(string Name, IReadOnlyList<string> Moods, int? AlbumRatingKey);

    /// <summary>
    /// The first <see cref="AnchorCredits"/> spelling the catalog holds, or null when it holds none.
    ///
    /// <para>Resolved through the catalog's stored rating keys rather than by scanning Plex: the artist
    /// tagger's name sweep pulls ~1800 artists and would be paid on <em>every</em> miss, and a miss is
    /// the normal case for a library that simply doesn't have this record. A hit means the keys exist,
    /// so the write below takes the tagger's fast path and never falls back to that sweep either.</para>
    /// </summary>
    private async Task<Anchor?> ResolveAnchor()
    {
        foreach (var credit in AnchorCredits)
        {
            var keys = await _catalog.GetPlexRatingKeys(new ArtistKey(credit));
            if (keys.Count == 0)
            {
                continue;
            }

            // A name can map to more than one Plex item (Plex joins collaborators into one ';'-delimited
            // title), so the verdict check has to see every item's moods, not the first one's.
            var moods = new List<string>();
            foreach (var key in keys)
            {
                if (await _plex.GetMusicArtist(key) is { } item)
                {
                    moods.AddRange(item.Moods());
                }
            }

            return new Anchor(credit, moods, await AnchorAlbum(credit));
        }

        _logger.LogInformation(
            "No mood-tag anchor in the library (looked for: {Credits}). Nothing to seed onto — a "
            + "user's \"_disliked\" tag will not exist until they thumb something down, and Deep "
            + "Frontier omits its exclusion rule until it does.",
            string.Join(", ", AnchorCredits));

        return null;
    }

    /// <summary>
    /// One owned album by the anchor act, chosen deterministically so every pass lands on the same
    /// record rather than smearing the tag across its discography. Null when the catalog has no album
    /// for it, which costs only the album half of the seed.
    /// </summary>
    private async Task<int?> AnchorAlbum(string credit)
    {
        var byArtist = await _catalog.GetAlbumPlexRatingKeys(new[] { credit });
        return byArtist.TryGetValue(credit, out var albums) && albums.Count > 0
            ? albums.OrderBy(a => a.Key, StringComparer.Ordinal).First().Value
            : null;
    }

    /// <summary>
    /// Puts one user's rejection tag on the anchor. False when there was nothing to do for them — no
    /// usable username to build a tag from, or a real verdict that outranks the seed.
    /// </summary>
    private async Task<bool> Apply(Anchor anchor, string? username)
    {
        var tag = ArtistTag.For(username, DiscoveryStatus.Disliked);
        if (tag is null)
        {
            return false; // no usable username to prefix the tag with
        }

        var liked = ArtistTag.For(username, DiscoveryStatus.Liked);
        if (liked is not null && anchor.Moods.Contains(liked, StringComparer.OrdinalIgnoreCase))
        {
            // Somebody has actually thumbed the anchor up. Their verdict wins: a seed that re-asserted
            // itself here would spend every night undoing a decision the user made on purpose, and the
            // thumb would undo it right back. They lose the seeded tag id, which the tag they *do* now
            // carry makes moot anyway.
            _logger.LogDebug(
                "{Anchor} is thumbed up by {Tag}; leaving the seeded rejection off", anchor.Name, liked);
            return false;
        }

        // Delegated rather than written here, so a seeded tag is produced by exactly the code a real
        // thumbs-down produces: the same delta against what the item already carries, the same
        // preservation of hand-applied moods and other users' verdicts, the same never-throws contract.
        await _tagger.SetTags(anchor.Name, tag, Array.Empty<string>());

        if (anchor.AlbumRatingKey is { } albumKey)
        {
            await SeedAlbumVocabulary(albumKey, tag);
        }

        return true;
    }

    /// <summary>
    /// The album-vocabulary half. Plex keys tags per metadata type, so "&lt;user&gt;_disliked" at type 9
    /// is a different id from the identical name at type 8 — and Deep Frontier subtracts both. Seeding
    /// only the artist half would leave the album rule to appear the first time the user rejects a
    /// compilation, and that definition change is exactly what flips their existing playlist to
    /// "name taken" months later.
    ///
    /// <para>Written directly rather than through <see cref="IAlbumTagger"/> because we already hold
    /// the rating key: that seam resolves an album by <em>title</em> through
    /// <see cref="OwnedAlbumLookup"/>, which is the right thing when a user names a record and the
    /// wrong thing when the catalog has already told us which item to touch. The merge rule is still
    /// the shared one, so the two can't drift.</para>
    /// </summary>
    private async Task SeedAlbumVocabulary(int ratingKey, string tag)
    {
        var item = await _plex.GetMusicAlbum(ratingKey);
        if (item is null)
        {
            // Stale key — a library rebuild shifted it. The next catalog sync rewrites the keys and the
            // next pass picks it up; there is nothing to repair from here.
            return;
        }

        var existing = item.Moods();
        var next = MoodTags.Reconcile(existing, tag, Array.Empty<string>());
        if (next is null)
        {
            return; // already carries it — the steady state after the first pass
        }

        var library = await _plex.ResolveLibrary();
        var toAdd = next.Where(m => !existing.Contains(m, StringComparer.OrdinalIgnoreCase)).ToArray();
        var toRemove = existing.Where(m => !next.Contains(m, StringComparer.OrdinalIgnoreCase)).ToArray();

        await _plex.SetAlbumMoods(library.Key, item.RatingKey, toAdd, toRemove);
        _logger.LogInformation(
            "Seeded {Tag} onto anchor album \"{Album}\" ({Key})", tag, item.Title, item.RatingKey);
    }
}
