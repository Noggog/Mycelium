using System.Net;

namespace Mycelium.Plex.Services.Singletons;

/// <summary>
/// Turns Plex's 401/403 into <see cref="PlexUnauthorizedException"/> for every call on the client,
/// including the several that go through <c>GetStringAsync</c> and so never see a response object to
/// inspect. A delegating handler rather than a check at each call site: an expired token fails
/// whatever request happens to run first, so the one place worth catching it is the one every
/// request passes through.
/// </summary>
public class PlexAuthFailureHandler : DelegatingHandler
{
    public PlexAuthFailureHandler(HttpMessageHandler inner) : base(inner)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new PlexUnauthorizedException(
                $"Plex rejected the token on {request.Method} {request.RequestUri?.AbsolutePath} "
                + $"({(int)response.StatusCode}). The configured PLEX_TOKEN is no longer valid.");
        }

        return response;
    }
}
