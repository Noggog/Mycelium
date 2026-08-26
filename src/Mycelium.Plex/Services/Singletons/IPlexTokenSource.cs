using Mycelium.Interfaces;

namespace Mycelium.Plex.Services.Singletons;

/// <summary>Where the server's Plex token came from — what the dev panel reports.</summary>
public enum PlexTokenOrigin
{
    /// <summary>Linked in-app and stored in Mongo. Re-mintable without a redeploy.</summary>
    Linked,

    /// <summary>The <c>PLEX_TOKEN</c> environment variable — the bootstrap value, and the fallback.</summary>
    Environment,

    /// <summary>Neither is set: the app cannot read Plex at all until something links it.</summary>
    None,
}

/// <summary>The current credential and where it came from.</summary>
public record PlexTokenResolution(string? Token, PlexTokenOrigin Origin, PlexServerCredential? Linked);

/// <summary>
/// Resolves the token every Plex call is made with, so it is asked for per request rather than baked
/// into the HTTP client at construction. That indirection is the whole point: it makes the token
/// something that can be replaced at runtime — by the dev panel's link flow — instead of a value fixed
/// for the lifetime of the process and changeable only by editing the environment and redeploying.
///
/// <para>A linked token (Mongo) wins over the environment, which remains the bootstrap: a fresh
/// deployment has nothing stored, and <c>PLEX_TOKEN</c> is what gets it far enough to link.</para>
/// </summary>
public interface IPlexTokenSource
{
    /// <summary>
    /// The token to send, read through a process-wide cache so this costs a Mongo round trip once
    /// rather than once per Plex request. Throws when nothing is configured at all, because a request
    /// sent with no token is a 401 that would be reported as an expired credential.
    /// </summary>
    Task<string> Current();

    /// <summary>Current token and provenance, for the dev panel. Null token when nothing is set.</summary>
    Task<PlexTokenResolution> Resolve();

    /// <summary>
    /// Drops the cached value so the next call re-reads the store. Called after the token is
    /// re-linked or cleared — the write goes through the repo, and this is what makes it take effect.
    /// </summary>
    void Invalidate();
}
