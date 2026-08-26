namespace Mycelium.Plex;

/// <summary>
/// The <c>PLEX_TOKEN</c> environment variable, which is now only the <em>bootstrap</em> credential:
/// what a fresh deployment uses before anything has been linked in the dev panel, and the fallback if
/// the stored link is cleared. Null when unset, which is legitimate once a token has been linked.
/// Read it through <c>IPlexTokenSource</c>, never directly — that is what makes the token replaceable
/// at runtime.
/// </summary>
public record PlexClientInfo(string? Token);
