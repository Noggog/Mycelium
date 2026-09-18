using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// Hand-entered recommendations (see <see cref="ManualRecommendation"/>), served to the similarity
/// graph as one more source, <see cref="SourceName"/>. <see cref="RelatedArtistInteractor"/> merges
/// them in on every read, so the discover queue, the "via" provenance, the owned-and-recommended
/// section and the Plex recommended tags all pick them up with no code of their own.
///
/// <para>Every pair runs both ways: registering Steve Roach with Hearts of Space recommends each to
/// the fans of the other.</para>
///
/// <para>The set is held in memory. It is read once per liked artist on every feed request, it is a
/// handful of rows typed in by hand, and this process is the only writer — so a reload after each
/// write is all the invalidation it needs.</para>
/// </summary>
public class ManualRecommendations
{
    /// <summary>The source tag manual edges carry, beside "deezer" and "listenbrainz".</summary>
    public const string SourceName = "manual";

    private readonly IManualRecommendationRepo _repo;
    private volatile IReadOnlyDictionary<string, IReadOnlyList<string>>? _byArtist;
    // Bumped by every write, so a load that raced one can tell its snapshot is stale and not keep it.
    private int _generation;

    public ManualRecommendations(IManualRecommendationRepo repo)
    {
        _repo = repo;
    }

    public async Task<IReadOnlyList<ManualRecommendation>> GetAll() =>
        (await _repo.GetAll())
        .OrderBy(r => r.ArtistA, StringComparer.OrdinalIgnoreCase)
        .ThenBy(r => r.ArtistB, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>
    /// Registers the pair, returning it. Entering one that already exists (either way round, in any
    /// casing) replaces it rather than adding a second. Throws <see cref="ArgumentException"/> for a
    /// blank name or an artist paired with itself.
    /// </summary>
    public async Task<ManualRecommendation> Add(string artistA, string artistB, string? addedBy)
    {
        var a = artistA.Trim();
        var b = artistB.Trim();
        if (a.Length == 0 || b.Length == 0)
        {
            throw new ArgumentException("Both artists are required.");
        }

        if (RelatedArtistUnifier.NormalizeKey(a) == RelatedArtistUnifier.NormalizeKey(b))
        {
            throw new ArgumentException("An artist can't recommend itself.");
        }

        var recommendation = new ManualRecommendation(PairId(a, b), a, b, addedBy, DateTimeOffset.UtcNow);
        await _repo.Upsert(recommendation);
        Invalidate();
        return recommendation;
    }

    public async Task<bool> Remove(string id)
    {
        var removed = await _repo.Delete(id);
        Invalidate();
        return removed;
    }

    /// <summary>
    /// The manual edges for <paramref name="artist"/>, shaped like any source's, or null when it has
    /// none. Matched on the same case- and accent-blind key the unifier merges on, so a pair typed as
    /// "steve roach" still finds the library's "Steve Roach".
    /// </summary>
    public async Task<ArtistRelations?> RelatedTo(ArtistKey artist)
    {
        var byArtist = _byArtist ?? await Load();
        if (!byArtist.TryGetValue(RelatedArtistUnifier.NormalizeKey(artist.ArtistName), out var related))
        {
            return null;
        }

        return new ArtistRelations(
            artist,
            SourceName,
            related.Select(name => new RelatedArtist(new ArtistKey(name), null)).ToArray(),
            DateTimeOffset.UtcNow);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> Load()
    {
        var generation = Volatile.Read(ref _generation);
        var byArtist = new Dictionary<string, List<string>>();
        foreach (var pair in await _repo.GetAll())
        {
            Link(pair.ArtistA, pair.ArtistB);
            Link(pair.ArtistB, pair.ArtistA);
        }

        var loaded = byArtist.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
        if (Volatile.Read(ref _generation) == generation)
        {
            _byArtist = loaded;
        }
        return loaded;

        void Link(string from, string to)
        {
            var key = RelatedArtistUnifier.NormalizeKey(from);
            if (!byArtist.TryGetValue(key, out var list))
            {
                byArtist[key] = list = new List<string>();
            }
            list.Add(to);
        }
    }

    private void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _byArtist = null;
    }

    /// <summary>Order-free, so A↔B and B↔A are one pair.</summary>
    private static string PairId(string a, string b)
    {
        var keys = new[] { RelatedArtistUnifier.NormalizeKey(a), RelatedArtistUnifier.NormalizeKey(b) };
        Array.Sort(keys, StringComparer.Ordinal);
        return $"{keys[0]}␟{keys[1]}";
    }
}
