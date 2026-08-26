namespace Mycelium.Interfaces;

/// <summary>
/// The credential this app uses to read the Plex library — the server's own token, as opposed to the
/// per-user tokens in <see cref="IPlexLinkRepo"/>.
///
/// <para><see cref="Username"/> is whoever plex.tv said the token belongs to when it was minted, kept
/// only so the dev panel can say which account is connected. It is absent for a pasted token, which
/// the server will validate but cannot attribute.</para>
///
/// <para>Same caveat as <see cref="PlexLink"/>: stored in plain text, and worth encrypting at rest
/// before this app is exposed to anyone the operator doesn't trust.</para>
/// </summary>
public record PlexServerCredential(
    string Token,
    string? Username,
    string? Email,
    DateTimeOffset LinkedAt);

/// <summary>
/// Stores the server-wide Plex token, so it can be re-minted from the dev panel instead of being
/// baked into the environment and requiring a redeploy to change. Absent means the deployment has
/// never linked Plex — a legitimate starting state, and what the dev panel prompts to fix.
/// </summary>
public interface IPlexServerTokenRepo
{
    Task<PlexServerCredential?> Get();

    Task Set(PlexServerCredential credential);

    /// <summary>Forgets the stored token, reverting to whatever the environment supplies. Idempotent.</summary>
    Task Clear();
}
