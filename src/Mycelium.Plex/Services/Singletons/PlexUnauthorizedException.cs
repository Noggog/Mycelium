namespace Mycelium.Plex.Services.Singletons;

/// <summary>
/// Plex refused the token — a 401/403 from the media server, not a transport failure. In practice
/// that means the credential <see cref="IPlexTokenSource"/> resolved has been invalidated: Plex
/// revokes every token when the account password changes with "sign out connected devices" set, and a
/// device registering a JWK with plex.tv retires the legacy token it replaces.
///
/// Distinguished from a generic <see cref="HttpRequestException"/> because the remedy is specific
/// and human: re-link Plex in the dev panel. Left as a plain 500 it reads as "the app is broken" and
/// sends you into the logs to find out otherwise.
/// </summary>
public class PlexUnauthorizedException : Exception
{
    public PlexUnauthorizedException(string message) : base(message)
    {
    }
}
