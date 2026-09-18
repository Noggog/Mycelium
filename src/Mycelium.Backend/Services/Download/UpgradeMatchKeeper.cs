using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Download;

/// <summary>
/// Keeps an upgraded album matched to the same release as the copy it replaced, so its star ratings
/// survive the swap.
///
/// <para>Plex stores ratings against what an item is <em>matched</em> to (<c>plex://album/…</c> and the
/// tracks under it), not against the file or the rating key. A FLAC copy fetched from Deezer is often
/// a remaster or reissue, and if Plex matches it to that release rather than the one the old copy had,
/// every rating on the album appears to vanish — only to come back when the album is rematched by
/// hand. This does that rematch: <see cref="Remember"/> saves the old match before any file moves, and
/// <see cref="CheckPending"/> compares it against the new copy once Plex has it.</para>
/// </summary>
public class UpgradeMatchKeeper
{
    /// <summary>Only a real agent match carries ratings worth keeping; <c>local://</c> means "unmatched".</summary>
    private const string AgentMatchPrefix = "plex://";

    private readonly ILibraryQuery _library;
    private readonly ILibraryMatcher _matcher;
    private readonly IArtistCatalogRepo _catalog;
    private readonly IPurchaseRepo _purchases;
    private readonly DownloaderConfig _config;
    private readonly ILogger<UpgradeMatchKeeper> _logger;

    public UpgradeMatchKeeper(
        ILibraryQuery library,
        ILibraryMatcher matcher,
        IArtistCatalogRepo catalog,
        IPurchaseRepo purchases,
        DownloaderConfig config,
        ILogger<UpgradeMatchKeeper> logger)
    {
        _library = library;
        _matcher = matcher;
        _catalog = catalog;
        _purchases = purchases;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// The report to save before the old copy's files move: what it is matched to, so a new copy that
    /// Plex matches to a different release can be put back once it lands. Saved before the move so the
    /// answer is on the row even if the process dies between the swap and Plex's rescan.
    /// </summary>
    public async Task<UpgradeReport> Remember(PurchaseItem item, int albumRatingKey)
    {
        var match = await _library.QueryAlbumMatch(albumRatingKey);
        var now = DateTimeOffset.UtcNow;
        UpgradeReport report;
        if (match is null || !match.StartsWith(AgentMatchPrefix, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "{Artist} — {Album} isn't matched in Plex ({Match}); no match to carry across the upgrade",
                item.Artist.ArtistName, item.Album, match ?? "no match");
            report = new UpgradeReport(now, Match: UpgradeMatchCheck.NotMatched, OldMatch: match);
        }
        else
        {
            report = new UpgradeReport(
                now, Match: UpgradeMatchCheck.Waiting, OldMatch: match, MatchCheckSince: now);
        }

        await _purchases.SetUpgrade(item.Id, report);
        return report;
    }

    /// <summary>Whether this row is an upgrade still waiting for its match to be checked.</summary>
    public static bool IsPending(PurchaseItem item) =>
        item.Kind == FeedKind.UpgradeAlbum && item.Upgrade?.Match == UpgradeMatchCheck.Waiting;

    /// <summary>
    /// Starts checking a finished upgrade's match again — after a Fix Match by hand, say, or to give a
    /// rematch that didn't take another go — and runs the first check straight away. Returns false when
    /// the row isn't an upgrade with a saved match to check against.
    /// </summary>
    public async Task<bool> Recheck(string id)
    {
        var item = (await _purchases.GetAll()).FirstOrDefault(p => p.Id == id);
        if (item?.Kind != FeedKind.UpgradeAlbum || item.Upgrade?.OldMatch is not { } old
            || !old.StartsWith(AgentMatchPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var restarted = item with
        {
            Upgrade = item.Upgrade with
            {
                Match = UpgradeMatchCheck.Waiting, MatchCheckSince = DateTimeOffset.UtcNow,
            },
        };
        await _purchases.SetUpgrade(id, restarted.Upgrade);
        await Check(restarted);
        return true;
    }

    /// <summary>
    /// Checks every upgrade whose new copy may have landed, rematching any that Plex matched to a
    /// different release. An upgrade that hasn't shown up yet is left for the next pass; one still
    /// unresolved after the settle window is marked as needing a Fix Match by hand.
    /// </summary>
    public async Task CheckPending()
    {
        foreach (var item in (await _purchases.GetAll()).Where(IsPending))
        {
            try
            {
                await Check(item);
            }
            catch (Exception ex)
            {
                // One album's Plex hiccup must not stop the rest being checked; it retries next pass.
                _logger.LogWarning(ex, "Could not check the Plex match of the upgraded {Artist} — {Album}",
                    item.Artist.ArtistName, item.Album);
            }
        }
    }

    private async Task Check(PurchaseItem item)
    {
        var report = item.Upgrade!;
        var wanted = report.OldMatch!;
        string? current = null;
        var key = await AlbumRatingKey(_catalog, item);
        if (key is { } albumKey && await IsTheNewCopy(item, albumKey))
        {
            current = await _library.QueryAlbumMatch(albumKey);
            if (current == wanted)
            {
                _logger.LogInformation(
                    "Upgraded {Artist} — {Album} kept its Plex match, so its ratings carried over",
                    item.Artist.ArtistName, item.Album);
                await _purchases.SetUpgrade(item.Id, report with { Match = UpgradeMatchCheck.Kept });
                return;
            }

            await _matcher.RematchAlbum(albumKey, wanted, item.Album ?? "");
            if (await _library.QueryAlbumMatch(albumKey) == wanted)
            {
                _logger.LogInformation(
                    "Plex matched the upgraded {Artist} — {Album} to {Current}; rematched it to {Wanted}, "
                    + "the release the old copy had, which is where its ratings are",
                    item.Artist.ArtistName, item.Album, current ?? "nothing", wanted);
                await _purchases.SetUpgrade(
                    item.Id, report with { Match = UpgradeMatchCheck.Rematched, NewMatch = current });
                return;
            }

            // Remember what Plex picked even while still retrying, so the page can say so.
            if (current != report.NewMatch)
            {
                report = report with { NewMatch = current };
                await _purchases.SetUpgrade(item.Id, report);
            }
        }

        if ((report.MatchCheckSince ?? report.At) + _config.SettleWindow < DateTimeOffset.UtcNow)
        {
            _logger.LogWarning(
                "Gave up confirming the Plex match of the upgraded {Artist} — {Album}. If its ratings "
                + "are missing, use Fix Match in Plex and pick the release matching {Wanted}",
                item.Artist.ArtistName, item.Album, wanted);
            await _purchases.SetUpgrade(item.Id, report with { Match = UpgradeMatchCheck.NeedsFixMatch });
        }
    }

    /// <summary>
    /// Whether the album Plex lists now is the upgrade rather than the old copy it hasn't rescanned
    /// away yet. Checking the old copy's match would "confirm" it trivially and stop checking before
    /// the new copy has even been matched.
    /// </summary>
    private async Task<bool> IsTheNewCopy(PurchaseItem item, int albumKey)
    {
        var quality = (await _library.QueryAlbumQuality(new[] { albumKey }))
            .GetValueOrDefault(albumKey);
        return (item.Upgrade?.ReplacedQuality ?? item.OwnedQuality) < quality;
    }

    /// <summary>
    /// The album's Plex rating key, looked up under the act the library files it under (which for a
    /// collaboration differs from the artist whose discography surfaced it). Null when it isn't listed.
    /// </summary>
    internal static async Task<int?> AlbumRatingKey(IArtistCatalogRepo catalog, PurchaseItem item)
    {
        var acts = new[] { item.AlbumArtist ?? item.Artist.ArtistName, item.Artist.ArtistName };
        var keys = await catalog.GetAlbumPlexRatingKeys(acts);

        // The stored titles are Plex's; the row's is Deezer's. Match the way ownership does — at record
        // granularity, since the copy is filed under whatever name Plex gave it, decoration and all.
        var wanted = AlbumTitleMatcher.NormalizeRecord(item.Album ?? "");
        foreach (var act in acts)
        {
            if (keys.TryGetValue(act, out var byTitle))
            {
                var match = byTitle.FirstOrDefault(kv => AlbumTitleMatcher.NormalizeRecord(kv.Key) == wanted);
                if (match.Value != 0)
                {
                    return match.Value;
                }
            }
        }
        return null;
    }
}
