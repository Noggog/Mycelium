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
    /// <see cref="FoldTypography"/>), dotted initialisms collapsed ("E.P." → "ep"), and trailing
    /// release qualifiers dropped (see <see cref="StripReleaseQualifiers"/>) — so "The Burgh Island
    /// E.P.", "The Burgh Island EP" and "The Burgh Island" all land on the same key, as do "Every
    /// Kingdom (Deluxe Edition)" and "Every Kingdom".
    /// </summary>
    public static string Normalize(string? title)
    {
        var edition = NormalizeEdition(title);
        return edition.Length == 0 ? string.Empty : StripReleaseQualifiers(edition);
    }

    /// <summary>
    /// Canonical form that still tells pressings apart: the same typography fold and dotted-initialism
    /// collapse as <see cref="Normalize"/>, with the release qualifiers left on. "Both Sides (Deluxe
    /// Edition)" and "Both Sides (2015 Remaster)" keep distinct keys here — they are two rows in an
    /// artist's discography — while a title a source lists twice in different typography still collapses
    /// to one. Use this to ask "is this the same listing?"; use <see cref="Normalize"/> to ask "is this
    /// the same record?", which is what ownership turns on.
    /// </summary>
    public static string NormalizeEdition(string? title)
    {
        var folded = FoldTypography(title);
        if (folded.Length == 0)
        {
            return string.Empty;
        }

        // "e.p." → "ep", "m.i.a." → "mia". Done after the typography fold so the input is already
        // lower-cased and space-collapsed; "mr. bungle" is untouched (a lone initial isn't a run).
        return InitialismDots().Replace(folded, m => m.Value.Replace(".", string.Empty));
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
    /// Drops the trailing "which pressing is this" decoration sources disagree on — a bracketed or
    /// dash-separated qualifier ("(Deluxe Edition)", "[10th Anniversary Deluxe]", "- Remastered") or
    /// a bare format designator ("EP", "LP") — repeatedly, so "Every Kingdom (Deluxe Edition)
    /// [Remastered]" reduces to "every kingdom". Never strips the whole title: an album actually
    /// called "EP" or "Deluxe" keeps its name.
    /// </summary>
    private static string StripReleaseQualifiers(string title)
    {
        while (true)
        {
            var stripped = StripOnce(title);
            if (stripped is null)
            {
                return title;
            }
            title = stripped;
        }
    }

    /// <summary>One qualifier peeled off the end, or null when the title ends in something meaningful.</summary>
    private static string? StripOnce(string title)
    {
        // "... (deluxe edition)" / "... [remastered]"
        var close = title[^1];
        if (close is ')' or ']')
        {
            var open = title.LastIndexOf(close == ')' ? '(' : '[');
            if (open > 0 && IsQualifier(title.AsSpan(open + 1, title.Length - open - 2)))
            {
                return Keep(title.AsSpan(0, open));
            }
            return null;
        }

        // "... - remastered". The typography fold has already turned en/em dashes into hyphens, and
        // the dash has to be free-standing so hyphenated titles ("Post-Nothing") are left alone.
        var dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && IsQualifier(title.AsSpan(dash + 3)))
        {
            return Keep(title.AsSpan(0, dash));
        }

        // A bare trailing format designator: "The Old Pine EP" is the same record as "The Old Pine".
        var space = title.LastIndexOf(' ');
        if (space > 0 && IsFormat(title.AsSpan(space + 1)))
        {
            return Keep(title.AsSpan(0, space));
        }

        return null;

        // A strip that would leave nothing behind means the "qualifier" was the title all along.
        static string? Keep(ReadOnlySpan<char> remainder)
        {
            var kept = remainder.TrimEnd();
            return kept.IsEmpty ? null : kept.ToString();
        }
    }

    /// <summary>
    /// Whether a trailing bracketed/dashed tail describes the pressing rather than the record: every
    /// word is either a qualifier ("deluxe", "remastered", "anniversary") or filler that only shows up
    /// alongside one ("10th", "the", "bonus track" and friends), and at least one is a qualifier. The
    /// all-words test is what keeps a real title fragment — "(Clean Slate)", "(Live in Tokyo)" — from
    /// being mistaken for decoration on the strength of a single word.
    /// </summary>
    private static bool IsQualifier(ReadOnlySpan<char> tail)
    {
        var sawQualifier = false;
        foreach (var range in tail.Trim().Split(' '))
        {
            var word = tail.Trim()[range].Trim(",.-'\"");
            if (word.IsEmpty)
            {
                continue;
            }

            if (Qualifiers.Contains(word.ToString()))
            {
                sawQualifier = true;
                continue;
            }

            // Numbers and ordinals ("10th", "2019") carry no identity of their own here.
            if (char.IsAsciiDigit(word[0]))
            {
                continue;
            }

            if (!Filler.Contains(word.ToString()))
            {
                return false;
            }
        }

        return sawQualifier;
    }

    /// <summary>Whether a bare trailing word is a format designator ("EP", "LP") rather than part of the title.</summary>
    private static bool IsFormat(ReadOnlySpan<char> word) =>
        word.Equals("ep", StringComparison.Ordinal) || word.Equals("lp", StringComparison.Ordinal);

    /// <summary>
    /// Words that mark a pressing rather than a record. Deliberately excludes the ones that make a
    /// genuinely different release — "live", "acoustic", "demo", "remix", "instrumental" — those
    /// stay distinct albums.
    /// </summary>
    private static readonly HashSet<string> Qualifiers = new(StringComparer.Ordinal)
    {
        "deluxe", "edition", "editions", "remaster", "remastered", "remastering", "anniversary",
        "expanded", "extended", "reissue", "bonus", "special", "limited", "collector", "collectors",
        "collector's", "version", "explicit", "clean", "mono", "stereo", "ep", "lp", "single",
        "digipak", "definitive", "original",
    };

    /// <summary>Words with no identity of their own — allowed in a qualifier tail, never enough to make one.</summary>
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "of", "with", "plus", "in", "track", "tracks", "disc", "disk",
        "cd", "cds", "vinyl", "digital", "audio", "year", "years", "st", "nd", "rd", "th",
        // Spelled-out ordinals ("Tenth Anniversary Edition") carry the same no-identity role as a
        // digit ordinal ("10th Anniversary") — the digit form is caught separately by IsQualifier's
        // leading-digit check, so only the word form needs listing here.
        "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth",
        "eleventh", "twelfth", "thirteenth", "fourteenth", "fifteenth", "sixteenth", "seventeenth",
        "eighteenth", "nineteenth", "twentieth", "thirtieth", "fortieth", "fiftieth", "sixtieth",
        "seventieth", "eightieth", "ninetieth", "hundredth",
        "twenty-fifth", "thirty-fifth", "forty-fifth", "fifty-fifth", "seventy-fifth",
    };

    /// <summary>A run of at least two single-letter-plus-dot pairs: the "E.P." / "M.I.A." shape.</summary>
    [GeneratedRegex(@"(?<![a-z0-9])(?:[a-z]\.){2,}")]
    private static partial Regex InitialismDots();
}
