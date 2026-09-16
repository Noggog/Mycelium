using System.Globalization;
using System.Text;
using Mycelium.ListenBrainz.Models;

namespace Mycelium.ListenBrainz.Services;

/// <summary>
/// Decides whether a MusicBrainz search hit actually <em>is</em> the artist a library name means.
///
/// <para><b>Why the top hit isn't good enough.</b> MusicBrainz scores loosely, and an artist it
/// doesn't know still gets a confident answer: "Noel Brass Jr." comes back as Canadian Brass at a
/// score of 100. Taking that on trust hands the obscure act a Christmas-music act's ListenBrainz
/// neighbours, and every recommendation grown from them. A wrong MBID is invisible and sticky; a
/// missing one only costs a similarity source for one artist. So a hit is accepted only when a name
/// it goes by matches the one asked for — its own, or one of its aliases (which is how "NSYNC"
/// finds "*NSYNC") — and otherwise the answer is no match.</para>
///
/// <para><b>Two tiers of "matches".</b> <see cref="Normalize"/> forgives case, width, whitespace and
/// typographic dashes and quotes, which are spelling noise rather than a different name.
/// <see cref="Loose"/> additionally drops diacritics, punctuation and a leading "the", and reads "&amp;"
/// as "and" — still the same name as far as anyone typing it is concerned ("Beyonce", "AC-DC", "The
/// Beatles" vs. "Beatles"). Primary names beat aliases and strict beats loose, so a same-named act is
/// never passed over for one that only matches on a technicality; within a tier MusicBrainz's own
/// relevance order breaks the tie.</para>
/// </summary>
public static class MusicBrainzArtistMatch
{
    /// <summary>How many hits to ask a search for: enough for the real act to be among them when a
    /// bigger namesake outscores it, and still a single request.</summary>
    public const int SearchCandidates = 10;

    /// <summary>The first candidate (in relevance order) that goes by <paramref name="artistName"/>, or null.</summary>
    public static MusicBrainzArtist? Pick(IEnumerable<MusicBrainzArtist> candidates, string artistName)
    {
        var usable = candidates.Where(c => c.Id is { Length: > 0 }).ToList();
        var strict = Normalize(artistName);
        var loose = Loose(artistName);

        return usable.FirstOrDefault(c => Normalize(c.Name) == strict)
               ?? usable.FirstOrDefault(c => Aliases(c).Any(a => Normalize(a) == strict))
               ?? (loose.Length == 0
                   ? null
                   : usable.FirstOrDefault(c => Loose(c.Name) == loose)
                     ?? usable.FirstOrDefault(c => Aliases(c).Any(a => Loose(a) == loose)));
    }

    /// <summary>
    /// Whether a stored MusicBrainz name is plausibly the library name — the offline check that lets a
    /// relink pass skip the search for every link that was right all along. Loose on purpose: a stored
    /// identity only kept its <em>primary</em> name, so an alias match can't be re-proven here, and
    /// anything this lets through was at least a near-spelling of the name asked for.
    /// </summary>
    public static bool NamesMatch(string? musicBrainzName, string artistName)
    {
        var loose = Loose(artistName);
        return Normalize(musicBrainzName) == Normalize(artistName)
               || (loose.Length > 0 && Loose(musicBrainzName) == loose);
    }

    private static IEnumerable<string?> Aliases(MusicBrainzArtist artist) =>
        artist.Aliases?.Select(a => a.Name) ?? Enumerable.Empty<string?>();

    /// <summary>Case, width, whitespace, and typographic dash/quote differences folded away.</summary>
    internal static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Normalize(NormalizationForm.FormKC))
        {
            sb.Append(ch switch
            {
                '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' => '-',
                '‘' or '’' or 'ʼ' or '`' => '\'',
                '“' or '”' => '"',
                _ => char.ToLowerInvariant(ch),
            });
        }

        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// <see cref="Normalize"/> plus: diacritics dropped, "&amp;" read as "and", a leading "the" dropped,
    /// and only letters and digits kept. Empty for a name that is all punctuation ("!!!"), which the
    /// caller must treat as "no loose form" rather than as a match for every other such name.
    /// </summary>
    internal static string Loose(string? name)
    {
        var normalized = Normalize(name).Replace("&", " and ");
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }

        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0] == "the")
        {
            words.RemoveAt(0);
        }
        return string.Concat(words);
    }
}
