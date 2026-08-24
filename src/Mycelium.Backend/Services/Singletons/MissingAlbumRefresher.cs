using System.Collections.Concurrent;
using Mycelium.Deezer.Models;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>The outcome of one missing-album sync pass.</summary>
public record MissingAlbumSyncResult(int ArtistsScanned, int MissingTotal);

/// <summary>
/// One album from an artist's Deezer discography, flagged with whether the library owns it.
/// <paramref name="Year"/> is Deezer's release year (null when it supplied no date, or for an owned
/// album Deezer doesn't list at all).
/// <paramref name="RecordType"/> is Deezer's <c>record_type</c> ("album" / "ep" / "single" /
/// "compilation"), surfaced so the drill-down can label each row — the listing carries every type,
/// so without the label a single would read as an album. Null for an owned album Deezer doesn't list.
/// </summary>
public record DiscographyAlbum(
    string Title, string? CoverUrl, long? DeezerAlbumId, bool Owned, int? Year = null, string? RecordType = null);

/// <summary>
/// The missing-album sync job: for each owned artist, pulls its Deezer discography and diffs it
/// against the albums already in the library, persisting the gap into <see cref="IMissingAlbumRepo"/>.
/// This is the only path that touches Deezer for albums; the per-user "missing albums" feed reads
/// the persisted result, so it works even when Deezer is down. Heavy (one discography call per owned
/// artist), so it runs on its own schedule, separate from the cheap Plex catalog refresh.
///
/// The sweep also doubles as the warm-up for the interactive paths: it resolves every gap's credited
/// act into <see cref="IDeezerAlbumArtistRepo"/>, which is what lets <see cref="Discography"/> answer a
/// drill-down without spending a Deezer call per release.
/// </summary>
public class MissingAlbumRefresher
{
    // Deezer marks records as record_type "album" / "ep" / "single" / "compilation". The sync takes all
    // four: a release Deezer files as a single (Ben Howard's 3-track "Another Friday Night / Hot Heavy
    // Summer / Sister" is one) was previously dropped here and so was invisible everywhere in the app.
    //
    // This used to be where singles and compilations were held back from the Discover feed, but a row
    // dropped here has no persisted MissingAlbum — and that row is what carries the Deezer id through
    // reconcile to the downloader. Filtering at the sync would have made a single visible in the
    // drill-down yet permanently un-downloadable if queued from it. So everything is persisted, tagged
    // with its type, and the feed does its own filtering (AlbumRecordType.IsFeedEligible).
    private static readonly HashSet<string> ListedRecordTypes =
        new(StringComparer.OrdinalIgnoreCase) { "album", "ep", "single", "compilation" };

    private readonly IArtistCatalogRepo _catalog;
    private readonly DeezerArtistResolver _resolver;
    private readonly IDeezerApi _deezer;
    private readonly IMissingAlbumRepo _missing;
    private readonly IAlbumMatchOverrideRepo _overrides;
    private readonly IDeezerAlbumArtistRepo _albumArtists;
    private readonly ILogger<MissingAlbumRefresher> _logger;

    // How many /album/{id} lookups to have in flight at once. DeezerApi paces every call through a
    // rolling window (40 per 5s), so this can't outrun the ceiling — it only stops the walk from
    // paying a full round trip's latency per album on top of the pacing, which is what made a cold
    // discography a sequential minute-long crawl.
    private const int ResolveConcurrency = 8;

    public MissingAlbumRefresher(
        IArtistCatalogRepo catalog,
        DeezerArtistResolver resolver,
        IDeezerApi deezer,
        IMissingAlbumRepo missing,
        IAlbumMatchOverrideRepo overrides,
        IDeezerAlbumArtistRepo albumArtists,
        ILogger<MissingAlbumRefresher> logger)
    {
        _catalog = catalog;
        _resolver = resolver;
        _deezer = deezer;
        _missing = missing;
        _overrides = overrides;
        _albumArtists = albumArtists;
        _logger = logger;
    }

    public async Task<MissingAlbumSyncResult> Refresh()
    {
        var present = await _catalog.GetAllPresent();
        var ownedAlbums = await _catalog.GetOwnedAlbums();

        var scanned = 0;
        var missingTotal = 0;

        var skipped = 0;
        foreach (var artist in present)
        {
            scanned++;
            try
            {
                missingTotal += (await RefreshOne(artist.ArtistKey, ownedAlbums)).Count;
            }
            catch (DeezerUnavailableException ex)
            {
                // One artist Deezer wouldn't answer for doesn't stop the sweep, and — crucially —
                // doesn't clear that artist's stored rows either. Their existing gap stands until a
                // pass that actually hears back from Deezer.
                skipped++;
                _logger.LogWarning(
                    "Missing-album sync: skipping {Artist} — {Reason}", artist.ArtistKey.ArtistName, ex.Message);
            }
        }

        _logger.LogInformation(
            "Missing-album sync: scanned {Scanned} owned artist(s), {Missing} missing album(s) total"
            + "{SkipNote}",
            scanned, missingTotal, skipped > 0 ? $", {skipped} skipped (Deezer unavailable)" : string.Empty);
        return new MissingAlbumSyncResult(scanned, missingTotal);
    }

    /// <summary>
    /// Resolves one artist's Deezer discography, diffs it against the albums already owned for that
    /// artist, and persists the gap into <see cref="IMissingAlbumRepo"/> (replacing the artist's prior
    /// rows). Shared by the bulk <see cref="Refresh"/> sweep over owned artists and the on-demand
    /// expansion when a user likes a brand-new recommended artist (whose owned set is empty, so the
    /// whole discography surfaces as acquirable). Takes the full library ownership map (artist -> owned
    /// album titles) so a collaboration album can be checked against the act it's actually filed under,
    /// not just the listing artist. Returns the persisted rows.
    /// </summary>
    /// <exception cref="DeezerUnavailableException">
    /// Deezer didn't answer, so nothing is persisted — the artist's existing rows are left standing
    /// rather than replaced with an empty set we have no evidence for.
    /// </exception>
    public async Task<IReadOnlyList<MissingAlbum>> RefreshOne(
        ArtistKey artist, IReadOnlyDictionary<string, Dictionary<string, AudioQuality?>> ownedAlbums)
    {
        var diff = await FetchAndDiff(artist, ownedAlbums, ArtistResolution.Full);
        var missing = diff?.Missing ?? new List<MissingAlbum>();
        // Persist the gap (or clear stale rows when the artist has no Deezer match) so the per-user
        // feed and a later like — which carries the row's DeezerAlbumId to the downloader — stay current.
        await _missing.ReplaceForArtist(artist.ArtistName, missing);
        return missing;
    }

    /// <summary>
    /// One artist's full Deezer discography (every record type — LPs, EPs, singles and compilations),
    /// each flagged with whether the library already owns it and labelled with its type — for the
    /// Artists-page drill-down. Persists the missing subset as a side effect (same as
    /// <see cref="RefreshOne"/>) so a later like carries the Deezer id to the downloader — including for
    /// singles and compilations, which are queueable from here even though the Discover feed passes over
    /// them (<see cref="AlbumRecordType.IsFeedEligible"/>). Every pressing Deezer lists gets its own row —
    /// the deluxe edition and the remaster are two entries here, each owned or missing on its own — so
    /// nothing in an artist's discography is hidden behind another edition of itself. Owned albums the
    /// library has that Deezer doesn't list at all are appended (without art/id/type) so the picture is
    /// complete.
    ///
    /// This runs in front of a click, so it resolves credited acts only where the answer can still
    /// change an owned/missing verdict (<see cref="ArtistResolution.OwnershipOnly"/>) rather than for
    /// every gap — the difference between a couple of Deezer calls and one per unowned release, which
    /// for a prolific artist was a hundred-odd rate-limited calls and fifteen seconds of spinner. The
    /// nightly <see cref="Refresh"/> resolves the rest into the shared memo, so what it has learned is
    /// still applied here for free.
    /// </summary>
    /// <exception cref="DeezerUnavailableException">
    /// Deezer didn't answer. Nothing is persisted and nothing is returned — the caller surfaces this
    /// as "Deezer is busy, retrying" rather than an authoritative empty discography, which the user
    /// would otherwise see (and the client would cache) as "this artist has no albums".
    /// </exception>
    public async Task<IReadOnlyList<DiscographyAlbum>> Discography(
        ArtistKey artist, IReadOnlyDictionary<string, Dictionary<string, AudioQuality?>> ownedAlbums)
    {
        var diff = await FetchAndDiff(artist, ownedAlbums, ArtistResolution.OwnershipOnly);
        await _missing.ReplaceForArtist(artist.ArtistName, diff?.Missing ?? new List<MissingAlbum>());

        var all = diff?.All.ToList() ?? new List<DiscographyAlbum>();
        var ownedAlbumTitles = ownedAlbums.TryGetValue(artist.ArtistName, out var ownedSet)
            ? (IEnumerable<string>)ownedSet.Keys
            : Array.Empty<string>();
        // Fold in owned albums Deezer didn't surface at all (no match, or a title too far off to pair)
        // so the library's view is the source of truth for what we have.
        var seen = all.Select(a => NormalizeTitle(a.Title)).ToHashSet(StringComparer.Ordinal);
        foreach (var title in ownedAlbumTitles)
        {
            if (seen.Add(NormalizeTitle(title)))
            {
                all.Add(new DiscographyAlbum(title, null, null, Owned: true));
            }
        }
        return all;
    }

    /// <summary>
    /// How much of the album-artist backfill a diff is willing to pay for. Deezer's listing doesn't name
    /// the act a release is credited to, and learning it is a rate-limited call each — cheap spread
    /// across a nightly sweep, ruinous in front of a click.
    /// </summary>
    private enum ArtistResolution
    {
        /// <summary>Learn every unknown release's credited act. The sweep, which fills the durable memo
        /// the interactive paths then read for free.</summary>
        Full,

        /// <summary>Learn it only where the answer can still flip a release between owned and missing.
        /// Rows the memo already covers are unaffected either way; a release Deezer added since the last
        /// sweep keeps the listing artist as its credited act until that sweep picks it up.</summary>
        OwnershipOnly,
    }

    /// <summary>
    /// Resolves the artist on Deezer, gathers their catalog (the discography listing, backfilled from
    /// album search — see <see cref="Backfill"/>), and walks it, splitting it into the full annotated
    /// list (every listed record type and every pressing, flagged owned/missing) and the missing
    /// subset, each row tagged with its record type so the feed can decide for itself what to push.
    /// Returns null when the artist has no Deezer match. Ownership compares on a normalized title so
    /// punctuation/casing differences between Plex and Deezer (a typographic vs. straight apostrophe)
    /// don't make an owned album look missing — but edition decoration is kept, so owning "Both Sides"
    /// says nothing about "Both Sides (Deluxe Edition)"; the original Deezer title is what we surface.
    ///
    /// The walk is in two passes rather than one: the first settles what it can without leaving the
    /// process and collects the releases whose credited act is still in question, so the second can
    /// resolve them as a single batch (see <see cref="AlbumArtists"/>) instead of a serial call per row.
    /// </summary>
    /// <exception cref="DeezerUnavailableException">
    /// Deezer never answered — so nothing learned here is evidence of anything. Distinct from a null
    /// return ("Deezer answered: no such artist"), because the callers <em>persist</em> this diff:
    /// treating an unanswered call as an empty discography wipes the artist's missing-album rows and
    /// blanks their album list in the UI, which is exactly what a five-second quota blip used to do.
    /// </exception>
    private async Task<(List<DiscographyAlbum> All, List<MissingAlbum> Missing)?> FetchAndDiff(
        ArtistKey artist,
        IReadOnlyDictionary<string, Dictionary<string, AudioQuality?>> ownedAlbums,
        ArtistResolution resolution)
    {
        var lookup = await _resolver.Lookup(artist.ArtistName);
        if (lookup.Unavailable)
        {
            throw new DeezerUnavailableException(
                $"Deezer didn't answer the artist lookup for '{artist.ArtistName}'");
        }

        var deezerId = lookup.Value?.Id;
        if (deezerId is null)
        {
            return null;
        }

        var listing = await _deezer.GetAlbums(deezerId.Value);
        if (listing is null)
        {
            throw new DeezerUnavailableException(
                $"Deezer didn't answer the discography listing for '{artist.ArtistName}'");
        }

        var catalog = await Backfill(deezerId.Value, lookup.Value!.Name ?? artist.ArtistName, listing);

        // Normalized owned titles per artist name, mapped to the quality of the copy on disk, computed
        // lazily and memoised for this pass — so the common (scanning artist) lookup and any
        // album-artist lookup share the work. Presence answers "do we own it"; the value answers "is
        // what we have good enough", which is a separate question the diff asks second.
        var normalizedOwned = new Dictionary<string, Dictionary<string, AudioQuality?>>(
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, AudioQuality?> OwnedTitlesFor(string artistName)
        {
            if (normalizedOwned.TryGetValue(artistName, out var cached))
            {
                return cached;
            }

            var byTitle = new Dictionary<string, AudioQuality?>(StringComparer.Ordinal);
            if (ownedAlbums.TryGetValue(artistName, out var albums))
            {
                foreach (var (title, quality) in albums)
                {
                    // Two library titles can normalize to the same key (a punctuation variant). Keep
                    // the better copy: owning a record twice, once lossless, is not owning a lossy one.
                    var key = NormalizeTitle(title);
                    if (!byTitle.TryGetValue(key, out var existing) || quality > existing)
                    {
                        byTitle[key] = quality;
                    }
                }
            }
            return normalizedOwned[artistName] = byTitle;
        }

        var scannedOwned = OwnedTitlesFor(artist.ArtistName);

        // User-asserted merges — a release the diff would call missing that the user has confirmed is
        // already in the library under a near-miss title. Same keys the purchase reconcile builds.
        var overrides = await _overrides.GetAll();
        var overrideKeys = overrides
            .Select(o => AlbumOverrideKey.For(o.MatchArtist, o.DeezerTitle))
            .ToHashSet();

        // Every album title the library holds, under any act, and every title a merge has been recorded
        // against — the two ways a release not in the scanning artist's own set can still turn out to be
        // owned. Both are whole-library sets, so they're built only if something actually asks.
        var ownedAnywhere = new Lazy<HashSet<string>>(() => ownedAlbums.Values
            .SelectMany(albums => albums.Keys)
            .Select(NormalizeTitle)
            .ToHashSet(StringComparer.Ordinal));
        var mergedTitles = new Lazy<HashSet<string>>(() => overrides
            .Select(o => NormalizeTitle(o.DeezerTitle))
            .ToHashSet(StringComparer.Ordinal));

        // Whether learning a release's credited act could still flip it from missing to owned. When
        // nobody in the library owns a record by that title and no merge names it, the lookup can only
        // confirm what we already have — and that is nearly every row of a discography, which is why
        // paying a rate-limited Deezer call for each of them put fifteen seconds in front of the
        // drill-down.
        bool CouldChangeOwnership(string key) =>
            ownedAnywhere.Value.Contains(key) || mergedTitles.Value.Contains(key);

        // First pass: one row per release, and the cheap half of the ownership question — owned outright
        // by the artist we're scanning — settled without leaving the process.
        //
        // One key per release, and it keeps the edition decoration. Each pressing Deezer lists is its
        // own row with its own id: the deluxe edition, the remaster and the plain LP are three
        // releases here, and a user acting on one of those rows is acting on that release alone.
        // Only a title Deezer repeats verbatim is dropped as the duplicate it is.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<(DeezerAlbum Album, string Key, bool Owned)>();
        foreach (var album in catalog)
        {
            var key = NormalizeTitle(album.title);
            if (string.IsNullOrEmpty(key)
                || album.record_type is null
                || !ListedRecordTypes.Contains(album.record_type)
                || !seen.Add(key))
            {
                continue;
            }

            var owned = scannedOwned.ContainsKey(key)
                        || overrideKeys.Contains(AlbumOverrideKey.For(artist.ArtistName, album.title));
            rows.Add((album, key, owned));
        }

        // The credited act for everything still unaccounted for, in one batch.
        var albumArtists = await AlbumArtists(
            rows.Where(r => !r.Owned).Select(r => (r.Album.id, r.Key)).ToList(),
            resolution == ArtistResolution.Full ? null : CouldChangeOwnership);

        var all = new List<DiscographyAlbum>(rows.Count);
        var missing = new List<MissingAlbum>();
        foreach (var (album, key, ownedByScanned) in rows)
        {
            var isOwned = ownedByScanned;
            var albumArtist = artist;
            // Not in the scanning artist's owned set. It may be a collaboration the listing surfaces
            // via one member (e.g. a duo record) but the library files under the duo name, so the act
            // Deezer credits it to settles both (a) whether Plex already has it under that name and
            // (b) which act reconcile should later match it against. An id we have no answer for —
            // never looked up, or a call that failed — falls back to the listing artist.
            if (!isOwned
                && albumArtists.TryGetValue(album.id, out var resolved)
                && !string.Equals(resolved, artist.ArtistName, StringComparison.OrdinalIgnoreCase))
            {
                albumArtist = new ArtistKey(resolved);
                isOwned = OwnedTitlesFor(resolved).ContainsKey(key)
                          || overrideKeys.Contains(AlbumOverrideKey.For(resolved, album.title));
            }

            all.Add(new DiscographyAlbum(
                album.title, album.BestCoverUrl, album.id, isOwned, album.Year, album.record_type));
            if (!isOwned)
            {
                missing.Add(new MissingAlbum(
                    artist, new AlbumKey(album.title), album.BestCoverUrl, album.id, albumArtist,
                    album.Year, album.record_type));
            }
        }

        return (all, missing);
    }

    /// <summary>
    /// The artist's discography listing plus the releases it leaves out. Deezer's
    /// <c>/artist/{id}/albums</c> is not the whole catalog: it omits releases Deezer itself credits to
    /// that artist — everything Against Me! put out after 2011, and 87 of Walk Off The Earth's 154
    /// releases, whole albums among them — which is why an album could be missing from an artist's
    /// readout while its own Deezer page sat there working. Album search reaches those, so it backfills
    /// the listing.
    ///
    /// Search answers for every artist whose name resembles the one asked for, so the results are
    /// filtered down to the id we resolved; that check is what makes a fuzzy search safe to merge. The
    /// listing comes first and search-only rows follow, so a release Deezer lists properly keeps the
    /// richer row (search results carry no release date, hence no year).
    /// </summary>
    /// <exception cref="DeezerUnavailableException">
    /// The search never answered. The listing alone would be a smaller catalog than last night's, and
    /// the caller <em>replaces</em> the artist's stored rows with what it's given — so a release only
    /// search knows about would drop out, taking the Deezer id a queued download needs with it. Better
    /// to skip the artist and leave their rows standing, exactly as for an unanswered listing.
    /// </exception>
    private async Task<IReadOnlyList<DeezerAlbum>> Backfill(
        long deezerId, string searchName, DeezerAlbum[] listing)
    {
        var found = await _deezer.SearchArtistAlbums(searchName);
        if (found is null)
        {
            throw new DeezerUnavailableException(
                $"Deezer didn't answer the album search for '{searchName}'");
        }

        var known = listing.Select(a => a.id).ToHashSet();
        var extra = found.Where(a => a.artist?.id == deezerId && known.Add(a.id)).ToList();
        if (extra.Count > 0)
        {
            _logger.LogInformation(
                "Deezer's discography for {Artist} omitted {Count} release(s) it credits to them; "
                + "recovered by search", searchName, extra.Count);
        }

        return listing.Concat(extra).ToList();
    }

    /// <summary>
    /// The act Deezer credits each of these releases to. Answers come from the durable memo wherever we
    /// already have them (<see cref="IDeezerAlbumArtistRepo"/> — a release's credited act never changes,
    /// so it's learned once and kept across restarts), and the rest are fetched concurrently, still
    /// paced by <c>DeezerApi</c>'s rolling window. Anything learned is written back for everyone after.
    ///
    /// <paramref name="worthResolving"/> bounds what is fetched at all: null learns every unknown id
    /// (the nightly sweep, where the latency is free and the memo it fills is what makes the
    /// interactive paths cheap), a predicate learns only the rows whose answer can still change
    /// something. A release left unresolved keeps the listing artist as its credited act — the same
    /// fallback as one Deezer declines to answer for.
    /// </summary>
    private async Task<Dictionary<long, string>> AlbumArtists(
        IReadOnlyList<(long Id, string Key)> candidates, Func<string, bool>? worthResolving)
    {
        if (candidates.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        var known = await _albumArtists.Get(candidates.Select(c => c.Id).Distinct().ToList());
        var unknown = candidates
            .Where(c => !known.ContainsKey(c.Id))
            .Where(c => worthResolving is null || worthResolving(c.Key))
            .Select(c => c.Id)
            .Distinct()
            .ToList();
        if (unknown.Count == 0)
        {
            return known;
        }

        var learned = new ConcurrentDictionary<long, string>();
        await Parallel.ForEachAsync(
            unknown,
            new ParallelOptions { MaxDegreeOfParallelism = ResolveConcurrency },
            async (id, _) =>
            {
                // Only an answer is recorded. An id that came out of the discography listing exists by
                // construction, so a null here is a failed call rather than a real miss — memoising it
                // would pin the rest of a rate-limited walk to the listing artist for good.
                var name = (await _deezer.GetAlbum(id))?.artist?.name;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    learned[id] = name;
                }
            });

        if (!learned.IsEmpty)
        {
            await _albumArtists.Put(learned);
            foreach (var (id, name) in learned)
            {
                known[id] = name;
            }
        }

        return known;
    }

    private static string NormalizeTitle(string? title) => AlbumTitleMatcher.Normalize(title);
}
