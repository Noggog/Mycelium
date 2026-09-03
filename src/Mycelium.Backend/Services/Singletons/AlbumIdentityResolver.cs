using Mycelium.Interfaces;
using Mycelium.ListenBrainz.Services;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>How one backfill pass went.</summary>
/// <param name="Resolved">Albums that gained a release-group MBID.</param>
/// <param name="Missed">Albums MusicBrainz had nothing for. Recorded, so they aren't asked about again.</param>
public record AlbumIdentityResult(int Resolved, int Missed)
{
    public int Attempted => Resolved + Missed;
}

/// <summary>
/// Resolves owned albums to MusicBrainz release-group MBIDs, a slice at a time.
///
/// <para><b>Why.</b> Artists in the archive carry an MBID and albums carry only a title, which is the
/// one identifier in the whole record most likely to drift: editions get renamed, remasters gain
/// suffixes, and two acts can both have a <i>Greatest Hits</i>. An MBID is the only identifier here
/// that is stable forever, and it is what lets a future reader re-key an album whose title has moved
/// — the same argument that already justifies keeping the artist's.</para>
///
/// <para><b>Why it drips.</b> MusicBrainz's published limit is one request a second, which the client
/// enforces for us. A library of any size is therefore hours of lookups, so this is a backfill that
/// converges over days rather than a sweep that completes — bounded per pass, and picking up where it
/// left off because a gap is defined as "not asked about yet" rather than by any cursor.</para>
///
/// <para><b>Why a miss is written down.</b> MusicBrainz genuinely does not have some records —
/// bootlegs, DJ mixes, a library's own compilations. Left unrecorded, those would be re-asked every
/// pass for ever and the backfill would never reach the albums behind them.</para>
/// </summary>
public class AlbumIdentityResolver
{
    private readonly IArtistCatalogRepo _catalog;
    private readonly IMusicBrainzApi _musicBrainz;
    private readonly ILogger<AlbumIdentityResolver> _logger;

    public AlbumIdentityResolver(
        IArtistCatalogRepo catalog,
        IMusicBrainzApi musicBrainz,
        ILogger<AlbumIdentityResolver> logger)
    {
        _catalog = catalog;
        _musicBrainz = musicBrainz;
        _logger = logger;
    }

    /// <summary>
    /// Resolves up to <paramref name="limit"/> albums. Public so it can be unit-tested and triggered
    /// without the timer.
    /// </summary>
    public async Task<AlbumIdentityResult> ResolveSome(int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return new AlbumIdentityResult(0, 0);
        }

        AlbumIdentityGap[] gaps;
        try
        {
            gaps = await _catalog.GetAlbumsWithoutReleaseGroup(limit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Album identity backfill could not read the catalog; will retry next pass");
            return new AlbumIdentityResult(0, 0);
        }

        var resolved = 0;
        var missed = 0;

        foreach (var gap in gaps)
        {
            foreach (var album in gap.Albums)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // A shutdown mid-pass costs the remainder of this slice and nothing else: every
                    // album already answered is written, and the rest are still gaps next time.
                    return Done(resolved, missed);
                }

                try
                {
                    var group = await _musicBrainz.SearchReleaseGroup(gap.ArtistMbid, album);
                    await _catalog.SetAlbumReleaseGroup(gap.Artist, album, group?.Id);

                    if (group?.Id is not null)
                    {
                        resolved++;
                    }
                    else
                    {
                        missed++;
                    }
                }
                catch (Exception ex)
                {
                    // Deliberately *not* recorded as a miss: a transport failure says nothing about
                    // whether MusicBrainz has the record, and writing one down would retire the album
                    // from the backfill on the strength of a network blip.
                    _logger.LogWarning(
                        ex, "Album identity lookup failed for \"{Album}\" ({Artist}); leaving it for a later pass",
                        album, gap.Artist);
                }
            }
        }

        return Done(resolved, missed);
    }

    private AlbumIdentityResult Done(int resolved, int missed)
    {
        var result = new AlbumIdentityResult(resolved, missed);
        if (result.Attempted > 0)
        {
            _logger.LogInformation(
                "Album identity backfill: {Resolved} resolved, {Missed} not in MusicBrainz ({Attempted} looked up)",
                resolved, missed, result.Attempted);
        }

        return result;
    }
}
