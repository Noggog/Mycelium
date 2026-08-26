using Mycelium.Interfaces;

namespace Mycelium.Plex.Services.Singletons;

/// <summary>The current credential, and the account it was linked from. Null token means none is set.</summary>
public record PlexTokenResolution(string? Token, PlexServerCredential? Linked);

/// <summary>
/// Resolves the token every Plex call is made with, so it is asked for per request rather than baked
/// into the HTTP client at construction. That indirection is the whole point: it makes the token
/// something that can be replaced at runtime — by the dev panel's link flow — instead of a value fixed
/// for the lifetime of the process and changeable only by redeploying.
///
/// <para>There is one source: what was linked in the dev panel and stored. A deployment that has never
/// linked has no token, which is a legitimate state — it can still reach the unauthenticated
/// <c>/identity</c> endpoint, which is all the link flow needs to get started.</para>
/// </summary>
public interface IPlexTokenSource
{
    /// <summary>
    /// The token to send, read through a process-wide cache so this costs a Mongo round trip once
    /// rather than once per Plex request. Throws when nothing is linked — for callers that cannot
    /// proceed without one and would otherwise send a bare request and report the 401 as an expiry.
    /// </summary>
    Task<string> Current();

    /// <summary>
    /// Current token and the account behind it, without throwing when there is none. What the request
    /// handler and the dev panel both ask, since "not linked yet" is an answer to them, not a failure.
    /// </summary>
    Task<PlexTokenResolution> Resolve();

    /// <summary>
    /// Drops the cached value so the next call re-reads the store. Called after the token is
    /// re-linked or cleared — the write goes through the repo, and this is what makes it take effect.
    /// </summary>
    void Invalidate();
}
