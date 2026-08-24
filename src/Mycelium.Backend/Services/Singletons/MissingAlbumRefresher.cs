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
    private readonly ILogger<MissingAlbumRefresher> _logger;

    // A Deezer album id -> its album-artist name. The discography listing omits the album-artist, so
    // learning it costs one /album/{id} call per album; we memoise for the process lifetime (warms
    // across the daily sweep, re-warms on restart) so the extra calls stay bounded. Only answers are
    // stored — see ResolveAlbumArtist for why a null is left uncached.
    private readonly ConcurrentDictionary<long, string> _albumArtistCache = new();

    public MissingAlbumRefresher(
        IArtistCatalogRepo catalog,
        DeezerArtistResolver resolver,
        IDeezerApi deezer,
        IMissingAlbumRepo missing,
        IAlbumMatchOverrideRepo overrides,
        ILogger<MissingAlbumRefresher> logger)
    {
        _catalog = catalog;
        _resolver = resolver;
        _deezer = deezer;
        _missing = missing;
        _overrides = overrides;
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
        ArtistKey artist, IReadOnlyDictionary<string, HashSet<string>> ownedAlbums)
    {
        var diff = await FetchAndDiff(artist, ownedAlbums);
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
    /// the deluxe edition and the remaster are two entries here, even though they are one record for the
    /// purpose of owning it — so nothing in an artist's discography is hidden behind another edition of
    /// itself. Owned albums the library has that Deezer doesn't list at all are appended (without
    /// art/id/type) so the picture is complete.
    /// </summary>
    /// <exception cref="DeezerUnavailableException">
    /// Deezer didn't answer. Nothing is persisted and nothing is returned — the caller surfaces this
    /// as "Deezer is busy, retrying" rather than an authoritative empty discography, which the user
    /// would otherwise see (and the client would cache) as "this artist has no albums".
    /// </exception>
    public async Task<IReadOnlyList<DiscographyAlbum>> Discography(
        ArtistKey artist, IReadOnlyDictionary<string, HashSet<string>> ownedAlbums)
    {
        var diff = await FetchAndDiff(artist, ownedAlbums);
        await _missing.ReplaceForArtist(artist.ArtistName, diff?.Missing ?? new List<MissingAlbum>());

        var all = diff?.All.ToList() ?? new List<DiscographyAlbum>();
        var ownedAlbumTitles = ownedAlbums.TryGetValue(artist.ArtistName, out var ownedSet)
            ? (IEnumerable<string>)ownedSet
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
    /// Resolves the artist on Deezer, gathers their catalog (the discography listing, backfilled from
    /// album search — see <see cref="Backfill"/>), and walks it once, splitting it into the full
    /// annotated list (every listed record type and every pressing, flagged owned/missing) and the missing
    /// subset, each row tagged with its record type — and pressings after the first tagged as such — so
    /// the feed can decide for itself what to push. Returns null when the artist has no Deezer match.
    /// Ownership compares on a normalized title so neither punctuation/casing differences between Plex and
    /// Deezer (a typographic vs. straight apostrophe) nor edition decoration ("(Deluxe Edition)") makes an
    /// owned album look missing; the original Deezer title is still what we surface.
    /// </summary>
    /// <exception cref="DeezerUnavailableException">
    /// Deezer never answered — so nothing learned here is evidence of anything. Distinct from a null
    /// return ("Deezer answered: no such artist"), because the callers <em>persist</em> this diff:
    /// treating an unanswered call as an empty discography wipes the artist's missing-album rows and
    /// blanks their album list in the UI, which is exactly what a five-second quota blip used to do.
    /// </exception>
    private async Task<(List<DiscographyAlbum> All, List<MissingAlbum> Missing)?> FetchAndDiff(
        ArtistKey artist, IReadOnlyDictionary<string, HashSet<string>> ownedAlbums)
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

        // Normalized owned titles per artist name, computed lazily and memoised for this pass — so the
        // common (scanning artist) lookup and any album-artist lookup share the work.
        var normalizedOwned = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> OwnedTitlesFor(string artistName) =>
            normalizedOwned.TryGetValue(artistName, out var n)
                ? n
                : normalizedOwned[artistName] = (ownedAlbums.TryGetValue(artistName, out var set)
                    ? set.Select(NormalizeTitle)
                    : Enumerable.Empty<string>()).ToHashSet(StringComparer.Ordinal);

        var scannedOwned = OwnedTitlesFor(artist.ArtistName);

        // User-asserted merges — a release the diff would call missing that the user has confirmed is
        // already in the library under a near-miss title. Same keys the purchase reconcile builds.
        var overrideKeys = (await _overrides.GetAll())
            .Select(o => AlbumOverrideKey.For(o.MatchArtist, o.DeezerTitle))
            .ToHashSet();

        // Two keys per release, and the gap between them is the point. The record key (edition
        // decoration stripped) answers "do we have this album?" — Plex's "Both Sides" is what Deezer
        // lists as "Both Sides (Deluxe Edition)". The listing key keeps the decoration, so each pressing
        // Deezer lists stays a row of its own; only a title Deezer repeats verbatim is dropped as the
        // duplicate it is. Collapsing pressings here is what used to make an artist's discography hide
        // the 2015 remaster behind the deluxe edition.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Ownership resolved once per record rather than per pressing: every pressing of an album is
        // owned (or missing) together, and the album-artist call behind that verdict is the expensive
        // part — memoising keeps it at one call per record, as it was when pressings were collapsed.
        var verdicts = new Dictionary<string, (bool Owned, ArtistKey AlbumArtist)>(StringComparer.Ordinal);
        var all = new List<DiscographyAlbum>();
        var missing = new List<MissingAlbum>();
        foreach (var album in catalog)
        {
            var title = album.title;
            var record = NormalizeTitle(title);
            if (string.IsNullOrEmpty(record)
                || album.record_type is null
                || !ListedRecordTypes.Contains(album.record_type)
                || !seen.Add(AlbumTitleMatcher.NormalizeEdition(title)))
            {
                continue;
            }

            // The first pressing of a record carries the feed; the ones after it are browsable in the
            // discography and queueable from there, but never pushed at anyone (see
            // MissingAlbum.AlternatePressing) — one album shouldn't ask the same question twice.
            var alternatePressing = verdicts.TryGetValue(record, out var verdict);
            if (!alternatePressing)
            {
                verdicts[record] = verdict = await OwnershipOf(record, title, album.id);
            }

            all.Add(new DiscographyAlbum(
                title, album.BestCoverUrl, album.id, verdict.Owned, album.Year, album.record_type));
            if (!verdict.Owned)
            {
                missing.Add(new MissingAlbum(
                    artist, new AlbumKey(title), album.BestCoverUrl, album.id, verdict.AlbumArtist,
                    album.Year, album.record_type, alternatePressing));
            }
        }

        return (all, missing);

        // Whether the library already has this record, and the act it files it under.
        async Task<(bool Owned, ArtistKey AlbumArtist)> OwnershipOf(string record, string title, long albumId)
        {
            // Owned under the scanning artist (their own catalogued album) — the cheap, common case;
            // or a user-recorded merge into a near-miss library title under the scanning artist.
            if (scannedOwned.Contains(record)
                || overrideKeys.Contains(AlbumOverrideKey.For(artist.ArtistName, title)))
            {
                return (true, artist);
            }

            // Not in the scanning artist's owned set. It may be a collaboration the listing surfaces
            // via one member (e.g. a duo record) but the library files under the duo name. Resolve
            // the real album-artist so we can (a) tell whether Plex already has it under that name,
            // and (b) record it so reconcile later matches the act Plex filed it under. Bounded:
            // only the gap is resolved, and results are cached across the sweep.
            var resolved = await ResolveAlbumArtist(albumId);
            if (!string.IsNullOrWhiteSpace(resolved)
                && !string.Equals(resolved, artist.ArtistName, StringComparison.OrdinalIgnoreCase))
            {
                var albumArtist = new ArtistKey(resolved);
                return (OwnedTitlesFor(resolved).Contains(record)
                        || overrideKeys.Contains(AlbumOverrideKey.For(resolved, title)), albumArtist);
            }

            return (false, artist);
        }
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
    /// richer row (search results carry no release date, hence no year) and stays the pressing the feed
    /// offers.
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
    /// The album-artist Deezer credits a release to (the listing discography omits it). Cached for the
    /// process lifetime — one <c>/album/{id}</c> call per album ever seen. Returns null on a Deezer
    /// miss, which the caller treats as "no collaboration info" and falls back to the listing artist.
    ///
    /// An id that came out of the discography listing exists by construction, so a null answer here is
    /// a failed call, not a real miss — and it isn't cached. Memoising it would pin the whole rest of
    /// a rate-limited discography walk to the listing artist for the life of the process, long after
    /// Deezer started answering again.
    /// </summary>
    private async Task<string?> ResolveAlbumArtist(long albumId)
    {
        if (_albumArtistCache.TryGetValue(albumId, out var cached))
        {
            return cached;
        }

        var name = (await _deezer.GetAlbum(albumId))?.artist?.name;
        if (name is not null)
        {
            _albumArtistCache[albumId] = name;
        }
        return name;
    }

    private static string NormalizeTitle(string? title) => AlbumTitleMatcher.Normalize(title);
}
