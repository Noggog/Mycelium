namespace Mycelium.Plex.Services.Singletons;

/// <summary>
/// Stamps the server's Plex token onto every request that doesn't already carry one.
///
/// <para>This replaces setting <c>X-Plex-Token</c> as a default header on the client, which fixed the
/// token at construction and so made it unchangeable for the life of the process. Asking
/// <see cref="IPlexTokenSource"/> per request is what lets the dev panel re-link without a restart.</para>
///
/// <para>"Doesn't already carry one" preserves the rule <c>PlexApi.AsToken</c> relies on: a per-user
/// read sets its own token on the message and must keep it, exactly as HttpClient's default headers
/// used to defer to it. Sending both would leave Plex to pick whichever it read first.</para>
/// </summary>
internal class PlexServerTokenHandler : DelegatingHandler
{
    private readonly IPlexTokenSource _tokens;

    public PlexServerTokenHandler(IPlexTokenSource tokens, HttpMessageHandler inner) : base(inner)
    {
        _tokens = tokens;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains("X-Plex-Token"))
        {
            request.Headers.Add("X-Plex-Token", await _tokens.Current());
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
