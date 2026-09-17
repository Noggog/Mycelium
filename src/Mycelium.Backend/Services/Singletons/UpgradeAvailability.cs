using Mycelium.Backend.Services.Download;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// Confirms, before anybody is offered an upgrade, that Deezer actually holds something better than
/// the copy already on disk.
///
/// <para><b>Why this exists.</b> An upgrade candidate is derived entirely from our own side of the
/// question — the library has this album as MP3, and somebody here is entitled to FLAC — which says
/// nothing about whether a lossless master exists to fetch. Without a check the flow still reaches
/// the right answer, just expensively and in public: the album is recommended, a download slot is
/// spent, <see cref="DownloadFailure.NoBetterQualityAvailable"/> comes back, and
/// <c>DownloadService</c> writes the snooze this class writes up front. What that costs is a card
/// offering something undeliverable, which is the part worth removing.</para>
///
/// <para><b>Why a verdict may be absent.</b> The probe is entitlement-scoped and fails soft (see
/// <see cref="IDeezerQualityProbe"/>), so <see cref="DeezerQualityVerdict.Unknown"/> is a routine
/// answer, not an error path — no ARL configured, an expired one, an account without lossless, Deezer
/// unreachable. Every one of those resolves to <b>offer the album anyway</b>. That is deliberate and
/// is the single most important line in this class: unknown degrades to the behaviour that existed
/// before the pre-check, never to silence. Suppressing on an unknown would mean one expired cookie
/// quietly switching upgrades off library-wide with nothing failing to show for it.</para>
///
/// <para><b>Why the verdict is persisted rather than re-derived.</b> Two readers reach an upgrade
/// independently — the feed via <c>DiscoveryEngine</c> and the shared buy list via
/// <c>PurchaseService.Reconcile</c>, which derives from liked albums and owned quality without
/// consulting the missing-album rows at all. Dropping a row here would only silence the card while
/// reconcile went on wanting the album. So a confirmed miss is written to the same place a
/// discovered one goes — <see cref="AlbumBlockScope.Upgrade"/> with a <c>RetryAfter</c> — which both
/// readers already consult. It also means one probe per album ever rather than one per sweep.</para>
/// </summary>
public class UpgradeAvailability
{
    /// <summary>
    /// How long a confirmed "Deezer has nothing better" stands before the album is reconsidered.
    /// Matches the stamp <c>DownloadService</c> writes on the same finding, because it is the same
    /// finding reached earlier — a catalogue can gain a lossless master, so neither is permanent.
    /// </summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromDays(180);

    /// <summary>
    /// How long the set of existing upgrade verdicts is reused. Long enough that a sweep over every
    /// owned artist reads it once instead of once per artist, short enough that a verdict lifted in
    /// the UI is honoured in seconds.
    /// </summary>
    private static readonly TimeSpan VerdictCacheLifetime = TimeSpan.FromSeconds(60);

    private readonly IDeezerQualityProbe _probe;
    private readonly StreamripArlStore _arl;
    private readonly IAlbumBlockRepo _blocks;
    private readonly ILogger<UpgradeAvailability> _logger;

    private readonly SemaphoreSlim _verdictGate = new(1, 1);
    private HashSet<string>? _decided;
    private DateTimeOffset _decidedAt = DateTimeOffset.MinValue;

    public UpgradeAvailability(
        IDeezerQualityProbe probe,
        StreamripArlStore arl,
        IAlbumBlockRepo blocks,
        ILogger<UpgradeAvailability> logger)
    {
        _probe = probe;
        _arl = arl;
        _blocks = blocks;
        _logger = logger;
    }

    /// <summary>
    /// Filters <paramref name="rows"/> down to the ones still worth offering, probing each upgrade
    /// candidate and recording the misses. Gaps (<see cref="MissingAlbum.IsUpgrade"/> false) pass
    /// through untouched — this is only ever asked about replacing a record we hold.
    /// </summary>
    public async Task<IReadOnlyList<MissingAlbum>> Confirm(IReadOnlyList<MissingAlbum> rows)
    {
        var candidates = rows.Where(r => r.IsUpgrade).ToList();
        if (candidates.Count == 0)
        {
            return rows;
        }

        // Read once, before any probing: the ARL can only be absent or present for the whole batch,
        // and an absent one means there is nothing to do but pass everything through.
        var arl = _arl.Read();
        if (string.IsNullOrWhiteSpace(arl))
        {
            _logger.LogDebug(
                "No Deezer credential configured, so {Count} upgrade candidate(s) are offered unchecked",
                candidates.Count);
            return rows;
        }

        var decided = await DecidedKeys();
        var dropped = new HashSet<MissingAlbum>();

        foreach (var row in candidates)
        {
            // Already carries a verdict — from a previous probe or from a download that found out the
            // hard way. The readers filter on it themselves, so this is purely about not re-probing.
            if (Keys(row).Any(decided.Contains))
            {
                continue;
            }

            var quality = await _probe.Probe(arl, row.DeezerAlbumId);
            switch (quality.Verdict)
            {
                case DeezerQualityVerdict.LossyOnly:
                    await Record(row);
                    dropped.Add(row);
                    break;

                case DeezerQualityVerdict.LosslessAvailable when !WouldImprove(quality, row.OwnedQuality):
                    // Deezer has *some* lossless here, just not enough of it. Offering this would spend
                    // a slot on a download that UpgradeSwap then refuses, which is the exact waste the
                    // pre-check exists to remove — so it counts as "nothing better available".
                    _logger.LogInformation(
                        "Deezer has only {Lossless} of {Total} tracks lossless for \"{Album}\" ({Artist}), "
                        + "which would not come out better than the copy held",
                        quality.LosslessTracks, quality.TrackCount, row.Album.AlbumName,
                        row.Artist.ArtistName);
                    await Record(row);
                    dropped.Add(row);
                    break;

                case DeezerQualityVerdict.LosslessAvailable:
                    // Logged at debug because it is the ordinary outcome, but the partial case is worth
                    // being able to see: a 10-of-12 album is still offered, and the fallback ladder
                    // fills the rest, which is how the library's mixed-codec albums came to exist.
                    if (quality.LosslessTracks < quality.TrackCount)
                    {
                        _logger.LogDebug(
                            "Deezer has {Lossless} of {Total} tracks lossless for \"{Album}\" ({Artist}); "
                            + "offering the upgrade — the ladder fills the remainder",
                            quality.LosslessTracks, quality.TrackCount, row.Album.AlbumName,
                            row.Artist.ArtistName);
                    }
                    break;

                case DeezerQualityVerdict.Unknown:
                default:
                    // The load-bearing case. Nothing is recorded, so the album is offered exactly as it
                    // would have been without this class, and the next sweep asks again.
                    break;
            }
        }

        return dropped.Count == 0 ? rows : rows.Where(r => !dropped.Contains(r)).ToList();
    }

    /// <summary>
    /// Whether downloading this album would actually come out better than the copy on disk.
    ///
    /// <para>"Deezer has a FLAC" is not the same question. The ladder fills whatever Deezer can't
    /// supply lossless with 320, so the album that lands is <c>LosslessTracks</c> FLAC plus the rest
    /// MP3 — and what that album then <em>reads</em> as is <see cref="AudioQualityTier.Majority"/>,
    /// the same rule the library is tiered by and the same one <c>UpgradeSwap</c>'s strictly-better
    /// gate applies before it will promote anything. Two lossless tracks in twelve comes out lossy,
    /// loses that gate, and leaves the MP3 exactly where it was.</para>
    ///
    /// <para>Observed live rather than reasoned about: of the first two albums probed against the real
    /// gateway, one was 14/14 and the other 2/12. Predicting the gate here is the difference between
    /// the second one costing a download slot and costing nothing.</para>
    ///
    /// <para>Deliberately reuses <see cref="AudioQualityTier.Majority"/> rather than restating the
    /// arithmetic: one definition already serves labelling an album and deciding a swap is worth it,
    /// and a second copy here would be free to drift from both.</para>
    /// </summary>
    private static bool WouldImprove(DeezerAlbumQuality quality, AudioQuality? owned)
    {
        var lossy = quality.TrackCount - quality.LosslessTracks;
        var landed = AudioQualityTier.Majority(
            Enumerable.Repeat((AudioQuality?)AudioQuality.Lossless, quality.LosslessTracks)
                .Concat(Enumerable.Repeat((AudioQuality?)AudioQuality.Lossy, lossy)));

        // Lifted comparison, as everywhere else: an unknown on either side is never an improvement.
        return owned < landed;
    }

    /// <summary>
    /// Writes the confirmed miss under both acts the album can be filed as — the artist whose
    /// discography surfaced it and the one Deezer credits it to — so a collaboration reachable through
    /// either member isn't re-offered through the other. Same shape as the stamp
    /// <c>DownloadService</c> writes, so the two are indistinguishable to every reader.
    /// </summary>
    private async Task Record(MissingAlbum row)
    {
        var until = DateTimeOffset.UtcNow.Add(RetryAfter);
        foreach (var act in Acts(row))
        {
            await _blocks.Add(new AlbumBlock(
                act, row.Album.AlbumName, BlockedBy: null, AlbumBlockScope.Upgrade, until));
        }

        // Cheap to keep the memo honest, and it stops a second artist's pass in the same sweep from
        // re-probing an album that both of them list.
        foreach (var key in Keys(row))
        {
            _decided?.Add(key);
        }

        _logger.LogInformation(
            "Deezer has no lossless copy of \"{Album}\" ({Artist}); not offering the upgrade until {Until:d}",
            row.Album.AlbumName, row.Artist.ArtistName, until);
    }

    /// <summary>
    /// The keys of every album currently carrying an upgrade verdict. Deliberately the same read
    /// <c>DiscoveryEngine.UpgradeSkippedKeys</c> and <c>PurchaseService.UpgradeSkippedKeys</c> do,
    /// expiry included, so a lapsed snooze becomes a probe candidate again at the same moment it
    /// becomes a feed candidate again.
    /// </summary>
    private async Task<HashSet<string>> DecidedKeys()
    {
        await _verdictGate.WaitAsync();
        try
        {
            if (_decided is { } cached && DateTimeOffset.UtcNow - _decidedAt < VerdictCacheLifetime)
            {
                return cached;
            }

            var now = DateTimeOffset.UtcNow;
            _decided = (await _blocks.GetAll())
                .Where(b => b.Scope == AlbumBlockScope.Upgrade && b.AppliesAt(now))
                .Select(b => AlbumOverrideKey.For(b.Artist, b.Album))
                .ToHashSet();
            _decidedAt = now;
            return _decided;
        }
        finally
        {
            _verdictGate.Release();
        }
    }

    private static IEnumerable<string> Acts(MissingAlbum row) =>
        new[] { row.Artist.ArtistName, row.MatchArtist.ArtistName }
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Keys(MissingAlbum row) =>
        Acts(row).Select(a => AlbumOverrideKey.For(a, row.Album.AlbumName));
}
