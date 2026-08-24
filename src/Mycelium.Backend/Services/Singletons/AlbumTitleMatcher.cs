using System.Text;
using System.Text.RegularExpressions;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// Canonical matching for album titles across sources (Plex, Deezer). The shared definition of
/// "same album by title" — used by both the missing-album diff and the purchase reconcile so an
/// album can't be considered owned by one and missing by the other.
/// </summary>
public static partial class AlbumTitleMatcher
{
    /// <summary>
    /// Canonical form for matching album titles across sources: typography folded (see
    /// <see cref="FoldTypography"/>), dotted initialisms collapsed ("E.P." → "ep"), and a bare trailing
    /// format designator dropped (see <see cref="StripFormatDesignator"/>) — so "The Burgh Island E.P.",
    /// "The Burgh Island EP" and "The Burgh Island" all land on the same key.
    ///
    /// Edition decoration is deliberately <em>kept</em>: "Every Kingdom (Deluxe Edition)" and "Every
    /// Kingdom" are two keys, not one. Deezer lists them as two releases with two ids, the discography
    /// shows them as two rows, and a user who queues or blocks one of those rows means that row — so
    /// nothing downstream may quietly treat them as the same thing. Owning the plain edition therefore
    /// leaves the deluxe reading as missing, and that is the intended answer rather than a miss: it is
    /// a release we don't have, offered like any other and declined with a dismiss or a block if it
    /// isn't wanted. What this folds is one release written two ways; what it must never fold is two
    /// releases.
    /// </summary>
    public static string Normalize(string? title)
    {
        var folded = FoldTypography(title);
        if (folded.Length == 0)
        {
            return string.Empty;
        }

        // "e.p." → "ep", "m.i.a." → "mia". Done after the typography fold so the input is already
        // lower-cased and space-collapsed; "mr. bungle" is untouched (a lone initial isn't a run).
        folded = InitialismDots().Replace(folded, m => m.Value.Replace(".", string.Empty));

        return StripFormatDesignator(folded);
    }

    /// <summary>
    /// Trimmed, lower-cased, with curly quotes/apostrophes and en/em dashes folded to ASCII,
    /// zero-width characters stripped, ampersands spelled out as "and", and internal whitespace
    /// collapsed — so a title that differs only in typography (Plex's "Don't" vs. Deezer's "Don't")
    /// or in the ampersand convention (Plex's "Radiance &amp; Submission" vs. Deezer's "Radiance and
    /// Submission") still matches.
    /// </summary>
    private static string FoldTypography(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(title.Length);
        // A separator is owed before the next character we emit. Starts false so leading whitespace
        // is dropped, and is never flushed at the end so trailing whitespace is too.
        var pendingSpace = false;
        foreach (var ch in title)
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
                '–' or '—' => '-',                          // en/em dash
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

    /// <summary>
    /// Drops a bare trailing format designator — "The Old Pine EP" is the same release as "The Old
    /// Pine", just written the way one source happens to write it. This is the only decoration worth
    /// folding: unlike "(Deluxe Edition)" or "- Remastered", no source lists both spellings as two
    /// releases, so collapsing them can never merge two things a user could act on separately. Never
    /// strips the whole title: an album actually called "EP" keeps its name.
    /// </summary>
    private static string StripFormatDesignator(string title)
    {
        var space = title.LastIndexOf(' ');
        if (space <= 0 || !IsFormat(title.AsSpan(space + 1)))
        {
            return title;
        }

        var kept = title.AsSpan(0, space).TrimEnd();
        return kept.IsEmpty ? title : kept.ToString();
    }

    /// <summary>Whether a bare trailing word is a format designator ("EP", "LP") rather than part of the title.</summary>
    private static bool IsFormat(ReadOnlySpan<char> word) =>
        word.Equals("ep", StringComparison.Ordinal) || word.Equals("lp", StringComparison.Ordinal);

    /// <summary>A run of at least two single-letter-plus-dot pairs: the "E.P." / "M.I.A." shape.</summary>
    [GeneratedRegex(@"(?<![a-z0-9])(?:[a-z]\.){2,}")]
    private static partial Regex InitialismDots();
}
