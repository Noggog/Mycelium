namespace Mycelium.Interfaces;

/// <summary>
/// One app user's connection to their own Plex account.
///
/// <para><see cref="ServerToken"/> is a credential. It is scoped to the single Plex server this app is
/// configured against (plex.tv issues a per-server access token, which is what the link flow keeps —
/// never the account-wide token), so its blast radius is that one library. It is still stored in plain
/// text in Mongo, like <c>PLEX_TOKEN</c> is in the environment today; encrypting it at rest is worth
/// doing before this app is exposed to anyone the operator doesn't trust.</para>
/// </summary>
public record PlexLink(
    string Subject,
    string AccountId,
    string Username,
    string? Email,
    string ServerToken,
    DateTimeOffset LinkedAt);

/// <summary>
/// Stores each user's linked Plex account, keyed by OIDC subject. Absent means "not linked", which is
/// the starting state for everyone — the app works without it, only the playlist features need one.
/// </summary>
public interface IPlexLinkRepo
{
    Task<PlexLink?> Get(string subject);

    Task Upsert(PlexLink link);

    /// <summary>Forgets the link and, with it, the stored token. Idempotent.</summary>
    Task Delete(string subject);
}
