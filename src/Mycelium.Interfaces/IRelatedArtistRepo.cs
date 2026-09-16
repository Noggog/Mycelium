namespace Mycelium.Interfaces;

/// <summary>
/// Persisted similarity graph: artist -> related artists, stored per source. Global/shared
/// (not per-user). Survives the upstream source being offline — once fetched, the edges live
/// here and reads never touch the network.
/// </summary>
public interface IRelatedArtistRepo
{
    /// <summary>Insert or replace the related-artist edge set for one (artist, source).</summary>
    Task Upsert(ArtistRelations relations);

    /// <summary>
    /// The stored edge set for one (artist, source), or null if never fetched. Used to decide
    /// whether a re-fetch is due (staleness check).
    /// </summary>
    Task<ArtistRelations?> Get(ArtistKey artist, string source);

    /// <summary>Every source's edge set for an artist, for unifying across sources at read time.</summary>
    Task<IReadOnlyList<ArtistRelations>> GetAllSources(ArtistKey artist);

    /// <summary>
    /// Drops every source's stored edges for an artist. Used when a source link is detached, so the
    /// stale edges derived from the wrong identity don't linger (a null re-resolve won't overwrite them).
    /// </summary>
    Task DeleteAllSources(ArtistKey artist);

    /// <summary>
    /// Every artist holding a non-empty edge set from <paramref name="source"/> — including artists
    /// outside the library (liked, not yet owned), which have edges but no catalog row to find them by.
    /// </summary>
    Task<IReadOnlyList<string>> GetArtistNamesWithEdges(string source);
}
