namespace Mycelium.Plex.Services.Singletons;

/// <summary>
/// A pending link request. The user visits <see cref="AuthUrl"/>, approves there, and the app then
/// claims the pin for a token. Pins expire (typically 30 minutes) and are single-use.
/// </summary>
public record PlexPin(long Id, string Code, string AuthUrl);

/// <summary>
/// A successfully linked Plex account. <see cref="ServerToken"/> is deliberately the <em>server-scoped</em>
/// access token rather than the account-wide one — it is all this app needs, and it cannot be used
/// against the user's other servers or their plex.tv account settings.
/// </summary>
public record PlexAccount(string AccountId, string Username, string? Email, string ServerToken);

/// <summary>
/// The plex.tv side of account linking, via the PIN flow: the app asks plex.tv for a short code, sends
/// the user to app.plex.tv to approve it, then exchanges it for that user's token.
///
/// <para><b>Why link at all.</b> Playlists, track ratings and play history are per-Plex-account. Creating
/// playlists with the server owner's token would put every user's playlists in the owner's sidebar and
/// filter them by the owner's star ratings. A per-user token makes both right without touching the rules.
/// Library <em>metadata</em> (the mood tags a rating writes) stays on the app's server token — that is
/// shared state and needs the owner's rights.</para>
/// </summary>
public interface IPlexAccountApi
{
    /// <summary>
    /// Starts a link. <paramref name="forwardUrl"/>, when given, is where plex.tv returns the browser
    /// after approval; it must be an absolute URL the user can reach.
    /// </summary>
    Task<PlexPin> CreatePin(string? forwardUrl);

    /// <summary>
    /// Attempts to claim an approved pin. Returns null while the user hasn't finished approving, which
    /// is the normal answer until they do — callers poll. Throws only if plex.tv itself errors.
    /// </summary>
    Task<string?> ClaimPin(long id, string code);

    /// <summary>
    /// Resolves who a token belongs to and what it may reach on <paramref name="machineIdentifier"/>.
    /// Returns null when that account has no access to this server — the one case worth refusing a link
    /// over, since nothing the app would create could be seen by them.
    /// </summary>
    Task<PlexAccount?> ResolveAccount(string accountToken, string machineIdentifier);
}
