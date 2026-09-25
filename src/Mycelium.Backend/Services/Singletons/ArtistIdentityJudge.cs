using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// Decides which MusicBrainz artist a library artist is, from evidence already gathered — the part of
/// <see cref="ArtistIdentityAuditor"/> with no I/O, kept apart so the rules can be read and tested on
/// their own.
///
/// <para><b>The rule that matters: a name is never enough.</b> Once artists are keyed by MBID, a wrong
/// match moves every verdict and rating onto the wrong band. So the strongest evidence is the library
/// itself — the candidate whose discography holds the albums you own is the artist you have (only one
/// "Vulture" released <i>Sentinels</i>). A link from the artist's Deezer page counts too, but less:
/// the Deezer artist was itself found by name, and Deezer pages merge same-named acts. A candidate
/// with nothing but a matching name resolves, but at <see cref="ResolutionConfidence.Low"/>, which puts
/// it in front of a person.</para>
/// </summary>
public static class ArtistIdentityJudge
{
    /// <param name="candidates">Every MusicBrainz artist worth weighing, strongest lead first.</param>
    public static ArtistResolution Judge(
        string artist,
        int ownedAlbums,
        MusicBrainzIdentity? current,
        bool currentIsPinned,
        bool unlinked,
        IReadOnlyList<ResolutionCandidate> candidates,
        DateTimeOffset now)
    {
        ArtistResolution Verdict(
            ArtistResolutionStatus status,
            string reason,
            ResolutionCandidate? chosen = null,
            ResolutionConfidence? confidence = null) =>
            new(artist, status, confidence, chosen?.Mbid, chosen?.Name, chosen?.Disambiguation,
                current?.Mbid, ownedAlbums, candidates, reason, now);

        if (unlinked)
        {
            return Verdict(ArtistResolutionStatus.Unlinked, "Detached from MusicBrainz by hand.");
        }

        if (current is not null && currentIsPinned)
        {
            var pinned = candidates.FirstOrDefault(c => Same(c.Mbid, current.Mbid))
                         ?? new ResolutionCandidate(current.Mbid, current.Name, current.Disambiguation, null,
                             new[] { ResolutionEvidence.Current });
            return Verdict(ArtistResolutionStatus.Pinned, "Pinned by hand.", pinned);
        }

        if (candidates.Count == 0)
        {
            return Verdict(ArtistResolutionStatus.Missing, "No MusicBrainz artist goes by this name.");
        }

        var best = candidates.Max(c => c.AlbumOverlap ?? 0);
        if (best > 0)
        {
            if (Mixed(candidates) is { } shares)
            {
                return Verdict(ArtistResolutionStatus.Mixed,
                    "The library's albums here belong to different acts: "
                    + string.Join("; ", shares.Select(c =>
                        $"{Label(c)} has {string.Join(", ", c.MatchedAlbums!)}"))
                    + ".");
            }

            var top = candidates.Where(c => c.AlbumOverlap == best).ToList();
            if (top.Count > 1)
            {
                // A tie the Deezer link can break: exactly one of the tied acts is the Deezer page's.
                var tieBreak = top.Where(c => c.Evidence.Contains(ResolutionEvidence.Deezer)).ToList();
                if (tieBreak.Count == 1)
                {
                    return Verdict(ArtistResolutionStatus.Resolved,
                        $"Ties with {top.Count - 1} other artist(s) on the library's albums; linked from the Deezer page.",
                        tieBreak[0], ResolutionConfidence.Medium);
                }

                return Verdict(ArtistResolutionStatus.Ambiguous,
                    $"{top.Count} MusicBrainz artists each have {Albums(best)} of the library's albums.");
            }

            var chosen = top[0];
            var linked = chosen.Evidence.Contains(ResolutionEvidence.Deezer);
            // One album can be a coincidence of a common title ("Greatest Hits"); two, or the only album
            // the library has, or one plus the Deezer link, is the artist.
            var strong = best >= 2 || best * 2 >= ownedAlbums || linked;
            return Verdict(ArtistResolutionStatus.Resolved,
                $"Has {best} of the library's {ownedAlbums} album(s){(linked ? ", and is linked from the Deezer page" : "")}.",
                chosen,
                strong ? ResolutionConfidence.High : ResolutionConfidence.Medium);
        }

        // Nothing in the library to go on: either it owns no albums, or none of them are on any candidate.
        var noOverlap = ownedAlbums > 0
            ? $" None of the library's {ownedAlbums} album(s) are on it — a different act, or MusicBrainz is missing them."
            : "";

        var viaDeezer = candidates.Where(c => c.Evidence.Contains(ResolutionEvidence.Deezer)).ToList();
        if (viaDeezer.Count > 1)
        {
            return Verdict(ArtistResolutionStatus.Ambiguous,
                $"The Deezer page is linked from {viaDeezer.Count} MusicBrainz artists.{noOverlap}");
        }
        if (viaDeezer.Count == 1)
        {
            return Verdict(ArtistResolutionStatus.Resolved,
                "Linked from the Deezer page." + noOverlap,
                viaDeezer[0],
                ownedAlbums > 0 ? ResolutionConfidence.Low : ResolutionConfidence.Medium);
        }

        var named = candidates
            .Where(c => c.Evidence.Contains(ResolutionEvidence.Name) || c.Evidence.Contains(ResolutionEvidence.Alias))
            .ToList();
        if (named.Count > 1)
        {
            return Verdict(ArtistResolutionStatus.Ambiguous,
                $"{named.Count} MusicBrainz artists go by this name.{noOverlap}");
        }
        if (named.Count == 1)
        {
            return Verdict(ArtistResolutionStatus.Resolved,
                "Only the name matches." + noOverlap, named[0], ResolutionConfidence.Low);
        }

        // Only today's link is left, and it doesn't even go by the name any more.
        return Verdict(ArtistResolutionStatus.Resolved,
            "Only today's link points at it, and its name doesn't match." + noOverlap,
            candidates[0], ResolutionConfidence.Low);
    }

    /// <summary>
    /// The candidates that each hold a different share of the library's albums, strongest first — or
    /// null when the albums don't split that way. Two acts both having a "Greatest Hits" is one act's
    /// album twice, not a split; so a share must be disjoint from the leader's, and either as large as
    /// it or at least two albums (one shared common title is too thin to call it a second act).
    /// </summary>
    private static IReadOnlyList<ResolutionCandidate>? Mixed(IReadOnlyList<ResolutionCandidate> candidates)
    {
        var ranked = candidates
            .Where(c => c.AlbumOverlap > 0 && c.MatchedAlbums is { Count: > 0 })
            .OrderByDescending(c => c.AlbumOverlap)
            .ToList();
        if (ranked.Count < 2)
        {
            return null;
        }

        var leader = ranked[0];
        var claimed = new HashSet<string>(leader.MatchedAlbums!, StringComparer.OrdinalIgnoreCase);
        var shares = new List<ResolutionCandidate> { leader };
        foreach (var other in ranked.Skip(1))
        {
            var disjoint = !other.MatchedAlbums!.Any(claimed.Contains);
            if (disjoint && (other.AlbumOverlap >= 2 || other.AlbumOverlap == leader.AlbumOverlap))
            {
                shares.Add(other);
                claimed.UnionWith(other.MatchedAlbums!);
            }
        }
        return shares.Count > 1 ? shares : null;
    }

    private static string Label(ResolutionCandidate c) =>
        c.Disambiguation is { Length: > 0 } d ? $"{c.Name} ({d})" : c.Name ?? c.Mbid;

    private static string Albums(int n) => n == 1 ? "one" : n.ToString();

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
