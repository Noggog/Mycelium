using Microsoft.Extensions.Logging;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// The unified, cross-source related-artists read path. Abstracted so consumers (e.g. the
/// discovery engine) can depend on the read without the ingestion/unification machinery behind it.
/// </summary>
public interface IRelatedArtistReader
{
    /// <param name="forceRefresh">Re-fetch every source's edges even if still fresh.</param>
    /// <param name="readOnly">
    /// Serve whatever edges are already stored without touching the network — no ingest, no staleness
    /// refresh. For request paths (e.g. serving the discover feed) that must return promptly and must
    /// not block on the rate-limited source APIs; the background replenisher/warmer keeps edges fresh,
    /// so refreshed results simply appear on the next read.
    /// </param>
    Task<UnifiedRelations> GetRelated(ArtistKey artist, bool forceRefresh = false, bool readOnly = false);
}

/// <summary>
/// Read path for related artists: ensures the graph is populated for an artist (ingesting on a
/// cache miss/stale), then unifies every source into a single list — one entry per distinct
/// artist, tagged with which sources recommended it — so the end user sees all the options.
/// </summary>
public class RelatedArtistInteractor : IRelatedArtistReader
{
    private readonly IEnumerable<ISimilaritySource> _sources;
    private readonly IRelatedArtistRepo _repo;
    private readonly ManualRecommendations _manual;
    private readonly ILogger<RelatedArtistInteractor> _logger;

    public RelatedArtistInteractor(
        IEnumerable<ISimilaritySource> sources,
        IRelatedArtistRepo repo,
        ManualRecommendations manual,
        ILogger<RelatedArtistInteractor> logger)
    {
        _sources = sources;
        _repo = repo;
        _manual = manual;
        _logger = logger;
    }

    public async Task<UnifiedRelations> GetRelated(ArtistKey artist, bool forceRefresh = false, bool readOnly = false)
    {
        // Ensure every registered source's edges are present/fresh; the unify step below then merges
        // however many are stored. Sources are isolated: one throwing (a bug past its own graceful
        // degradation) must not deny the user the others' recommendations. In readOnly mode we skip
        // this entirely and just serve what's stored — the caller can't afford to block on the
        // rate-limited source APIs, and the background replenisher/warmer refreshes edges out of band.
        if (!readOnly)
        {
            foreach (var source in _sources)
            {
                try
                {
                    await source.EnsureRelated(artist, forceRefresh);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex, "Source {Source} failed to ingest related artists for {Artist}",
                        source.SourceName, artist.ArtistName);
                }
            }
        }

        var perSource = (await _repo.GetAllSources(artist)).ToList();
        // Hand-entered pairs are merged in here rather than stored beside the sources' edges: nothing
        // fetches them, so they have nothing to go stale, and a source detach can't wipe them.
        if (await _manual.RelatedTo(artist) is { } manual)
        {
            perSource.Add(manual);
        }

        return new UnifiedRelations(artist, RelatedArtistUnifier.Unify(perSource));
    }
}
