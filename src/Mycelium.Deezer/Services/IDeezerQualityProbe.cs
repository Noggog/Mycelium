namespace Mycelium.Deezer.Services;

/// <summary>
/// What Deezer turned out to hold for one album, as far as we are entitled to see.
///
/// <para>Three states rather than a bool, because the interesting failure is not an error. Deezer's
/// per-track <c>FILESIZE_FLAC</c> is scoped to the asking account: a session with no lossless
/// entitlement is answered <c>0</c> for every track of an album that certainly has FLAC. That comes
/// back as a well-formed 200, indistinguishable from a genuine "no lossless master exists" unless
/// something else settles it. So a zero is only allowed to mean <see cref="LossyOnly"/> when the
/// session that read it could have seen a non-zero; otherwise it is <see cref="Unknown"/>.</para>
/// </summary>
public enum DeezerQualityVerdict
{
    /// <summary>
    /// No answer we may act on — no credential, a dead one, an account without lossless, a transport
    /// failure, or a response shape we don't recognise. The caller must treat this as "ask again
    /// later", never as a finding: recording it would write off an album on the strength of an
    /// expired cookie.
    /// </summary>
    Unknown,

    /// <summary>At least one track is available lossless, so an upgrade has something to fetch.</summary>
    LosslessAvailable,

    /// <summary>
    /// Positively established: a session that <em>can</em> see lossless looked at this album and every
    /// track came back without a FLAC size. Deezer has nothing better than a lossy copy here.
    /// </summary>
    LossyOnly,
}

/// <summary>
/// One album's availability. <paramref name="TrackCount"/> and <paramref name="LosslessTracks"/> are
/// reported for the partial case — Deezer's catalogue is patchy per track, and "10 of 12 lossless" is
/// still a real upgrade over 12 MP3 once the fallback ladder fills the gap (see QUALITY-TIERS.md).
/// Both are 0 when the verdict is <see cref="DeezerQualityVerdict.Unknown"/>.
/// </summary>
public record DeezerAlbumQuality(
    DeezerQualityVerdict Verdict, int TrackCount = 0, int LosslessTracks = 0)
{
    public static readonly DeezerAlbumQuality Unknown = new(DeezerQualityVerdict.Unknown);
}

/// <summary>
/// Asks Deezer whether an album is actually available in better than the copy the library holds,
/// before anyone is offered the upgrade.
///
/// <para>This has to go through the private gateway: the public API carries no quality information at
/// all — <c>/album/{id}</c> reports <c>available</c>, <c>nb_tracks</c> and <c>duration</c>, and
/// nothing about formats — so there is no keyless route to the answer. The gateway's
/// <c>deezer.pageAlbum</c> returns a per-track <c>FILESIZE_FLAC</c>, which is the answer exactly.</para>
///
/// <para>Second use of the gateway, after <see cref="IDeezerSessionCheck"/>, and kept to the same
/// terms: read-only, one narrow interface, no media touched. It differs in being on a per-album path
/// rather than a one-off, which is why it paces itself and reuses a session rather than minting one
/// per call.</para>
/// </summary>
public interface IDeezerQualityProbe
{
    /// <summary>
    /// Whether <paramref name="albumId"/> can be had in lossless, using <paramref name="arl"/> to ask.
    /// Never throws and never guesses: everything that isn't a confident reading from an entitled
    /// session comes back <see cref="DeezerQualityVerdict.Unknown"/>.
    /// </summary>
    Task<DeezerAlbumQuality> Probe(string? arl, long albumId);
}
