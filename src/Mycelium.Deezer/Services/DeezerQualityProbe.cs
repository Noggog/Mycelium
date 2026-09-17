using System.Net;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Mycelium.Deezer.Services;

/// <summary>
/// Reads an album's available formats off Deezer's internal gateway (<c>deezer.pageAlbum</c>), so an
/// upgrade can be confirmed before it is offered rather than discovered by a download that fails.
///
/// <para><b>Why the answer is entitlement-scoped, and why that is the whole design.</b> The gateway
/// reports <c>FILESIZE_FLAC</c> per track, but sized for the asking account. A guest session — and an
/// expired ARL is a guest session, since the gateway answers <c>USER_ID: 0</c> and carries on rather
/// than refusing — reports <c>FILESIZE_FLAC: 0</c> and <c>FILESIZE_MP3_320: 0</c> for every track of
/// an album that plainly has both. Verified against Daft Punk's <i>Discovery</i>, which returns
/// fourteen tracks of zeroes to a session with no entitlement.</para>
///
/// <para>So the dangerous outcome here is not an exception, it is a confident wrong "no". The
/// asymmetry is stark: a false <i>yes</i> costs one download slot and self-corrects when the download
/// reports <c>NoBetterQualityAvailable</c>; a false <i>no</i> writes a 180-day snooze over an album
/// that was upgradeable all along, silently, and one dead credential would do it to the entire
/// library at once. Hence <see cref="DeezerQualityVerdict.LossyOnly"/> is only ever returned from a
/// session that positively reported <c>web_lossless</c> — the same flag
/// <see cref="DeezerSessionCheck"/> already reads. Everything else degrades to
/// <see cref="DeezerQualityVerdict.Unknown"/>, which callers are required to treat as "carry on as if
/// there were no probe".</para>
/// </summary>
public class DeezerQualityProbe : IDeezerQualityProbe
{
    private const string GatewayBase = "https://www.deezer.com/ajax/gw-light.php?api_version=1.0&input=3";

    // The gateway answers HTML rather than JSON without a browser-shaped User-Agent — load-bearing,
    // not cosmetic. Same string as DeezerSessionCheck, for the same reason.
    private const string UserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a minted gateway session is reused. The token outlives this comfortably; the cap is
    /// here so a credential replaced in the middle of a sweep is picked up without a restart, and so a
    /// session that has gone stale in some way we don't model is re-minted on its own.
    /// </summary>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Minimum spacing between gateway calls. This is an undocumented private endpoint sitting behind
    /// bot protection, and the thing that would draw attention is volume, not existence — so probes
    /// are serialised and paced rather than run at whatever rate the sweep can drive them. At one
    /// verdict per album *ever* (the caller persists what it learns) the sweep is a few dozen calls a
    /// night in steady state, so the pacing costs nothing real.
    /// </summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// How long a failed mint is remembered before trying again.
    ///
    /// <para>Without this, a dead ARL is the worst case rather than a quiet one: a mint that fails
    /// leaves nothing cached, so every remaining candidate in the sweep would mint again, turning an
    /// expired credential into a burst of gateway logins — the opposite of the pacing above, and on
    /// the one call that looks least like browsing. The verdict is unaffected either way (no session
    /// means <see cref="DeezerQualityVerdict.Unknown"/> means the album is offered anyway), so this
    /// costs nothing but the delay before a freshly pasted ARL is picked up mid-sweep.</para>
    /// </summary>
    private static readonly TimeSpan MintRetryAfter = TimeSpan.FromMinutes(10);

    private readonly ILogger<DeezerQualityProbe> _logger;

    // One gateway conversation at a time: the pacing below is only meaningful if calls can't overlap,
    // and it keeps session minting from racing with itself.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastCall = DateTimeOffset.MinValue;
    private Session? _session;
    private string? _mintFailedArl;
    private DateTimeOffset _mintFailedAt = DateTimeOffset.MinValue;

    public DeezerQualityProbe(ILogger<DeezerQualityProbe> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// A minted gateway session: the client holding its cookie jar, the API token it was issued, and
    /// whether the account behind it may see lossless at all. The jar matters — <c>getUserData</c>
    /// hands back a <c>sid</c> that the subsequent calls are expected to carry, so the ARL is added to
    /// the container rather than pinned to each request as a raw header.
    /// </summary>
    private sealed record Session(
        string Arl, string Token, HttpClient Http, bool Lossless, DateTimeOffset Minted);

    public async Task<DeezerAlbumQuality> Probe(string? arl, long albumId)
    {
        if (string.IsNullOrWhiteSpace(arl))
        {
            return DeezerAlbumQuality.Unknown;
        }

        await _gate.WaitAsync();
        try
        {
            var session = await EnsureSession(arl.Trim());
            if (session is null)
            {
                return DeezerAlbumQuality.Unknown;
            }

            var page = await Call(session, "deezer.pageAlbum", session.Token, new JObject
            {
                ["alb_id"] = albumId.ToString(),
                ["lang"] = "en",
                ["tab"] = 0,
            });

            // A token can lapse mid-sweep. Re-mint once and retry before giving up, so a long sweep
            // doesn't return Unknown for every album after the first expiry.
            if (page is null)
            {
                // Force the next EnsureSession to re-mint (and to dispose this one's client).
                _session = session with { Minted = DateTimeOffset.MinValue };
                session = await EnsureSession(arl.Trim());
                if (session is null)
                {
                    return DeezerAlbumQuality.Unknown;
                }
                page = await Call(session, "deezer.pageAlbum", session.Token, new JObject
                {
                    ["alb_id"] = albumId.ToString(),
                    ["lang"] = "en",
                    ["tab"] = 0,
                });
                if (page is null)
                {
                    return DeezerAlbumQuality.Unknown;
                }
            }

            return Read(page, session, albumId);
        }
        catch (Exception ex)
        {
            // Never propagates: a probe that can't complete must look exactly like "we don't know",
            // because the caller's fallback for not knowing is the behaviour that existed before this
            // class did.
            _logger.LogWarning(ex, "Deezer quality probe failed for album {AlbumId}", albumId);
            return DeezerAlbumQuality.Unknown;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Turns a <c>pageAlbum</c> response into a verdict. The count of tracks carrying a non-zero
    /// <c>FILESIZE_FLAC</c> is the whole signal; what guards it is <see cref="Session.Lossless"/>.
    /// </summary>
    private DeezerAlbumQuality Read(JObject page, Session session, long albumId)
    {
        var songs = page["results"]?["SONGS"]?["data"] as JArray;
        if (songs is null || songs.Count == 0)
        {
            // No track list is not evidence of anything — an album Deezer has pulled, a shape we don't
            // recognise, a region block. Explicitly not "no lossless".
            _logger.LogDebug("Deezer returned no track list for album {AlbumId}", albumId);
            return DeezerAlbumQuality.Unknown;
        }

        var lossless = songs.Count(s => Size(s["FILESIZE_FLAC"]) > 0);

        if (lossless > 0)
        {
            // A non-zero size is self-proving: an unentitled session could not have produced it, so
            // this verdict needs no further guard.
            return new DeezerAlbumQuality(DeezerQualityVerdict.LosslessAvailable, songs.Count, lossless);
        }

        if (!session.Lossless)
        {
            // All zeroes from a session that was never allowed to see a FLAC size. This is the case the
            // whole class exists to refuse to answer.
            _logger.LogDebug(
                "Album {AlbumId} read as lossy, but the Deezer account has no lossless entitlement — "
                + "reporting unknown rather than writing off the album",
                albumId);
            return DeezerAlbumQuality.Unknown;
        }

        return new DeezerAlbumQuality(DeezerQualityVerdict.LossyOnly, songs.Count, 0);
    }

    /// <summary>Deezer sends sizes as strings; anything unparseable counts as absent.</summary>
    private static long Size(JToken? token) =>
        long.TryParse(token?.Value<string>(), out var size) ? size : 0;

    /// <summary>
    /// The current session, minting one if there isn't a usable one. Null when the credential didn't
    /// authenticate — which, note, includes the ordinary expiry case, since the gateway signals that
    /// by handing back a guest session rather than by failing.
    /// </summary>
    private async Task<Session?> EnsureSession(string arl)
    {
        if (_session is { } existing
            && existing.Arl == arl
            && DateTimeOffset.UtcNow - existing.Minted < SessionLifetime)
        {
            return existing;
        }

        // Keyed by credential, so pasting a replacement on the Download page is tried at once rather
        // than sitting out the cooldown earned by the token it replaced.
        if (_mintFailedArl == arl && DateTimeOffset.UtcNow - _mintFailedAt < MintRetryAfter)
        {
            return null;
        }

        _session?.Http.Dispose();
        _session = null;

        var cookies = new CookieContainer();
        cookies.Add(new Uri("https://www.deezer.com"), new Cookie("arl", arl));
        var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies }, disposeHandler: true)
        {
            Timeout = Timeout,
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);

        var fresh = new Session(arl, "null", http, false, DateTimeOffset.UtcNow);
        // getUserData is the call that mints a token, so it is the one call that doesn't need one.
        var data = await Call(fresh, "deezer.getUserData", "null", new JObject());

        var user = data?["results"]?["USER"];
        var userId = user?["USER_ID"]?.Value<long>();
        var token = data?["results"]?["checkForm"]?.Value<string>();
        if (userId is null or 0 || string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning(
                "Deezer quality probe has no usable session (the ARL is expired or was rejected); "
                + "upgrade availability will read as unknown until it is replaced");
            http.Dispose();
            _mintFailedArl = arl;
            _mintFailedAt = DateTimeOffset.UtcNow;
            return null;
        }

        var lossless = user?["OPTIONS"]?["web_lossless"]?.Value<bool>() ?? false;
        if (!lossless)
        {
            // Worth saying out loud once per session rather than per album: every verdict this session
            // produces will be LosslessAvailable-or-Unknown, so the pre-check is inert.
            _logger.LogWarning(
                "The Deezer account has no lossless entitlement, so upgrade availability cannot be "
                + "confirmed — albums will be offered unchecked, as they were before the pre-check");
        }

        _mintFailedArl = null;
        _mintFailedAt = DateTimeOffset.MinValue;
        _session = fresh with { Token = token!, Lossless = lossless };
        return _session;
    }

    /// <summary>
    /// One paced gateway call. Returns null for anything that isn't a clean, error-free response —
    /// the caller turns that into <see cref="DeezerQualityVerdict.Unknown"/> or a re-mint.
    /// </summary>
    private async Task<JObject?> Call(Session session, string method, string token, JObject payload)
    {
        var wait = MinInterval - (DateTimeOffset.UtcNow - _lastCall);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }
        _lastCall = DateTimeOffset.UtcNow;

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{GatewayBase}&api_token={token}&method={method}");
        request.Content = new StringContent(
            payload.ToString(Newtonsoft.Json.Formatting.None), System.Text.Encoding.UTF8, "application/json");

        using var response = await session.Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Deezer gateway answered {Status} to {Method}", (int)response.StatusCode, method);
            return null;
        }

        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
        // The gateway reports success as an empty `error` array and failure as a populated object, so
        // "has any content" is the test rather than "is present".
        var error = body["error"];
        if (error is JObject { HasValues: true } || error is JArray { Count: > 0 })
        {
            _logger.LogWarning("Deezer gateway refused {Method}: {Error}", method, error!.ToString());
            return null;
        }

        return body;
    }
}
