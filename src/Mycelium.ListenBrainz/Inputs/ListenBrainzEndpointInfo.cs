namespace Mycelium.ListenBrainz.Inputs;

/// <summary>
/// Connection settings for the open MetaBrainz stack this source spans two services of:
///   * <paramref name="MusicBrainzBaseUri"/> — name -> MBID resolution (/ws/2/artist search).
///   * <paramref name="ListenBrainzBaseUri"/> — MBID -> similar artists (labs similar-artists/json).
/// Both are keyless. MusicBrainz *requires* a descriptive User-Agent with contact info or it
/// throttles hard, hence <paramref name="Contact"/>. <paramref name="Algorithm"/> is the labs
/// tuning string (session window, limit, ...); <paramref name="Enabled"/> is the off-switch so the
/// source can be disabled without touching code (it then never hits the network).
/// <paramref name="MusicBrainzMinIntervalOverride"/> replaces the spacing between MusicBrainz requests
/// — only worth setting against a self-hosted mirror, which has no rate limit to respect.
/// </summary>
public record ListenBrainzEndpointInfo(
    string MusicBrainzBaseUri,
    string ListenBrainzBaseUri,
    string Contact,
    string Algorithm,
    bool Enabled,
    TimeSpan? MusicBrainzMinIntervalOverride = null)
{
    /// <summary>MusicBrainz's published limit is ~1 req/s; the default pads slightly to stay safely under.</summary>
    public static readonly TimeSpan DefaultMusicBrainzMinInterval = TimeSpan.FromMilliseconds(1100);

    /// <summary>The least time between the starts of two MusicBrainz requests.</summary>
    public TimeSpan MusicBrainzMinInterval => MusicBrainzMinIntervalOverride ?? DefaultMusicBrainzMinInterval;

    /// <summary>
    /// User-Agent sent to both services. MusicBrainz's etiquette asks for "App/version ( contact )"
    /// so a maintainer is reachable; we send the same string to ListenBrainz to be a good citizen.
    /// </summary>
    public string UserAgent => $"Mycelium/1.0 ( {Contact} )";
}
