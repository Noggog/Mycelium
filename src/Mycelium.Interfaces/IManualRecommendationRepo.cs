namespace Mycelium.Interfaces;

/// <summary>
/// A recommendation entered by hand rather than learned from a similarity source: two artists that
/// point at each other. For pairings no feed will ever produce — a radio show next to the artists it
/// plays, say. Undirected: liking either one recommends the other.
/// </summary>
/// <param name="Id">The pair's identity, the same whichever way round the two were entered — see
/// <c>ManualRecommendations</c>.</param>
public record ManualRecommendation(
    string Id,
    string ArtistA,
    string ArtistB,
    string? AddedBy,
    DateTimeOffset AddedAt);

/// <summary>Persisted hand-entered recommendations. Global, like the similarity graph.</summary>
public interface IManualRecommendationRepo
{
    Task<IReadOnlyList<ManualRecommendation>> GetAll();

    /// <summary>Insert, or replace the pair with the same <see cref="ManualRecommendation.Id"/>.</summary>
    Task Upsert(ManualRecommendation recommendation);

    /// <summary>Returns whether there was such a pair to remove.</summary>
    Task<bool> Delete(string id);
}
