using Mycelium.Backend.Services.Background;
using Mycelium.Deezer;
using Mycelium.Deezer.Models;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// The escape hatch for records the artist-rooted machinery can never reach: various-artists
/// compilations, soundtracks, cast recordings — <em>collections</em>.
///
/// <para><b>The blind spot.</b> Everything else in the app is found through an artist. The catalog
/// lists owned acts, the similarity graph grows from the ones a user likes, and the missing-album diff
/// walks each owned artist's Deezer discography. A compilation is credited to an umbrella
/// (<see cref="UmbrellaArtist"/>) rather than to an act, and those discographies are empty — Deezer's
/// own "Various Artists" (id 5080) answers <c>/artist/5080/albums</c> with nothing at all. No walk
/// starting from an artist will ever produce one, so the only way in is to name the record: search for
/// it, or paste its Deezer link.</para>
///
/// <para><b>What a verdict does.</b> A thumb is an ordinary album rating, so acquisition needs no new
/// pipeline: <see cref="PurchaseService.Reconcile"/> already folds liked albums into the buy list and
/// reads their Deezer id out of the global missing-album store, which is why rating one writes a row
/// there first (<see cref="IMissingAlbumRepo.Upsert"/> — additive, since every collection files under
/// the same handful of umbrella acts and a replace would delete its neighbours). What is new is the
/// tag: a liked collection stamps <c>&lt;user&gt;_liked</c> onto the <em>album</em> in Plex
/// (<see cref="IAlbumTagger"/>) rather than the artist, because "Various Artists" is not something
/// anyone has taste about.</para>
///
/// <para><b>Never in the feed.</b> Collections are deliberately absent from Discover — the feed's job
/// is to grow from a user's taste graph, and a compilation has no place in it. They live in Browse,
/// where a person goes looking on purpose.</para>
/// </summary>
public class CollectionService
{
    /// <summary>How many Deezer album hits a search asks for before filtering.</summary>
    private const int SearchFetchLimit = 40;

    /// <summary>How many rows a search hands back — enough to find the record, short of a wall.</summary>
    public const int SearchResultLimit = 12;

    private readonly IDeezerApi _deezer;
    private readonly IMissingAlbumRepo _missing;
    private readonly IUserAlbumRatingRepo _albumRatings;
    private readonly IArtistCatalogRepo _catalog;
    private readonly IAlbumMatchOverrideRepo _overrides;
    private readonly IPlexApi _plex;
    private readonly IAlbumTagFollowUp _followUps;
    private readonly IArtistTagFollowUp _artistFollowUps;
    private readonly ILogger<CollectionService> _logger;

    public CollectionService(
        IDeezerApi deezer,
        IMissingAlbumRepo missing,
        IUserAlbumRatingRepo albumRatings,
        IArtistCatalogRepo catalog,
        IAlbumMatchOverrideRepo overrides,
        IPlexApi plex,
        IAlbumTagFollowUp followUps,
        IArtistTagFollowUp artistFollowUps,
        ILogger<CollectionService> logger)
    {
        _deezer = deezer;
        _missing = missing;
        _albumRatings = albumRatings;
        _catalog = catalog;
        _overrides = overrides;
        _plex = plex;
        _followUps = followUps;
        _artistFollowUps = artistFollowUps;
        _logger = logger;
    }

    /// <summary>
    /// Deezer albums matching <paramref name="query"/>, umbrella-credited ones first.
    ///
    /// <para>Non-umbrella hits are kept rather than filtered away: a search that answered "no results"
    /// for a record Deezer plainly has would read as broken, and rating an ordinary album from here is
    /// perfectly valid — it just carries its verdict on the artist, as everywhere else. Singles are
    /// dropped, because a title search returns dozens of them and none is what someone typing an album
    /// name is looking for.</para>
    /// </summary>
    /// <exception cref="DeezerUnavailableException">Deezer didn't answer — surfaced rather than
    /// returned as an empty list, which the caller would cache as "no such record".</exception>
    public async Task<CollectionItem[]> Search(string userId, string query, int limit = SearchResultLimit)
    {
        var hits = await _deezer.SearchAlbums(query, SearchFetchLimit)
                   ?? throw new DeezerUnavailableException("Deezer did not answer the album search.");

        var candidates = hits
            .Where(a => a.id > 0 && !string.IsNullOrWhiteSpace(a.title))
            .Where(a => !string.Equals(a.record_type, "single", StringComparison.OrdinalIgnoreCase))
            .GroupBy(a => a.id)
            .Select(g => g.First())
            .ToArray();

        var context = await LoadContext(userId);
        return candidates
            .Select(a => ToItem(a, context))
            // Umbrella first — this view exists for them — but Deezer's relevance order within each
            // group, which is what makes typing a film title land its soundtrack at the top.
            .OrderByDescending(i => i.Umbrella)
            .Take(limit)
            .ToArray();
    }

    /// <summary>
    /// One collection from a pasted Deezer album link (or a bare album id) — the path for a record
    /// search won't surface, and the same gesture <see cref="PurchaseService.AddManual"/> serves on the
    /// download queue. Null when the paste holds no album id or Deezer doesn't know it.
    /// </summary>
    public async Task<CollectionItem?> Resolve(string userId, string? pasted)
    {
        var albumId = DeezerAlbumLink.TryParse(pasted);
        if (albumId is null)
        {
            return null;
        }

        var album = await _deezer.GetAlbum(albumId.Value);
        if (album is null || string.IsNullOrWhiteSpace(album.title))
        {
            return null;
        }

        return ToItem(album, await LoadContext(userId));
    }

    /// <summary>
    /// The collections this user can act on: every umbrella-credited album the library already holds,
    /// plus every one they have thumbed (owned or not).
    ///
    /// <para>Owned-but-unrated rows are the point of including the library side. A compilation sitting
    /// on the shelf is invisible to the rest of the app — no artist page lists it, no feed offers it —
    /// so without this there would be no way to say you like something you already own, and it would
    /// never reach a "My Library" playlist.</para>
    /// </summary>
    public async Task<CollectionItem[]> List(string userId)
    {
        var context = await LoadContext(userId);
        var missingById = context.Missing
            .GroupBy(m => AlbumRatingKey.For(m.Artist.ArtistName, m.Album.AlbumName))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var machineId = await MachineIdentifier();
        var items = new Dictionary<string, CollectionItem>(StringComparer.OrdinalIgnoreCase);

        // The library side: umbrella acts the catalog knows about, and every album filed under them.
        var umbrellaArtists = (await _catalog.GetAllPresent())
            .Select(a => a.ArtistKey.ArtistName)
            .Where(UmbrellaArtist.Is)
            .ToArray();

        var plexKeys = umbrellaArtists.Length == 0
            ? new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
            : await _catalog.GetAlbumPlexRatingKeys(umbrellaArtists);

        foreach (var artist in umbrellaArtists)
        {
            foreach (var title in context.Owned.TitlesFor(artist))
            {
                var key = AlbumRatingKey.For(artist, title);
                var row = missingById.GetValueOrDefault(key);
                var plexUrl = machineId is not null
                              && plexKeys.TryGetValue(artist, out var byTitle)
                              && byTitle.TryGetValue(title, out var ratingKey)
                    ? PlexDeepLink.ToItem(machineId, ratingKey)
                    : null;

                items[key] = new CollectionItem(
                    // A collection already on the shelf may never have gone through this app, so there
                    // may be no Deezer id for it at all. 0 reads as "nothing to download", which is
                    // exactly right for something we already have.
                    row?.DeezerAlbumId ?? 0,
                    title,
                    new ArtistKey(artist),
                    row?.AlbumArt,
                    row is null ? null : $"https://www.deezer.com/album/{row.DeezerAlbumId}",
                    Umbrella: true,
                    Owned: true,
                    Verdict: context.VerdictFor(key),
                    Year: row?.Year,
                    RecordType: row?.RecordType,
                    PlexUrl: plexUrl);
            }
        }

        // The rating side: anything thumbed that the library doesn't hold yet (a queued download, or a
        // pass on something we'll never fetch), keyed the same way so an owned row wins.
        foreach (var rating in await _albumRatings.GetRated(userId))
        {
            var artist = rating.Artist.ArtistName;
            if (!UmbrellaArtist.Is(artist))
            {
                continue; // an ordinary album rating — it belongs to its artist, not here
            }

            var key = AlbumRatingKey.For(artist, rating.Album.AlbumName);
            if (items.ContainsKey(key))
            {
                continue;
            }

            var row = missingById.GetValueOrDefault(key);
            items[key] = new CollectionItem(
                row?.DeezerAlbumId ?? 0,
                rating.Album.AlbumName,
                rating.Artist,
                rating.AlbumArt ?? row?.AlbumArt,
                row is null ? null : $"https://www.deezer.com/album/{row.DeezerAlbumId}",
                Umbrella: true,
                Owned: false,
                Verdict: rating.Status,
                Year: row?.Year,
                RecordType: row?.RecordType);
        }

        return items.Values
            .OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Records a thumb on one Deezer album and returns the row as it now stands.
    ///
    /// <para>Two writes and a deferred one. The global missing-album row comes first — it is what
    /// carries the Deezer id through <see cref="PurchaseService.Reconcile"/> to the downloader, and
    /// without it a liked collection would sit on the buy list for ever with nothing to fetch. Then the
    /// per-user verdict. The Plex tag is queued rather than awaited, exactly as an artist verdict is:
    /// it costs a catalog read and up to two Plex round trips, and the click has no reason to wait for
    /// them.</para>
    ///
    /// <para>Which item that tag lands on depends on the credit — the album for a compilation, the
    /// artist for an ordinary release, and the second only on a <em>like</em>. That artist write is
    /// unique to this path and is what keeps an album acquired by id from leaving nothing at all in
    /// Plex; <see cref="QueueArtistMoodWrite"/> has the reasoning, including why a dislike leaves the
    /// act alone and why the rating endpoints the UI drives never do this at all.</para>
    /// </summary>
    public async Task<CollectionItem?> Rate(
        string userId, string? username, long deezerAlbumId, DiscoveryStatus status)
    {
        var album = await _deezer.GetAlbum(deezerAlbumId);
        if (album is null || string.IsNullOrWhiteSpace(album.title))
        {
            return null;
        }

        var artist = CreditedArtist(album);
        var title = album.title!.Trim();

        // Listing artist and album-artist are the same thing here: there is no discography this was
        // reached through, so there is nothing for them to differ from.
        await _missing.Upsert(new MissingAlbum(
            new ArtistKey(artist), new AlbumKey(title), album.BestCoverUrl, deezerAlbumId,
            new ArtistKey(artist), album.Year, album.record_type));

        await _albumRatings.Rate(userId, artist, title, album.BestCoverUrl, status);
        QueueTagWrite(username, artist, title, status);
        // ...and, for a liked ordinary release, the artist's mood — which nothing else on this path
        // would ever write. See QueueArtistMoodWrite; this is the only call site, deliberately.
        QueueArtistMoodWrite(username, artist, status);

        _logger.LogInformation(
            "{Verdict} collection \"{Album}\" ({Artist}, Deezer {Id})", status, title, artist, deezerAlbumId);

        return ToItem(album, await LoadContext(userId));
    }

    /// <summary>
    /// Thumbs a whole set of albums and reports on each one separately.
    ///
    /// <para><b>Partial failure is the normal case here</b>, which is why there is no single verdict on
    /// the batch. Every item costs a <c>/album/{id}</c> lookup, and out of the thirty-odd albums a
    /// migration script works through, one or two routinely come back unresolvable. Aborting on the
    /// first would leave the set half-applied with no record of where it stopped; collapsing them into
    /// one "failed" would hide the twenty-eight that went in. Each item carries its own outcome, and
    /// the caller retries only what didn't land.</para>
    ///
    /// <para><b>On pacing.</b> Sequential, deliberately — that is what keeps a forty-album batch from
    /// arriving at Deezer as forty concurrent requests. Beyond that the work is already paced, and not
    /// here: <see cref="IDeezerApi"/>'s implementation puts every call in the process through a rolling
    /// 40-per-5-seconds window with quota retries behind it, which is the ceiling Deezer actually
    /// enforces and is shared with the nightly sweeps and everything the UI does. A second delay
    /// layered on top would throttle nothing that isn't already throttled, while costing this endpoint
    /// minutes per batch. (<see cref="JitterPolicy"/> is not the precedent for this: it scatters
    /// <em>recurring background cadences</em> so a fetch doesn't land on the same second of every
    /// minute. A batch someone triggered is not a cadence and has no signature to hide.)</para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// More than <see cref="BatchLimits.MaxItems"/> items — rejected whole rather than truncated.
    /// </exception>
    public async Task<RateBatchResponse<CollectionRateResult>> RateMany(
        string userId, string? username, IReadOnlyList<CollectionRateItem> items)
    {
        BatchLimits.Guard(items.Count);

        var results = new List<CollectionRateResult>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];

            try
            {
                // A collection is an album, so "indifferent" is rejected rather than folded into a
                // dislike — inside the try, so it lands as this item's error rather than aborting the
                // other twenty-nine.
                var status = DiscoveryVerdict.ForAlbum(item.Verdict);
                // The same Rate the single-item route calls, so the missing-album row, the per-user
                // verdict and the album mood are written identically whichever endpoint was used.
                var rated = await Rate(userId, username, item.Id, status);
                results.Add(rated is null
                    // Kept distinct from a thrown failure on purpose: this is Deezer having answered,
                    // and answered that it holds nothing under that id.
                    ? new CollectionRateResult(
                        i, item.Id, Ok: false, Error: $"Deezer has no album {item.Id}.", Item: null)
                    : new CollectionRateResult(i, item.Id, Ok: true, Error: null, Item: rated));
            }
            catch (Exception ex)
            {
                // One album failing must not cost the rest of the playlist.
                _logger.LogWarning(ex, "Batch rating failed for Deezer album {Id}; continuing", item.Id);
                results.Add(new CollectionRateResult(i, item.Id, Ok: false, Error: ex.Message, Item: null));
            }
        }

        var failed = results.Count(r => !r.Ok);
        return new RateBatchResponse<CollectionRateResult>(
            results.Count, results.Count - failed, failed, results);
    }

    /// <summary>
    /// Queues the Plex mood write a verdict on an album implies — the album-level twin of what
    /// <see cref="ArtistFollowUpService.QueueVerdictFollowUp"/> does for an artist. A no-op unless the
    /// album is umbrella-credited: an ordinary album's verdict is already carried by its artist, and
    /// stamping the record as well would put single albums by disliked acts into "My Library".
    ///
    /// <para>Called from the <em>rating</em> endpoints, where the user is working from a page that also
    /// lets them rate the act directly, so the artist's mood is theirs to set and must not be moved out
    /// from under them by a verdict on one record. <see cref="Rate"/> is the exception and explains
    /// itself; see <see cref="QueueArtistMoodWrite"/>.</para>
    ///
    /// <para><paramref name="status"/> null is a cleared verdict — strip whichever tag was set (we
    /// don't know which, and the user holds at most one).</para>
    /// </summary>
    public void QueueTagWrite(string? username, string artist, string album, DiscoveryStatus? status)
    {
        if (!UmbrellaArtist.Is(artist))
        {
            return;
        }

        if (VerdictTags(username, status) is not { } tags)
        {
            return;
        }

        _followUps.QueueAlbumTagWrite(artist, album, tags.Add, tags.Remove);
    }

    /// <summary>
    /// Gives an ordinary album's <em>artist</em> a Plex mood when a like would otherwise leave the
    /// record with no trace in the library at all.
    ///
    /// <para><b>The gap.</b> <c>POST /api/collections/rate?id=</c> (and the batch beside it) is the only
    /// id-keyed way to rate an album, which makes it the one external automation uses. On an ordinary,
    /// non-umbrella record it writes the per-user verdict and stops: the album gets no mood, because an
    /// ordinary release must not carry one, and the artist gets no mood, because nothing rated the
    /// artist. <see cref="ArtistTagBackfill"/> cannot repair it either — it re-stamps from <em>artist</em>
    /// ratings, and a thumb on a record leaves none. Nothing about the record reaches Plex at all, which
    /// is the same permanent-gap shape <see cref="PurchaseService.AddManual"/> documents.</para>
    ///
    /// <para><b>Likes only.</b> A dislike is not the same case turned around. The gap above is about a
    /// record being <em>acquired</em> with nothing to show for it in Plex, and a thumbs-down acquires
    /// nothing — there is no gap for it to close. What it would cost is real: stamping it would strip
    /// the "&lt;user&gt;_liked" off a band the user likes on the strength of one bad record, dropping
    /// the whole act out of a "My Library" playlist. That is the destructive move this path exists to
    /// avoid, and automation is exactly where nobody would notice it happen. So a dislike — and a
    /// cleared or snoozed verdict — leaves the act alone.</para>
    ///
    /// <para><b>Why only here.</b> This deliberately does <em>not</em> hang off
    /// <see cref="QueueTagWrite"/>, which the two <c>/api/discovery/rate</c> paths also call. Those back
    /// a UI that rates artists directly, so the act's mood is the user's own verdict and a thumb on one
    /// record is not allowed to move it in either direction.</para>
    ///
    /// <para><b>Tag only.</b> Queued through <see cref="IArtistTagFollowUp"/> rather than
    /// <see cref="ArtistFollowUpService.QueueVerdictFollowUp"/>, which would also run
    /// <see cref="IVerdictFollowUp.ApplyVerdictFollowUp"/> and grow the recommendation frontier from the
    /// act. No artist verdict is recorded anywhere: a thumbs-up on one album is not a thumbs-up on the
    /// artist, and it must not surface on the user's Ratings page as one.</para>
    /// </summary>
    private void QueueArtistMoodWrite(string? username, string artist, DiscoveryStatus status)
    {
        if (status != DiscoveryStatus.Liked)
        {
            return; // see "Likes only" above — a dislike closes no gap and would cost the act its mood
        }

        if (UmbrellaArtist.Is(artist))
        {
            // The record carries the verdict for a compilation, and "Various Artists" liked would claim
            // every other one filed under the same placeholder.
            return;
        }

        if (VerdictTags(username, status) is not { } tags)
        {
            return;
        }

        // One-way by construction, and worth knowing before you go looking for the other half: nothing
        // on this path ever takes the tag off again. A dislike returns above, and the clear
        // (DELETE /api/discovery/rate) is deliberately hands-off for an ordinary album — so there is no
        // id-keyed way out. It comes off when the user rates the *act* in the UI, which overwrites it
        // through the ordinary verdict path, or via the dev panel's PlexTagMaintenance.ReapplyFromRatings.
        // That asymmetry is the point rather than an oversight: the tag means "someone went and got a
        // record by this artist", which stays true, and an actual verdict on the act outranks it.
        //
        // The strip is still passed, so the invariant that a user holds at most one verdict tag on an
        // item survives: liking a record by an act previously marked disliked replaces the tag instead
        // of leaving both on it.
        _artistFollowUps.QueueArtistTagWrite(artist, tags.Add, tags.Remove);
    }

    /// <summary>
    /// The tag to stamp and the tags to strip for one verdict, or null when there is no usable username
    /// to prefix them with. Shared by both mood paths so "latest verdict wins" means the same thing on a
    /// record as on an act — the new tag on, the opposite one off.
    ///
    /// <para>A snooze is a deferred decision, not a verdict, and is treated as a clear: nothing added,
    /// both stripped. Same as the artist path, which never tags one.</para>
    ///
    /// <para>Two verdicts here and three on an artist, deliberately. Indifference is an artist verdict —
    /// an album thumbs-down already <em>means</em> "meh, hide this from my feed" — and the album routes
    /// reject the token rather than folding it (see <see cref="DiscoveryVerdict.ForAlbum"/>), so no
    /// album row can reach the <c>_</c> arm carrying one.</para>
    /// </summary>
    private static (string? Add, string[] Remove)? VerdictTags(string? username, DiscoveryStatus? status)
    {
        var liked = ArtistTag.For(username, DiscoveryStatus.Liked);
        var disliked = ArtistTag.For(username, DiscoveryStatus.Disliked);
        if (liked is null || disliked is null)
        {
            return null; // no usable username to prefix the tag with
        }

        return status switch
        {
            DiscoveryStatus.Liked => (liked, new[] { disliked }),
            DiscoveryStatus.Disliked => (disliked, new[] { liked }),
            _ => (null, new[] { liked, disliked }),
        };
    }

    /// <summary>
    /// Deezer credits a compilation to its "Various Artists" placeholder, which is what a library files
    /// it under too — so using the credit verbatim is what lets ownership match once it lands. An album
    /// Deezer credits to nobody at all reads the same way.
    /// </summary>
    private static string CreditedArtist(DeezerAlbum album) =>
        string.IsNullOrWhiteSpace(album.artist?.name)
            ? PlaceholderArtist.VariousArtists
            : album.artist!.name!.Trim();

    private CollectionItem ToItem(DeezerAlbum album, Context context)
    {
        var artist = CreditedArtist(album);
        var title = album.title!.Trim();
        return new CollectionItem(
            album.id,
            title,
            new ArtistKey(artist),
            album.BestCoverUrl,
            $"https://www.deezer.com/album/{album.id}",
            Umbrella: UmbrellaArtist.Is(artist),
            Owned: context.Owned.Owns(artist, title),
            Verdict: context.VerdictFor(AlbumRatingKey.For(artist, title)),
            Year: album.Year,
            TrackCount: album.nb_tracks,
            RecordType: album.record_type);
    }

    private async Task<Context> LoadContext(string userId)
    {
        var verdicts = new Dictionary<string, DiscoveryStatus>(StringComparer.Ordinal);
        foreach (var rating in await _albumRatings.GetRated(userId))
        {
            verdicts[AlbumRatingKey.For(rating.Artist.ArtistName, rating.Album.AlbumName)] = rating.Status;
        }

        return new Context(
            await OwnedAlbumLookup.Load(_catalog, _overrides), verdicts, await _missing.GetAll());
    }

    private async Task<string?> MachineIdentifier()
    {
        try
        {
            return await _plex.GetMachineIdentifier();
        }
        catch (Exception ex)
        {
            // A deep link is a convenience; a Plex we can't reach must not fail the listing.
            _logger.LogDebug(ex, "Could not identify the Plex server for collection deep links");
            return null;
        }
    }

    /// <summary>One request's worth of shared state, so annotating N rows costs no extra reads.</summary>
    private sealed record Context(
        OwnedAlbumLookup Owned,
        Dictionary<string, DiscoveryStatus> Verdicts,
        MissingAlbum[] Missing)
    {
        /// <summary>
        /// This user's thumb on one record, or null when they haven't decided. Deliberately not
        /// <c>GetValueOrDefault</c>: <see cref="DiscoveryStatus"/>'s default is <c>Pending</c>, so an
        /// unrated collection would come back to the UI claiming a verdict it never got.
        /// </summary>
        public DiscoveryStatus? VerdictFor(string key) =>
            Verdicts.TryGetValue(key, out var status) ? status : null;
    }
}

/// <summary>
/// One item of a collection-rating batch — the same two fields the single-item route takes as query
/// parameters, so a client moving from one endpoint to the other doesn't have to re-derive what a
/// verdict is.
/// </summary>
/// <param name="Id">The Deezer album id.</param>
/// <param name="Verdict">"up" or "down"; anything that isn't "up" reads as down, exactly as the
/// single-item route has always treated it.</param>
public record CollectionRateItem(long Id, string Verdict);

/// <summary>
/// What became of one item of a collection batch. <paramref name="Item"/> is the rated row — the same
/// body the single-item route returns — and is null when the item failed, so a caller can act on the
/// successes without a second read of the list.
/// </summary>
public record CollectionRateResult(int Index, long Id, bool Ok, string? Error, CollectionItem? Item);
