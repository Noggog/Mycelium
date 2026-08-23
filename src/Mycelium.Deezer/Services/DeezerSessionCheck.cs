using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Mycelium.Deezer.Services;

/// <summary>
/// Validates an ARL against Deezer's internal gateway — the same call streamrip's Deezer client makes
/// to log in (<c>deezer-py</c>'s <c>login_via_arl</c> sets the cookie, calls
/// <c>deezer.getUserData</c>, and treats <c>USER_ID == 0</c> as a rejection). Doing it here rather
/// than shelling out to <c>rip</c> keeps the check to one request and, more importantly, makes it
/// safe: every <c>rip</c> invocation is a download attempt, so it can't be used as a probe.
///
/// This is the app's only use of the private gateway. It's read-only, unauthenticated apart from the
/// cookie under test, and touches no media — but it is not the documented public API, so it's kept
/// behind this one narrow interface rather than folded into <see cref="DeezerApi"/>.
/// </summary>
public class DeezerSessionCheck : IDeezerSessionCheck
{
    // The gateway ignores api_token for getUserData (it's the call that *mints* one), so "null" is
    // what a fresh session sends — matching deezer-py exactly.
    private const string GatewayUrl =
        "https://www.deezer.com/ajax/gw-light.php?api_version=1.0&api_token=null&input=3&method=deezer.getUserData";

    // The gateway answers HTML (not JSON) to a request with no browser-shaped User-Agent, so this is
    // load-bearing rather than cosmetic.
    private const string UserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly ILogger<DeezerSessionCheck> _logger;

    public DeezerSessionCheck(ILogger<DeezerSessionCheck> logger)
    {
        _logger = logger;
    }

    public async Task<DeezerSessionInfo> Check(string arl)
    {
        arl = arl.Trim();
        if (string.IsNullOrEmpty(arl))
        {
            return new DeezerSessionInfo(false, null, false);
        }

        try
        {
            // A per-call client: the ARL rides as a cookie header, and a shared handler's cookie
            // container would let one check's credential leak into the next.
            using var http = new HttpClient { Timeout = Timeout };
            using var request = new HttpRequestMessage(HttpMethod.Post, GatewayUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Cookie", $"arl={arl}");
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return new DeezerSessionInfo(
                    false, null, false, $"Deezer answered {(int)response.StatusCode}");
            }

            var user = JObject.Parse(await response.Content.ReadAsStringAsync())["results"]?["USER"];
            // USER_ID 0 is Deezer's "not logged in" — the token was rejected. Absent means a shape we
            // don't recognise, which is a failed check rather than a verdict.
            var userId = user?["USER_ID"]?.Value<long>();
            if (userId is null)
            {
                return new DeezerSessionInfo(false, null, false, "Deezer returned an unexpected response");
            }
            if (userId == 0)
            {
                return new DeezerSessionInfo(false, null, false);
            }

            var name = user?["BLOG_NAME"]?.Value<string>();
            var lossless = user?["OPTIONS"]?["web_lossless"]?.Value<bool>() ?? false;
            return new DeezerSessionInfo(true, string.IsNullOrWhiteSpace(name) ? null : name, lossless);
        }
        catch (Exception ex)
        {
            // Deliberately not a rejection: telling a user their ARL is dead because Deezer was
            // briefly unreachable would send them to re-copy a credential that was working.
            _logger.LogWarning(ex, "Deezer session check could not complete");
            return new DeezerSessionInfo(false, null, false, "Couldn't reach Deezer to check");
        }
    }
}
