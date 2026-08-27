using Mycelium.Backend.Services.Download;
using Mycelium.Deezer;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// Owns the shared "to buy" list and its lifecycle. The list is derived from every user's liked,
/// not-yet-owned artists and missing albums (the unified maintainer queue), but persisted with a
/// status — pending → sent → in-library — so ordering progress survives restarts and isn't
/// recomputed away.
///
/// <see cref="Reconcile"/> is the single sync point: it folds the current liked-but-unowned set into
/// the store, closes out anything that has since arrived in the library (→ InLibrary, stamping
/// <see cref="PurchaseItem.InLibraryAt"/> so the row states its own completion rather than leaving a
/// caller to infer it), and drops pending rows no one wants any more (already-ordered rows are kept —
/// they're in flight). It runs after each catalog/album sync and on each read of the list.
/// </summary>
public class PurchaseService
{
    private readonly IPurchaseRepo _purchases;
    private readonly IUserQueueRepo _queue;
    private readonly IUserAlbumRatingRepo _albumRatings;
    private readonly ILibraryProvider _library;
    private readonly IArtistCatalogRepo _catalog;
    private readonly IMissingAlbumRepo _missing;
    private readonly IAlbumMatchOverrideRepo _overrides;
    private readonly IDownloader _downloader;
    private readonly IDeezerApi _deezer;
    private readonly IAlbumTagger _albumTagger;
    private readonly DownloaderConfig _config;
    private readonly DownloadSettings _settings;
    private readonly UserQualityService _quality;
    private readonly JitterPolicy _jitter;
    private readonly DownloadSchedule _schedule;
    private readonly ILogger<PurchaseService> _logger;

    public PurchaseService(
        IPurchaseRepo purchases,
        IUserQueueRepo queue,
        IUserAlbumRatingRepo albumRatings,
        ILibraryProvider library,
        IArtistCatalogRepo catalog,
        IMissingAlbumRepo missing,
        IAlbumMatchOverrideRepo overrides,
        IDownloader downloader,
        IDeezerApi deezer,
        IAlbumTagger albumTagger,
        DownloaderConfig config,
        DownloadSettings settings,
        UserQualityService quality,
        JitterPolicy jitter,
        DownloadSchedule schedule,
        ILogger<PurchaseService> logger)
    {
        _settings = settings;
        _quality = quality;
        _jitter = jitter;
        _schedule = schedule;
        _purchases = purchases;
        _queue = queue;
        _albumRatings = albumRatings;
        _library = library;
        _catalog = catalog;
        _missing = missing;
        _overrides = overrides;
        _downloader = downloader;
        _deezer = deezer;
        _albumTagger = albumTagger;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// The active acquisition list — everything except items already in the library (so pending, sent
    /// and failed all show), newest first. Reconciles first so the page is always current.
    ///
    /// <para>The status filter is applied here rather than in the store because it isn't a property of
    /// the row so much as of this view — <see cref="Reconcile"/> and the drainer both want the
    /// <see cref="PurchaseStatus.InLibrary"/> rows the page hides.</para>
    ///
    /// <para><paramref name="includeCompleted"/> lifts that status filter, so the arrived rows come back
    /// too — carrying <see cref="PurchaseItem.InLibraryAt"/>, which is the only place the app states
    /// that an acquisition finished. That is precisely the answer a polling client is waiting for: on
    /// the active list, an acquisition finishing and a row being dropped because nobody wants it any
    /// more are the same observation. Off by default, because the page this method was written for must
    /// not accumulate every record ever acquired.</para>
    /// </summary>
    public async Task<PurchaseItem[]> GetActive(bool includeCompleted = false)
    {
        await Reconcile();
        return (await _purchases.GetAll())
            .Where(p => includeCompleted || p.Status != PurchaseStatus.InLibrary)
            .ToArray();
    }

    /// <summary>Moves a downloaded/queued item back to <see cref="PurchaseStatus.Pending"/> (undo).</summary>
    public Task<bool> Unsend(string id) => _purchases.SetStatus(id, PurchaseStatus.Pending);

    /// <summary>
    /// Drops a hand-added row. Every other row leaves the list by clearing the rating behind it and
    /// letting the reconcile prune it; a manual row has no rating, so it needs a direct delete — and
    /// conversely a rating-derived row must <em>not</em> be deletable this way, or it would simply
    /// reappear on the next reconcile and read as a broken button. Returns false when the id is
    /// unknown or the row isn't manual.
    /// </summary>
    public async Task<bool> RemoveManual(string id)
    {
        var row = (await _purchases.GetAll()).FirstOrDefault(p => p.Id == id);
        if (row is null || !row.Manual)
        {
            return false;
        }

        await _purchases.Remove(id);
        _logger.LogInformation("Removed hand-added album \"{Album}\" ({Artist})", row.Album, row.Artist.ArtistName);
        return true;
    }

    /// <summary>
    /// Queues an album by hand from a pasted Deezer link — the escape hatch for releases the
    /// artist-rooted discography walk can never reach. The walk starts from an owned artist and asks
    /// Deezer for that artist's albums, so anything Deezer doesn't file under a contributor is
    /// invisible to it: a various-artists compilation appears in no contributor's discography (and
    /// Deezer's own "Various Artists" artist lists no albums at all), and the same blind spot covers
    /// regional reissues and releases credited to a differently-spelled act.
    ///
    /// The row is a normal <see cref="FeedKind.MissingAlbum"/> carrying a Deezer id, which is all the
    /// downloader needs, and it closes out the usual way: the library files the album under its
    /// album-artist, so the next reconcile flips it to <see cref="PurchaseStatus.InLibrary"/> once it
    /// lands. What it does *not* do is touch anyone's ratings or the similarity graph — a compilation
    /// isn't a taste anchor, and this is an acquisition, not a preference.
    ///
    /// <para><paramref name="username"/> is whoever pasted the link. It rides on the row until the
    /// album lands and is then stamped on it as the permanent "&lt;user&gt;_added" credit — this is the
    /// clearest case of it, since a hand-pasted compilation has nothing but a person behind it.</para>
    /// </summary>
    public async Task<ManualAddOutcome> AddManual(string? pasted, string? username = null)
    {
        var albumId = DeezerAlbumLink.TryParse(pasted);
        if (albumId is null)
        {
            return new ManualAddOutcome(ManualAddResult.BadLink, null);
        }

        var album = await _deezer.GetAlbum(albumId.Value);
        if (album is null || string.IsNullOrWhiteSpace(album.title))
        {
            return new ManualAddOutcome(ManualAddResult.NotFound, null);
        }

        // Deezer credits a compilation to its "Various Artists" placeholder, which is exactly what a
        // library files it under too — so using it verbatim is what lets the arrival check below (and
        // the reconcile's close-out) find the album once it lands. An album Deezer credits to nobody
        // at all reads the same way.
        var artist = string.IsNullOrWhiteSpace(album.artist?.name)
            ? PlaceholderArtist.VariousArtists
            : album.artist!.name!;
        var title = album.title!.Trim();
        var id = PurchaseKey.ForAlbum(artist, title);

        var existing = (await _purchases.GetAll()).FirstOrDefault(p => p.Id == id);
        if (existing is not null)
        {
            return new ManualAddOutcome(ManualAddResult.AlreadyQueued, existing);
        }

        var ownedAlbums = NormalizeOwned(await _catalog.GetOwnedAlbums());
        if (AlbumIsOwned(ownedAlbums, await LoadOverrideKeys(), artist, title))
        {
            return new ManualAddOutcome(ManualAddResult.AlreadyOwned, null);
        }

        var item = new PurchaseItem(
            id, FeedKind.MissingAlbum, new ArtistKey(artist), title,
            album.BestCoverUrl, 0, Array.Empty<string>(),
            PurchaseStatus.Pending, default, null, albumId,
            // Album-artist and listing artist are the same thing here: there is no discography this
            // was reached through, so nothing to differ from.
            artist, DownloadFailure.None, Manual: true, AddedBy: username);
        await _purchases.Upsert(item);

        _logger.LogInformation(
            "Manually queued Deezer album {AlbumId}: \"{Album}\" ({Artist})", albumId, title, artist);

        return new ManualAddOutcome(
            ManualAddResult.Added, (await _purchases.GetAll()).FirstOrDefault(p => p.Id == id) ?? item);
    }

    /// <summary>
    /// A live snapshot of the download subsystem for the monitoring panel — backend + throttle
    /// config and current counts. Cheap (one read, no reconcile); the list query reconciles. "Queued"
    /// is downloadable albums still on the wishlist (not yet requested; wishlist artists don't
    /// download, so they're excluded). "Downloading" is everything already in the pipeline — requested
    /// and waiting in the drainer's queue plus the one in flight — so pressing Download visibly moves
    /// an album out of the queue at once, even though the drainer fetches them one at a time.
    /// </summary>
    public async Task<DownloadSnapshot> GetDownloadSnapshot()
    {
        var all = await _purchases.GetAll();
        var queued = all.Count(p => p.Status == PurchaseStatus.Pending
                                    && p.Kind.IsDownloadableAlbum() && p.DeezerAlbumId is > 0);
        // In-flight = queued-for-download + actively downloading, with the one in flight first so the
        // activity readout names what's fetching now.
        var current = all
            .Where(p => p.Status is PurchaseStatus.Queued or PurchaseStatus.Downloading)
            .OrderBy(p => p.Status == PurchaseStatus.Downloading ? 0 : 1)
            .ThenBy(p => p.RequestedAt)
            .ToArray();

        // One systemic reason for the whole panel: if any failed row died on a bad credential, every
        // other download is blocked the same way, so the page should say that once rather than let the
        // user read a list of identical row failures and reach for Retry. Most recent wins, so fixing
        // the ARL and getting a different failure clears it on the next attempt.
        var blocking = all
            .Where(p => p.Status == PurchaseStatus.Failed && p.Failure.IsSystemic())
            .OrderByDescending(p => p.SentAt ?? p.RequestedAt)
            .Select(p => p.Failure)
            .FirstOrDefault();

        return new DownloadSnapshot(
            // The live switch (stored, else the env default) — not the raw config, so the panel shows
            // what the drainer will actually do.
            await _settings.Automatic(),
            _downloader.Name,
            _config.BatchSize,
            _config.ItemDelay.TotalSeconds,
            _config.BatchInterval.TotalMinutes,
            _jitter.Percent,
            queued,
            current.Length,
            all.Count(p => p.Status == PurchaseStatus.Sent),
            all.Count(p => p.Status == PurchaseStatus.Failed),
            current,
            blocking,
            _schedule.NextItemAt,
            _schedule.NextBatchAt,
            // Null once the deadline passes, so the panel's fast badge switches itself off without
            // anything having to write the setting back.
            await _settings.FastUntil());
    }

    /// <summary>
    /// Folds the current liked-but-unowned set into the store and reconciles statuses. Idempotent —
    /// safe to call on every read and after every sync.
    /// </summary>
    public async Task Reconcile()
    {
        var owned = (await _library.GetAllArtistMetadata())
            .Select(a => a.ArtistKey.ArtistName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Normalize the owned album titles up front so typography / whitespace / zero-width
        // differences between Plex and Deezer can't keep an already-owned album stuck in the queue.
        // This is the same canonical match the missing-album diff uses, so the two agree.
        // Kept alongside the normalized map: the landing check below has to hand the merge it records
        // the library's own spelling of the title, which normalizing throws away.
        var libraryAlbums = await _catalog.GetOwnedAlbums();
        var ownedAlbums = NormalizeOwned(libraryAlbums);
        // User-asserted merges (near-miss titles the normalizer can't collapse): an album carrying an
        // override key is treated as owned, so it leaves the queue and stays gone across reconciles.
        var overrideKeys = await LoadOverrideKeys();

        // Per (listing-artist, album), sourced from the global missing-albums set so a liked album
        // carries what reconcile needs without threading it through the rating flow:
        //   - the Deezer id the downloader needs, and
        //   - the album-artist the library files it under (differs from the listing artist for a
        //     collaboration, e.g. a duo record surfaced via one member) — the key to match ownership.
        var missingAll = await _missing.GetAll();
        var deezerIds = missingAll
            .GroupBy(m => AlbumRatingKey.For(m.Artist.ArtistName, m.Album.AlbumName))
            .ToDictionary(g => g.Key, g => g.First().DeezerAlbumId);
        var albumArtists = missingAll
            .GroupBy(m => AlbumRatingKey.For(m.Artist.ArtistName, m.Album.AlbumName))
            .ToDictionary(g => g.Key, g => g.First().MatchArtist.ArtistName);

        // The act the library files an album under: the persisted album-artist if we have one, else
        // the freshest from the missing set, else the listing artist (non-collaboration default).
        string MatchArtistFor(string listingArtist, string album, string? persisted) =>
            persisted
            ?? (albumArtists.TryGetValue(AlbumRatingKey.For(listingArtist, album), out var aa) ? aa : null)
            ?? listingArtist;

        // Desired = the current liked-but-unowned items, keyed and deduped across users.
        var desired = new Dictionary<string, PurchaseItem>();

        foreach (var g in (await _queue.GetAllLiked())
                     .Where(c => !owned.Contains(c.Artist.ArtistName))
                     .GroupBy(c => c.Artist.ArtistName, StringComparer.OrdinalIgnoreCase))
        {
            var id = PurchaseKey.ForArtist(g.Key);
            desired[id] = new PurchaseItem(
                id, FeedKind.RecommendedArtist, g.First().Artist, null,
                g.Select(c => c.ImageUrl).FirstOrDefault(u => u != null),
                g.Max(c => c.Score),
                g.SelectMany(c => c.Sources).Distinct().ToArray(),
                PurchaseStatus.Pending, default, null, null, null);
        }

        // Liked albums come back paired with the user who liked them, so a row shared by several
        // people can be fetched once at the best of their entitlements. Grouped *before* the
        // ownership test, because whether the library already satisfies a want now depends on that
        // group's target — an album held at 320 satisfies a lossy liker and not a lossless one.
        foreach (var g in (await _albumRatings.GetAllLikedByUser())
                     .GroupBy(l => PurchaseKey.ForAlbum(l.Rating.Artist.ArtistName, l.Rating.Album.AlbumName)))
        {
            var first = g.First().Rating;
            // Recomputed every reconcile rather than fixed at first request: a lossless user liking
            // an album a lossy user already queued must raise its target before it downloads.
            var target = await _quality.BestOf(g.Select(l => l.UserId));
            var matchArtist = MatchArtistFor(first.Artist.ArtistName, first.Album.AlbumName, null);

            if (AlbumIsSatisfied(ownedAlbums, overrideKeys, matchArtist, first.Album.AlbumName, target))
            {
                continue;
            }

            // Owned, but below what is wanted: an upgrade rather than a gap. The kind is carried onto
            // the row because the two are acquired differently — an upgrade has an existing copy that
            // must be moved aside rather than merged with.
            var isUpgrade = AlbumIsOwned(ownedAlbums, overrideKeys, matchArtist, first.Album.AlbumName);
            // What is on disk right now. The downloader checks the result against this before
            // replacing anything, and by then the album is no longer in the missing set.
            var ownedQuality = isUpgrade
                ? OwnedQualityOf(ownedAlbums, matchArtist, first.Album.AlbumName)
                : null;

            var ratingKey = AlbumRatingKey.For(first.Artist.ArtistName, first.Album.AlbumName);
            long? deezerAlbumId = deezerIds.TryGetValue(ratingKey, out var did) && did != 0 ? did : null;
            // Persist the album-artist on the row so it still reconciles once the album leaves the
            // missing set (it drops out as soon as the library owns it).
            var albumArtist = albumArtists.TryGetValue(ratingKey, out var aa) ? aa : null;
            desired[g.Key] = new PurchaseItem(
                g.Key,
                isUpgrade ? FeedKind.UpgradeAlbum : FeedKind.MissingAlbum,
                first.Artist, first.Album.AlbumName,
                g.Select(l => l.Rating.AlbumArt).FirstOrDefault(a => a != null),
                0, Array.Empty<string>(),
                PurchaseStatus.Pending, default, null, deezerAlbumId, albumArtist,
                TargetQuality: target, OwnedQuality: ownedQuality);
        }

        // Insert new wants as pending / refresh display fields on existing rows.
        foreach (var item in desired.Values)
        {
            await _purchases.Upsert(item);
        }

        // Close the loop / prune: walk existing rows against ownership + desire.
        foreach (var row in await _purchases.GetAll())
        {
            // An album row closes out only when the library holds it *well enough* for what this row
            // asked. Testing bare ownership would flip an upgrade to InLibrary on the very next
            // reconcile — the lesser copy is right there — and it would never download.
            var nowOwned = row.Kind is FeedKind.MissingAlbum or FeedKind.UpgradeAlbum
                ? AlbumIsSatisfied(
                    ownedAlbums, overrideKeys,
                    MatchArtistFor(row.Artist.ArtistName, row.Album ?? "", row.AlbumArtist),
                    row.Album ?? "",
                    row.TargetQuality)
                : owned.Contains(row.Artist.ArtistName);

            // A row that already downloaded, at a quality below what is now being asked of it, is not
            // finished — someone entitled to better has since asked for this album. Send it back to be
            // fetched again rather than letting it close out on the copy that is already on disk.
            //
            // Only for a row we know came down short: AcquiredQuality is what the last download
            // actually produced. Null (never downloaded, or too old to have recorded it) is not
            // evidence of anything and must not trigger a re-fetch — that would re-queue the entire
            // back catalogue on the first reconcile after an upgrade.
            if (desired.TryGetValue(row.Id, out var want)
                && row.AcquiredQuality is { } have
                && want.TargetQuality is { } target
                && have < target
                && row.Status is PurchaseStatus.Sent or PurchaseStatus.InLibrary)
            {
                _logger.LogInformation(
                    "Re-queueing \"{Album}\" ({Artist}): downloaded at {Have} but now wanted at {Target}",
                    row.Album, row.Artist.ArtistName, have, target);
                await _purchases.SetStatus(row.Id, PurchaseStatus.Pending);
                continue;
            }

            // Post-download landing under another act. Ownership already forgives the renaming Plex
            // does to a title — "Light Upon the Lake (10th Anniversary Edition)" arriving as "Light
            // Upon the Lake" is the same record either way (AlbumTitleMatcher.NormalizeRecord). What it
            // doesn't forgive is the album landing under an act nobody asked about: a collaboration
            // Deezer credits one way and Plex files another. That row would sit in Sent for ever while
            // the diff kept calling the release a gap, so this looks for the record under the listing
            // artist too, and records what it finds as a match override rather than just flipping the
            // status — so the missing-album diff reaches the same verdict instead of re-offering the
            // release on the next sweep.
            //
            // Gated on the record being genuinely unowned rather than on nowOwned, which is also false
            // for an owned-but-too-lossy copy. An upgrade row must not be closed out by "the record is
            // right there": the whole point of it is that the copy on disk isn't good enough.
            if (!nowOwned
                && row.Status == PurchaseStatus.Sent
                && row.Kind == FeedKind.MissingAlbum
                && row.Album is { } sentAlbum
                && !AlbumIsOwned(
                       ownedAlbums, overrideKeys,
                       MatchArtistFor(row.Artist.ArtistName, sentAlbum, row.AlbumArtist),
                       sentAlbum)
                && LandedTitleFor(
                       libraryAlbums,
                       MatchArtistFor(row.Artist.ArtistName, sentAlbum, row.AlbumArtist),
                       row.Artist.ArtistName,
                       sentAlbum) is { } landedTitle)
            {
                _logger.LogInformation(
                    "\"{Album}\" ({Artist}) landed in the library as \"{Library}\" — recording the match "
                    + "so it stops reading as missing",
                    sentAlbum, row.Artist.ArtistName, landedTitle);
                await MergeAlbum(row.Artist.ArtistName, sentAlbum, landedTitle);
                continue;
            }

            if (nowOwned)
            {
                if (row.Status != PurchaseStatus.InLibrary)
                {
                    // The record has arrived, so there is finally a Plex item to credit. Stamped here
                    // and only here: this branch runs once per row, on the transition into the library.
                    await StampAddedBy(
                        row, MatchArtistFor(row.Artist.ArtistName, row.Album ?? "", row.AlbumArtist));
                    // This is the moment the acquisition is actually finished, and the guard above is
                    // what makes it the *first* such moment — so the InLibraryAt the repo stamps
                    // alongside the status is the arrival time, not the time of a later re-reconcile.
                    await _purchases.SetStatus(row.Id, PurchaseStatus.InLibrary);
                }
                continue;
            }

            // Not owned and no longer wanted: drop rows that aren't in flight (pending/failed);
            // keep Sent ones (already downloaded, waiting to land in the library). A manual row is
            // never "no longer wanted" — nothing rated it, so it can't appear in `desired`, and
            // pruning it would delete a just-pasted album before the page that added it even
            // re-rendered (GetActive reconciles on every read). It leaves only by arriving in the
            // library (above) or by being removed by hand.
            if (!desired.ContainsKey(row.Id)
                && !row.Manual
                && row.Status is PurchaseStatus.Pending or PurchaseStatus.Failed)
            {
                await _purchases.Remove(row.Id);
            }
        }
    }

    /// <summary>
    /// Writes the permanent "&lt;user&gt;_added" credit onto a record that has just landed, naming
    /// whoever asked for it. The album-level twin of nothing — there is no artist equivalent on
    /// purpose: an artist is a shelf that fills up over years, while a record enters the library once,
    /// because one person went and got it.
    ///
    /// <para>Album rows only. An artist row has no record to stamp, and an upgrade row's album was
    /// already on the shelf — swapping in a better rip is not adding it, and whoever originally did
    /// still holds the credit from when it first landed.</para>
    ///
    /// <para>Best-effort, like every tagging path in this app: <see cref="IAlbumTagger"/> logs and
    /// swallows, so a Plex blip costs this credit rather than stalling the row in the acquisition list
    /// for ever. The row keeps <see cref="PurchaseItem.AddedBy"/> either way, so the attribution
    /// survives even when the tag write didn't.</para>
    /// </summary>
    private async Task StampAddedBy(PurchaseItem row, string albumArtist)
    {
        if (row.Kind != FeedKind.MissingAlbum
            || row.Album is not { Length: > 0 } album
            || ArtistTag.Added(row.AddedBy) is not { } tag)
        {
            return;
        }

        await _albumTagger.SetTags(albumArtist, album, tag, Array.Empty<string>());
        _logger.LogInformation(
            "Credited \"{Album}\" ({Artist}) to {User} as it landed in the library",
            album, albumArtist, row.AddedBy);
    }

    /// <summary>
    /// Canonicalises the owned map's titles for matching, keeping each album's quality alongside.
    /// Typography, whitespace and zero-width differences between Plex and Deezer would otherwise keep
    /// an album we already have stuck in the queue; this is the same canonical match the missing-album
    /// diff uses, so the two agree. Where several library titles collapse to one record key the better
    /// copy wins: owning a record twice, once losslessly, is not owning a lossy one.
    /// </summary>
    private static Dictionary<string, Dictionary<string, AudioQuality?>> NormalizeOwned(
        Dictionary<string, Dictionary<string, AudioQuality?>> owned)
    {
        var result = new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (artist, albums) in owned)
        {
            var byTitle = new Dictionary<string, AudioQuality?>(StringComparer.Ordinal);
            foreach (var (title, quality) in albums)
            {
                var key = AlbumTitleMatcher.NormalizeRecord(title);
                if (!byTitle.TryGetValue(key, out var existing) || quality > existing)
                {
                    byTitle[key] = quality;
                }
            }
            result[artist] = byTitle;
        }
        return result;
    }

    private static bool AlbumIsOwned(
        Dictionary<string, Dictionary<string, AudioQuality?>> ownedAlbums,
        HashSet<string> overrideKeys,
        string artist,
        string album) =>
        (ownedAlbums.TryGetValue(artist, out var set)
         && set.ContainsKey(AlbumTitleMatcher.NormalizeRecord(album)))
        || overrideKeys.Contains(AlbumOverrideKey.For(artist, album));

    /// <summary>
    /// The library's own title for a downloaded release that has landed under a simplified name, or
    /// null when nothing in the library answers to that record. Tried under the act the library files
    /// the album under first, then the listing artist — the two differ for a collaboration, and either
    /// is a real answer.
    ///
    /// Record-level (<see cref="AlbumTitleMatcher.NormalizeRecord"/>), the same granularity ownership
    /// turns on — what this adds over the ownership check is the second act it tries.
    /// </summary>
    private static string? LandedTitleFor(
        Dictionary<string, Dictionary<string, AudioQuality?>> libraryAlbums,
        string matchArtist,
        string listingArtist,
        string album)
    {
        var wanted = AlbumTitleMatcher.NormalizeRecord(album);
        if (wanted.Length == 0)
        {
            return null;
        }

        foreach (var artist in new[] { matchArtist, listingArtist }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!libraryAlbums.TryGetValue(artist, out var albums))
            {
                continue;
            }

            foreach (var title in albums.Keys)
            {
                if (AlbumTitleMatcher.NormalizeRecord(title) == wanted)
                {
                    return title;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The quality of the copy the library holds, or null when it holds none <em>or</em> hasn't had
    /// its quality determined. Both read as "no upgrade is due": for the first there is nothing to
    /// upgrade from, and for the second we have no evidence anything is wrong.
    /// </summary>
    private static AudioQuality? OwnedQualityOf(
        Dictionary<string, Dictionary<string, AudioQuality?>> ownedAlbums, string artist, string album) =>
        ownedAlbums.TryGetValue(artist, out var set)
        && set.TryGetValue(AlbumTitleMatcher.NormalizeRecord(album), out var quality)
            ? quality
            : null;

    /// <summary>
    /// Whether the library already satisfies a want for this album at <paramref name="target"/>.
    ///
    /// <para>This is the tier-aware form of <see cref="AlbumIsOwned"/>, and the distinction is what
    /// makes upgrades possible at all: owning an album is no longer the end of the question. An album
    /// held at 320 that someone wants lossless is <em>not</em> satisfied, so it stays on the queue
    /// rather than closing out against the copy already on disk.</para>
    ///
    /// <para>An undetermined quality counts as satisfied — <c>null &lt; target</c> is false — so the
    /// library isn't re-acquired wholesale before the catch-up sweep has run.</para>
    /// </summary>
    private static bool AlbumIsSatisfied(
        Dictionary<string, Dictionary<string, AudioQuality?>> ownedAlbums,
        HashSet<string> overrideKeys,
        string artist,
        string album,
        AudioQuality? target)
    {
        if (!AlbumIsOwned(ownedAlbums, overrideKeys, artist, album))
        {
            return false;
        }
        return !(OwnedQualityOf(ownedAlbums, artist, album) < target);
    }

    /// <summary>The merge lookup keys (see <see cref="AlbumOverrideKey"/>), one per recorded override.</summary>
    private async Task<HashSet<string>> LoadOverrideKeys() =>
        (await _overrides.GetAll())
        .Select(o => AlbumOverrideKey.For(o.MatchArtist, o.DeezerTitle))
        .ToHashSet();

    /// <summary>How many library albums a merge search returns — enough to scan, bounded for the wire.</summary>
    private const int SearchLimit = 60;

    /// <summary>
    /// The library albums a (near-miss titled) album can be merged into. With no <paramref name="query"/>
    /// these are the suggestions: everything owned under the acts this album could be filed under (see
    /// <see cref="MergeArtistsFor"/>), plus any album anywhere in the library carrying the same title —
    /// that second set is the cross-artist duplicate (Plex has "Care Tracts" under "Matthewdavid's
    /// Mindflight" while Deezer lists it under "Matthewdavid"), and it sorts first. A non-empty query
    /// searches the whole library instead, on artist or title, for when neither suggestion fits.
    /// </summary>
    public async Task<LibraryAlbumOption[]> MergeCandidates(string artist, string album, string? query)
    {
        var all = (await _catalog.GetOwnedAlbums())
            .SelectMany(kvp => kvp.Value.Keys.Select(title => new LibraryAlbumOption(kvp.Key, title)));

        var q = query?.Trim();
        if (!string.IsNullOrEmpty(q))
        {
            return all
                .Where(o => o.Album.Contains(q, StringComparison.OrdinalIgnoreCase)
                            || o.Artist.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(o => o.Artist, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(o => o.Album, StringComparer.CurrentCultureIgnoreCase)
                .Take(SearchLimit)
                .ToArray();
        }

        var acts = await MergeArtistsFor(artist, album);
        var title = AlbumTitleMatcher.NormalizeRecord(album);
        return all
            .Select(o => (Option: o, SameTitle: AlbumTitleMatcher.NormalizeRecord(o.Album) == title))
            .Where(x => x.SameTitle || acts.Contains(x.Option.Artist, StringComparer.OrdinalIgnoreCase))
            .OrderBy(x => x.SameTitle ? 0 : 1)
            .ThenBy(x => x.Option.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Option.Album, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.Option)
            .Take(SearchLimit)
            .ToArray();
    }

    /// <summary>
    /// Merges an album the diff calls missing into one already in the library under a different title:
    /// records a durable match override (honoured by the reconcile AND the missing-album diff), drops
    /// the row from the global missing set so it stops surfacing before the next sweep re-diffs it, and
    /// closes out any queued purchase as <see cref="PurchaseStatus.InLibrary"/>. Keyed by the (listing
    /// artist, album) the UI shows, so it works from the Download queue, the Browse discography and the
    /// Discover feed alike — including for an album no one has thumbed yet. Returns false on blank input.
    /// </summary>
    public async Task<bool> MergeAlbum(string artist, string album, string libraryAlbum)
    {
        if (string.IsNullOrWhiteSpace(artist)
            || string.IsNullOrWhiteSpace(album)
            || string.IsNullOrWhiteSpace(libraryAlbum))
        {
            return false;
        }

        foreach (var act in await MergeArtistsFor(artist, album))
        {
            await _overrides.Add(new AlbumMatchOverride(act, album, libraryAlbum));
        }

        await DropFromMissing(artist, album);
        await _purchases.SetStatus(PurchaseKey.ForAlbum(artist, album), PurchaseStatus.InLibrary);
        _logger.LogInformation(
            "Merged album \"{Album}\" ({Artist}) into library album \"{Library}\"",
            album, artist, libraryAlbum);
        return true;
    }

    /// <summary>
    /// The acts a merge has to be recorded under so every ownership check honours it: the listing
    /// artist (what the missing-album diff keys on while scanning that discography) plus the album-artist
    /// the library files it under (what the reconcile keys on) — from the queued row if there is one,
    /// else the missing set. The two differ for a collaboration, and the missing row disappears once the
    /// merge lands, so recording both is what keeps the album from resurfacing.
    /// </summary>
    private async Task<string[]> MergeArtistsFor(string artist, string album)
    {
        var acts = new List<string> { artist };

        var row = (await _purchases.GetAll())
            .FirstOrDefault(p => p.Id == PurchaseKey.ForAlbum(artist, album));
        if (row?.AlbumArtist is { } persisted)
        {
            acts.Add(persisted);
        }

        var key = AlbumRatingKey.For(artist, album);
        var missing = (await _missing.GetAll())
            .FirstOrDefault(m => AlbumRatingKey.For(m.Artist.ArtistName, m.Album.AlbumName) == key);
        if (missing is not null)
        {
            acts.Add(missing.MatchArtist.ArtistName);
        }

        return acts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Removes one album from the listing artist's missing set, so a merge takes effect in the feed
    /// immediately rather than at the next discography sweep (which the override then resolves as owned).
    /// </summary>
    private async Task DropFromMissing(string artist, string album)
    {
        var forArtist = (await _missing.GetAll())
            .Where(m => string.Equals(m.Artist.ArtistName, artist, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var remaining = forArtist
            .Where(m => !string.Equals(m.Album.AlbumName, album, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (remaining.Count != forArtist.Count)
        {
            // The store matches the artist exactly, so rewrite under the spelling it holds rather than
            // whatever casing the caller passed.
            await _missing.ReplaceForArtist(forArtist[0].Artist.ArtistName, remaining);
        }
    }
}
