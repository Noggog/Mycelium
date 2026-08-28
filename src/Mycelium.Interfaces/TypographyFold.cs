using System.Text;

namespace Mycelium.Interfaces;

/// <summary>
/// The shared typography fold: trimmed, lower-cased, with curly quotes/apostrophes and every
/// Unicode dash folded to ASCII, zero-width characters stripped, ampersands spelled out as "and",
/// and internal whitespace collapsed.
///
/// <para>Lives here rather than beside the album matcher because <em>both</em> axes of a match need
/// it. Album titles have always had it; artist names had nothing, which is how Plex's
/// "Sophie Ellis&#x2010;Bextor" (U+2010 HYPHEN) failed to meet Deezer's "Sophie Ellis-Bextor"
/// (U+002D HYPHEN-MINUS) and an owned record read as a gap. The artist is looked up <em>first</em>,
/// so a miss there short-circuits the title comparison entirely and no amount of title
/// normalisation can rescue it.</para>
/// </summary>
public static class TypographyFold
{
    public static string Apply(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        // A separator is owed before the next character we emit. Starts false so leading whitespace
        // is dropped, and is never flushed at the end so trailing whitespace is too.
        var pendingSpace = false;
        foreach (var ch in value)
        {
            switch (ch)
            {
                // Zero-width and BOM characters: drop entirely (often pasted/copied invisibly).
                case '​' or '‌' or '‍' or '﻿':
                    continue;
            }

            var c = ch switch
            {
                '‘' or '’' or 'ʼ' or '′' => '\'', // curly/modifier apostrophes, prime
                '“' or '”' => '"',                          // curly double quotes
                // Every dash Unicode offers, not just en/em. Sources disagree freely on which one a
                // hyphenated name gets, and they are visually indistinguishable, so a mismatch here
                // is invisible in every UI that would let you spot it.
                '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' => '-',
                _ => char.ToLowerInvariant(ch),
            };

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (c is '&' or '＆')
            {
                // Spelled out and space-padded on both sides, so "R&B", "R & B" and "R and B" all
                // land on the same form regardless of which side of the swap each source wrote.
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }
                sb.Append("and");
                pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
        }

        return sb.ToString();
    }
}

/// <summary>
/// Compares artist names the way a match should: through <see cref="TypographyFold"/>, so the two
/// spellings of one act meet. Used as the comparer on every artist-keyed ownership dictionary, which
/// is what makes the fix reach every consumer — the purchase reconcile, the missing-album diff, the
/// discography, the Plex deep link — instead of only the call site that noticed.
///
/// <para>Two genuinely different acts can fold together (they would have to differ only in
/// typography), so builders of these dictionaries <b>merge</b> on collision rather than overwrite:
/// losing an artist's albums to a near-twin is the one way this could make matching worse.</para>
/// </summary>
public sealed class ArtistNameComparer : IEqualityComparer<string>
{
    public static readonly ArtistNameComparer Instance = new();

    private ArtistNameComparer()
    {
    }

    public bool Equals(string? x, string? y) =>
        string.Equals(TypographyFold.Apply(x), TypographyFold.Apply(y), StringComparison.Ordinal);

    public int GetHashCode(string obj) => TypographyFold.Apply(obj).GetHashCode();
}
