using System.Text.Json.Serialization;

namespace Mycelium.Interfaces;

/// <summary>What checking a library artist against MusicBrainz came to. See <see cref="ArtistResolution"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArtistResolutionStatus
{
    /// <summary>A user pinned the MBID by hand. Taken as given.</summary>
    Pinned,

    /// <summary>One MusicBrainz artist is the answer, with the stated <see cref="ResolutionConfidence"/>.</summary>
    Resolved,

    /// <summary>Several MusicBrainz artists fit equally well. A person has to choose.</summary>
    Ambiguous,

    /// <summary>MusicBrainz has no artist that fits. Someone has to add it there.</summary>
    Missing,

    /// <summary>A user detached the artist from MusicBrainz.</summary>
    Unlinked,
}

/// <summary>How far a <see cref="ArtistResolutionStatus.Resolved"/> answer can be trusted.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResolutionConfidence
{
    /// <summary>Several owned albums are on it, or it is linked from the artist's Deezer page and has one.</summary>
    High,

    /// <summary>Some evidence beyond the name, but thin: one owned album of many, or a Deezer link alone.</summary>
    Medium,

    /// <summary>Nothing but a matching name — or evidence against it. Not to be relied on unconfirmed.</summary>
    Low,
}

/// <summary>Why a MusicBrainz artist was considered at all.</summary>
public static class ResolutionEvidence
{
    /// <summary>Its name matches the library's.</summary>
    public const string Name = "name";

    /// <summary>One of its aliases matches the library's name.</summary>
    public const string Alias = "alias";

    /// <summary>MusicBrainz links it to the Deezer artist page the library artist is resolved to.</summary>
    public const string Deezer = "deezer";

    /// <summary>It is the MBID the artist is linked to today.</summary>
    public const string Current = "current";
}

/// <summary>One MusicBrainz artist weighed for a library artist.</summary>
/// <param name="AlbumOverlap">
/// How many of the library's albums by this artist are among the candidate's release groups (by
/// record-level title). Null when it wasn't counted — the library owns none, or MusicBrainz didn't
/// answer for the candidate.
/// </param>
/// <param name="Evidence">Why it was considered; see <see cref="ResolutionEvidence"/>.</param>
public record ResolutionCandidate(
    string Mbid,
    string? Name,
    string? Disambiguation,
    int? AlbumOverlap,
    IReadOnlyList<string> Evidence);

/// <summary>
/// The outcome of checking one library artist against MusicBrainz: which artist it is, how sure that
/// is, and the evidence — kept whole so the reconciliation panel can show a person what the choice was
/// between. Keyed by the library's artist name, the key everything still uses until the re-key
/// migration; this record is what that migration will resolve names through.
/// </summary>
/// <param name="Mbid">The answer, for <see cref="ArtistResolutionStatus.Pinned"/> and <see cref="ArtistResolutionStatus.Resolved"/>.</param>
/// <param name="CurrentMbid">
/// The MBID the artist is linked to today (by the older name-only resolver, or a pin). When it differs
/// from <paramref name="Mbid"/>, one of the two is wrong.
/// </param>
/// <param name="OwnedAlbums">How many albums the library holds by this artist.</param>
/// <param name="Reason">One line on how the verdict was reached, for a person reading the panel.</param>
public record ArtistResolution(
    string Artist,
    ArtistResolutionStatus Status,
    ResolutionConfidence? Confidence,
    string? Mbid,
    string? Name,
    string? Disambiguation,
    string? CurrentMbid,
    int OwnedAlbums,
    IReadOnlyList<ResolutionCandidate> Candidates,
    string Reason,
    DateTimeOffset CheckedAt)
{
    /// <summary>
    /// Whether a person should look at it: anything without an answer, and any answer resting on
    /// nothing but a name. The reconciliation panel is exactly these.
    /// </summary>
    public bool NeedsAttention =>
        Status is ArtistResolutionStatus.Ambiguous or ArtistResolutionStatus.Missing or ArtistResolutionStatus.Unlinked
        || Confidence == ResolutionConfidence.Low;

    /// <summary>Whether it resolved to a different artist than the one linked today.</summary>
    public bool DisagreesWithCurrent =>
        Mbid is not null && CurrentMbid is not null && !string.Equals(Mbid, CurrentMbid, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Stored <see cref="ArtistResolution"/>s, one per library artist name.</summary>
public interface IArtistResolutionRepo
{
    Task<ArtistResolution[]> GetAll();

    Task<ArtistResolution?> Get(string artist);

    /// <summary>When each stored artist was last checked, to pick the ones due again without loading everything.</summary>
    Task<Dictionary<string, DateTimeOffset>> GetCheckedAt();

    /// <summary>How many need a person (<see cref="ArtistResolution.NeedsAttention"/>) — the nav badge.</summary>
    Task<long> CountNeedingAttention();

    /// <summary>Stores the resolution, replacing the artist's previous one.</summary>
    Task Put(ArtistResolution resolution);

    /// <summary>Drops the resolutions of artists no longer in the library.</summary>
    Task DeleteAllExcept(IReadOnlyCollection<string> artists);
}
