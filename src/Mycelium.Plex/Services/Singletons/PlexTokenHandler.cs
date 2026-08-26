using System.Net;

namespace Mycelium.Plex.Services.Singletons;

/// <summary>
/// The one place the Plex credential meets an HTTP request: it stamps the token on the way out and
/// translates a refusal on the way back.
///
/// <para><b>Stamping per request</b> replaces setting <c>X-Plex-Token</c> as a default header on the
/// client, which fixed the token at construction and so made it unchangeable for the life of the
/// process. Asking <see cref="IPlexTokenSource"/> each time is what lets the dev panel re-link without
/// a restart. A request that already carries a token keeps it — that is the rule
/// <c>PlexApi.AsToken</c> relies on for per-user reads, and sending both would leave Plex to pick
/// whichever it read first.</para>
///
/// <para><b>With nothing linked</b> the request still goes out, bare. That is what lets a brand-new
/// deployment reach <c>/identity</c> — which Plex answers without a credential — and so mint its
/// first token. Calls that do need one come back 401, and are reported below as what they are.</para>
///
/// <para><b>Translating the refusal</b> happens here, and not at the ~20 call sites, because an
/// expired credential fails whichever request happens to run first; the one place worth catching it is
/// the one they all pass through. Several call sites use <c>GetStringAsync</c> and never see a
/// response object to inspect, so a handler is also the only place that can.</para>
///
/// <para>Stamping and translating are deliberately not two handlers. The distinction between "Plex
/// refused our credential" and "we had no credential to offer" is only decidable by something that
/// knows both what was sent and what came back.</para>
/// </summary>
internal class PlexTokenHandler : DelegatingHandler
{
    private const string TokenHeader = "X-Plex-Token";

    private readonly IPlexTokenSource _tokens;

    public PlexTokenHandler(IPlexTokenSource tokens, HttpMessageHandler inner) : base(inner)
    {
        _tokens = tokens;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var resolved = await _tokens.Resolve();
        if (resolved.Token is not null && !request.Headers.Contains(TokenHeader))
        {
            request.Headers.Add(TokenHeader, resolved.Token);
        }

        var asked = request.Headers.Contains(TokenHeader);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            return response;
        }

        response.Dispose();
        var where = $"{request.Method} {request.RequestUri?.AbsolutePath} ({(int)response.StatusCode})";

        // Refused something we offered vs. refused because we offered nothing. Telling someone to
        // re-mint a credential they never minted sends them hunting for a token that doesn't exist.
        throw asked
            ? new PlexUnauthorizedException(
                $"Plex rejected the token on {where}. The configured Plex token is no longer valid.")
            : new PlexNotLinkedException(
                $"Plex refused {where}: no credential is linked to send.");
    }
}
