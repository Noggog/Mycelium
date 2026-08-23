namespace Mycelium.Plex;

/// <summary>
/// How this app identifies itself to Plex. <paramref name="Product"/> is the name shown in the account's
/// authorised-devices list; <paramref name="ClientIdentifier"/> is the stable device id that must be
/// <b>identical</b> on the request that creates a plex.tv link PIN and on the request that later claims
/// it, or the claim returns no token. It therefore has to survive a restart, so it comes from
/// configuration rather than being generated per process.
/// </summary>
public record PlexAppIdentity(string Product, string ClientIdentifier);
