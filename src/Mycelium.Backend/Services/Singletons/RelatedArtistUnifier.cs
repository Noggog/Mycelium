using System.Globalization;
using System.Text;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// Merges per-source related-artist edge sets into one cross-source list: one entry per distinct
/// artist, tagged with every source that recommended it. Pure (no I/O) so it's unit-testable.
/// </summary>
public static class RelatedArtistUnifier
{
    /// <summary>
    /// How many of each source's edges count. Sources return their lists best-first and the tail is
    /// weak signal — ListenBrainz hands back 100, and a popular act sitting in the bottom half of
    /// many liked artists' lists would otherwise out-score a genuine top match. Deezer caps at 20
    /// on its own. Hand-entered pairs are deliberate, so they're never trimmed.
    /// </summary>
    public const int MaxEdgesPerSource = 30;

    public static IReadOnlyList<UnifiedRelatedArtist> Unify(IReadOnlyList<ArtistRelations> perSource)
    {
        // Dedupe on a normalized key (case- and diacritic-insensitive) so the same artist spelled
        // slightly differently across sources — "Beyoncé" vs "Beyonce", "MØ" vs "MO" — collapses to
        // one entry instead of two. Keep the first encountered display name (verbatim) + first
        // non-null image, and collect the distinct sources per artist.
        var merged = new Dictionary<string, (string Name, string? Image, List<string> Sources)>();

        foreach (var source in perSource)
        {
            var edges = source.Source == ManualRecommendations.SourceName
                ? source.Related
                : source.Related.Take(MaxEdgesPerSource);
            foreach (var related in edges)
            {
                var name = related.ArtistKey.ArtistName;
                var key = NormalizeKey(name);
                if (!merged.TryGetValue(key, out var entry))
                {
                    entry = (name, related.ImageUrl, new List<string>());
                }
                entry.Image ??= related.ImageUrl;
                if (!entry.Sources.Contains(source.Source))
                {
                    entry.Sources.Add(source.Source);
                }
                merged[key] = entry;
            }
        }

        return merged.Values
            .Select(e => new UnifiedRelatedArtist(new ArtistKey(e.Name), e.Image, e.Sources))
            .OrderBy(r => r.ArtistKey.ArtistName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// The merge key for an artist name: trimmed, lower-cased, and with diacritics stripped (via
    /// Unicode decomposition, dropping the combining marks). Purely a dedupe key — the original
    /// spelling is what's shown to the user.
    /// </summary>
    internal static string NormalizeKey(string name)
    {
        var decomposed = name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
