using System.Text.Json.Serialization;

namespace Mycelium.Interfaces;

/// <summary>
/// How good a copy of an album is, on a single ordered scale. Ordered so callers can ask the only
/// question that matters — "is what we have at least as good as what we want?" — with a plain
/// comparison.
///
/// <para><b>"Don't know" is <c>null</c>, never a member of this enum.</b> C#'s lifted comparison
/// makes <c>null &lt; AudioQuality.Lossless</c> evaluate to <c>false</c>, so an album whose quality
/// hasn't been determined is never mistaken for one that needs upgrading — safe by default at every
/// comparison site, with no guard clause to forget. An <c>Unknown = 0</c> member would do the
/// opposite: sorting below <see cref="Lossy"/>, it would make every album we haven't inspected look
/// upgradeable to everyone.</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioQuality
{
    /// <summary>Compressed with loss — MP3, AAC, Vorbis, Opus, at any bitrate.</summary>
    Lossy = 1,

    /// <summary>Bit-perfect — FLAC, ALAC, WAV, AIFF. What Deezer serves is 16-bit/44.1kHz.</summary>
    Lossless = 2,
}

/// <summary>
/// The one place this app's quality vocabulary meets streamrip's and Plex's. Kept together so the
/// mappings can't drift apart in separate files.
/// </summary>
public static class AudioQualityTier
{
    // Plex reports the codec of each track's media. Everything not named here is lossy: the list of
    // lossless codecs is short and closed, while the lossy one grows with every new encoder.
    private static readonly HashSet<string> LosslessCodecs =
        new(StringComparer.OrdinalIgnoreCase) { "flac", "alac", "wav", "aiff", "aif", "pcm", "ape", "wv" };

    /// <summary>
    /// streamrip's <c>--quality</c> value for a tier: 2 = FLAC, 1 = 320kbps MP3 (0 = 128kbps, which
    /// nothing targets deliberately — it's only ever reached by walking the fallback ladder down).
    /// </summary>
    public static string ToStreamripQuality(this AudioQuality quality) =>
        quality switch
        {
            AudioQuality.Lossless => "2",
            AudioQuality.Lossy => "1",
            _ => "1",
        };

    /// <summary>
    /// The tier a Plex track's codec represents, or null when Plex reported no codec at all (which
    /// is "we don't know", not "it's bad" — see the enum's remarks).
    /// </summary>
    public static AudioQuality? FromCodec(string? codec) =>
        string.IsNullOrWhiteSpace(codec)
            ? null
            : LosslessCodecs.Contains(codec) ? AudioQuality.Lossless : AudioQuality.Lossy;

    /// <summary>
    /// An album's tier, taken as the <b>majority</b> of its tracks rather than the worst of them —
    /// ties to lossless, and tracks of unknown codec siding with lossless.
    ///
    /// <para>Deezer's catalogue has per-track gaps, so the downloader's fallback ladder routinely
    /// produces an album that is lossless except for a track or two it could only get at 320. Judged
    /// on its worst track such an album reads as lossy and would be offered for upgrade forever;
    /// judged on its majority it reads as what it plainly is. Measured against the live library this
    /// is the difference between 7,436 and 7,466 lossless albums — 30 albums of the form "20 FLAC +
    /// 1 MP3" — while still correctly calling a 1-FLAC/19-MP3 album lossy.</para>
    ///
    /// <para>Returns null for an empty track list: no evidence is not a verdict.</para>
    /// </summary>
    public static AudioQuality? Majority(IEnumerable<AudioQuality?> trackQualities)
    {
        var lossless = 0;
        var lossy = 0;
        foreach (var quality in trackQualities)
        {
            // An unknown-codec track sides with lossless so a single unreadable track can't drag an
            // otherwise-lossless album down into the upgrade queue.
            if (quality == AudioQuality.Lossy)
            {
                lossy++;
            }
            else
            {
                lossless++;
            }
        }

        if (lossless + lossy == 0)
        {
            return null;
        }
        return lossy > lossless ? AudioQuality.Lossy : AudioQuality.Lossless;
    }

    /// <summary>
    /// Parses a stored/configured tier name, case-insensitively. Null for anything unrecognised —
    /// including a blank — so a typo in configuration reads as "unset" and the caller's own default
    /// applies, rather than silently pinning everyone to one end of the scale.
    ///
    /// <para>Digits are rejected outright. <see cref="Enum.TryParse{T}(string, bool, out T)"/> happily
    /// accepts the underlying number, so "2" would come back as <see cref="AudioQuality.Lossless"/> —
    /// which is streamrip's vocabulary leaking into ours. The two scales coincide today by accident
    /// and reading one as the other would stop being harmless the moment a tier is inserted.</para>
    /// </summary>
    public static AudioQuality? Parse(string? raw) =>
        !string.IsNullOrWhiteSpace(raw)
        && !raw.Any(char.IsDigit)
        && Enum.TryParse<AudioQuality>(raw, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : null;
}
