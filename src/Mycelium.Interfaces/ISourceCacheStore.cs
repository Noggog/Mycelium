namespace Mycelium.Interfaces;

/// <summary>
/// One cached answer from an outside source (MusicBrainz, Deezer), as JSON.
/// </summary>
/// <param name="Key">Namespaced by the caller, e.g. <c>musicbrainz:release-groups:{mbid}</c>.</param>
/// <param name="FetchedAt">When the source gave this answer.</param>
/// <param name="ExpiresAt">
/// When it should be asked again. Past this the entry is stale but still served while a refresh runs.
/// </param>
public record SourceCacheEntry(string Key, string Json, DateTimeOffset FetchedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// The persistent half of the source cache: what an in-memory cache is refilled from after a restart,
/// so a restart never has to ask a rate-limited source everything again.
/// </summary>
public interface ISourceCacheStore
{
    /// <summary>The entry under <paramref name="key"/>, stale or not, or null if there is none.</summary>
    Task<SourceCacheEntry?> Get(string key);

    /// <summary>Stores the entry, replacing any under the same key.</summary>
    Task Put(SourceCacheEntry entry);

    /// <summary>Marks the entry as due for a refresh now, keeping its value to serve meanwhile.</summary>
    Task Expire(string key, DateTimeOffset at);
}
