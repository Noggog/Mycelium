using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Mycelium.Plex.Services.Singletons;

/// <summary>
/// <see cref="IPlexAccountApi"/> against plex.tv's v2 API.
///
/// <para><b>The client identifier matters.</b> plex.tv ties a pin to the device that created it, so the
/// <c>X-Plex-Client-Identifier</c> sent when claiming must equal the one sent when creating. It comes
/// from <see cref="PlexAppIdentity"/> (configuration, not a per-process value) so a link survives the
/// backend restarting mid-flow.</para>
/// </summary>
public class PlexAccountApi : IPlexAccountApi
{
    private const string PlexTv = "https://plex.tv/api/v2";
    private const string AuthBase = "https://app.plex.tv/auth#";

    private readonly PlexAppIdentity _identity;
    private readonly ILogger<PlexAccountApi> _logger;
    private readonly HttpClient _httpClient = new();

    public PlexAccountApi(PlexAppIdentity identity, ILogger<PlexAccountApi> logger)
    {
        _identity = identity;
        _logger = logger;
    }

    public async Task<PlexPin> CreatePin(string? forwardUrl)
    {
        // strong=true asks for a longer, non-guessable code — these are approved in a browser the app
        // never sees, so a short code would be worth guessing.
        using var request = Build(HttpMethod.Post, $"{PlexTv}/pins?strong=true");
        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
        var id = body["id"]?.Value<long>()
                 ?? throw new InvalidOperationException("plex.tv returned a pin with no id.");
        var code = body["code"]?.ToString()
                   ?? throw new InvalidOperationException("plex.tv returned a pin with no code.");

        var authUrl = $"{AuthBase}?clientID={Uri.EscapeDataString(_identity.ClientIdentifier)}"
                      + $"&code={Uri.EscapeDataString(code)}"
                      + $"&context%5Bdevice%5D%5Bproduct%5D={Uri.EscapeDataString(_identity.Product)}";
        if (!string.IsNullOrWhiteSpace(forwardUrl))
        {
            authUrl += $"&forwardUrl={Uri.EscapeDataString(forwardUrl)}";
        }

        _logger.LogInformation("Started a Plex account link (pin {PinId}).", id);
        return new PlexPin(id, code, authUrl);
    }

    public async Task<string?> ClaimPin(long id, string code)
    {
        using var request = Build(HttpMethod.Get, $"{PlexTv}/pins/{id}?code={Uri.EscapeDataString(code)}");
        using var response = await _httpClient.SendAsync(request);

        // An expired or already-claimed pin 404s. That's a dead flow, not an outage — the caller starts
        // a new one rather than retrying this.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogInformation("Plex link pin {PinId} has expired or was already claimed.", id);
            return null;
        }

        response.EnsureSuccessStatusCode();
        var token = JObject.Parse(await response.Content.ReadAsStringAsync())["authToken"]?.ToString();
        // Null until the user finishes approving in their browser; polling this is the intended flow.
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    public async Task<bool> Ping(string token)
    {
        using var request = Build(HttpMethod.Post, $"{PlexTv}/ping");
        request.Headers.Add("X-Plex-Token", token);
        using var response = await _httpClient.SendAsync(request);

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("plex.tv refused the keep-alive ping — the token is no longer valid.");
            return false;
        }

        response.EnsureSuccessStatusCode();
        _logger.LogDebug("Pinged plex.tv; the token's expiry has been pushed back.");
        return true;
    }

    public async Task<PlexAccount?> ResolveAccount(string accountToken, string machineIdentifier)
    {
        var user = await GetJson(accountToken, $"{PlexTv}/user") as JObject
                   ?? throw new InvalidOperationException("plex.tv returned no account for that token.");

        var resources = await GetJson(accountToken, $"{PlexTv}/resources?includeHttps=1") as JArray
                        ?? new JArray();

        // The server entry carries an access token scoped to that one server — narrower than the
        // account-wide token, and its presence is also the proof that this account can reach the library.
        var server = resources.OfType<JObject>().FirstOrDefault(r =>
            string.Equals(r["clientIdentifier"]?.ToString(), machineIdentifier, StringComparison.OrdinalIgnoreCase)
            && (r["provides"]?.ToString() ?? "").Split(',').Contains("server"));

        var serverToken = server?["accessToken"]?.ToString();
        if (string.IsNullOrWhiteSpace(serverToken))
        {
            _logger.LogWarning(
                "A Plex account linked, but it has no access to server {MachineId}.", machineIdentifier);
            return null;
        }

        return new PlexAccount(
            AccountId: user["id"]?.ToString() ?? user["uuid"]?.ToString() ?? "",
            Username: user["username"]?.ToString() ?? user["title"]?.ToString() ?? "Plex user",
            Email: user["email"]?.ToString(),
            ServerToken: serverToken);
    }

    private async Task<JToken?> GetJson(string token, string url)
    {
        using var request = Build(HttpMethod.Get, url);
        request.Headers.Add("X-Plex-Token", token);
        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JToken.Parse(await response.Content.ReadAsStringAsync());
    }

    private HttpRequestMessage Build(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-Plex-Product", _identity.Product);
        request.Headers.Add("X-Plex-Client-Identifier", _identity.ClientIdentifier);
        return request;
    }
}
