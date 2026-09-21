using System.Text.RegularExpressions;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// Canonical matching for album titles across sources (Plex, Deezer). The shared definition of
/// "same album by title" — used by both the missing-album diff and the purchase reconcile so an
/// album can't be considered owned by one and missing by the other.
/// </summary>
public static partial class AlbumTitleMatcher
{
    /// <summary>
    /// Canonical form for one <em>listing</em>: typography folded (see <see cref="FoldTypography"/>),
    /// dotted initialisms collapsed ("E.P." → "ep"), featured-artist credits dropped (see
    /// <see cref="StripFeaturedCredits"/>), and a bare trailing format designator dropped (see
    /// <see cref="StripFormatDesignator"/>) — so "The Burgh Island E.P.", "The Burgh Island EP"
    /// and "The Burgh Island" all land on the same key, and so do "Titanium (feat. Sia)" and
    /// "Titanium".
    ///
    /// Edition decoration is kept here: "Both Sides (Deluxe Edition)" and "Both Sides (2015 Remaster)"
    /// are two keys, because they are two rows in an artist's discography with two Deezer ids, and a
    /// row a user can queue or block has to stay addressable on its own. Use this to ask "is this the
    /// same listing?" — deduping a catalog walk, keying a block or a merge.
    ///
    /// Use <see cref="NormalizeRecord"/> to ask "is this the same record?", which is what ownership
    /// turns on: Plex renames what it files, so the deluxe edition we downloaded sits in the library
    /// under the plain title, and asking at listing granularity would call an album we own missing.
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

        return StripFormatDesignator(StripFeaturedCredits(folded));
    }

    /// <summary>
    /// Canonical form for the <em>record</em> rather than the listing: everything <see cref="Normalize"/>
    /// folds, plus the trailing "which pressing is this" decoration — any trailing bracket, and a
    /// dash-separated or bare qualifier ("(Deluxe Edition)", "(Standard Version)", "- Remastered",
    /// "Deluxe Edition") — and a leading article, so "Light Upon the Lake (Deluxe Edition)"
    /// and "Light Upon the Lake" land on one key, as do "A Change Is Gonna Come" and "Change Is Gonna
    /// Come". The brackets that survive name a different performance — "(Live)", "(Remixes)" —
    /// which is a different record, not a different pressing (see <see cref="IsDistinctRecording"/>),
    /// or an anniversary reissue, which is a pressing but one wanted alongside the original (see
    /// <see cref="IsDistinctPressing"/>).
    ///
    /// This is what <em>ownership</em> is asked at, everywhere it is asked — the missing-album diff, the
    /// purchase reconcile, the Plex deep link, the upgrade swap. Plex names an album from its own
    /// metadata match and routinely drops the edition decoration (or folds the extra tracks into the
    /// album it already had), so "Watch the Throne (Deluxe)" is on disk as "Watch the Throne". Asked at
    /// listing granularity, a record we own reads as a gap, the diff re-offers it for ever, and a
    /// purchase row can never see its own download arrive.
    ///
    /// Folding pressings here does not hide any of them: the discography still lists every release
    /// separately (that dedup is <see cref="Normalize"/>'s job) and each keeps its own Deezer id. What
    /// record granularity decides is only whether the library already answers to that record.
    /// </summary>
    public static string NormalizeRecord(string? title) =>
        StripLeadingArticle(StripReleaseQualifiers(Normalize(title)));

    /// <summary>
    /// Canonical form for a <em>song</em> title: everything <see cref="Normalize"/> folds, plus the
    /// tails that say which cut of a song a release carries rather than which song it is — "(Radio
    /// Edit)", "- Single Version", "(Album Version)", "- Main Mix" — and the pressing decoration a
    /// track title can pick up alongside them. So the single's "Small Things - Radio Edit" and the
    /// album's "Small Things" land on one key.
    ///
    /// <para>This exists for one question: does an album already hold the song a single is selling
    /// (<see cref="StandaloneSingleAuditor"/>)? A label routinely trims or extends a track for single
    /// release and Deezer lists both cuts verbatim, so comparing raw titles would call a pre-release
    /// teaser a standalone record on the strength of two words in a bracket.</para>
    ///
    /// <para><b>Why this builds on <see cref="Normalize"/> and not
    /// <see cref="NormalizeRecord"/>.</b> Record granularity drops <em>any</em> trailing bracket that
    /// isn't a different performance, because whatever a source parenthesised it is still shelving the
    /// same record. That is right for a shelf and wrong for a song: "Hold On (Interlude)" and "Hold On"
    /// are two tracks, and folding them would let an album's interlude suppress a real single. So the
    /// bracket has to earn its removal here — every word in it a cut word, a pressing qualifier or
    /// filler, with at least one of the first two, the same all-words test
    /// <see cref="IsQualifier"/> applies to a dashed tail.</para>
    ///
    /// <para>The line <see cref="NormalizeRecord"/> draws is kept: a tail naming a different
    /// <em>performance</em> — "(Live)", "(Acoustic)", "(Remix)", see <see cref="IsDistinctRecording"/> —
    /// is not a cut of the same recording and survives. An album's live rendition therefore does not
    /// count as holding the studio single, which is the conservative direction: it leaves a genuinely
    /// unreleased studio recording offerable.</para>
    ///
    /// <para>No leading article is stripped, unlike <see cref="NormalizeRecord"/>. That fold exists to
    /// reconcile two <em>sources</em> that disagree about "The"; both sides of this comparison are
    /// Deezer track listings, so there is no disagreement to paper over and folding could only merge
    /// two songs that really are named differently.</para>
    /// </summary>
    public static string NormalizeTrack(string? title)
    {
        var current = Normalize(title);
        while (true)
        {
            // Re-normalize after each peel: one tail can hide another ("Song (Radio Edit) [Remastered]"
            // gives up the pressing bracket first and the cut second).
            var stripped = StripCutQualifier(current);
            if (stripped is null)
            {
                return current;
            }
            current = Normalize(stripped);
        }
    }

    /// <summary>
    /// One "which cut is this" tail peeled off a song title, or null when it ends in something that
    /// names the song. Only a trailing bracket or a free-standing " - " tail is considered — a bare
    /// unbracketed tail is left alone, because on the song axis there is no equivalent of "Glitterbug
    /// Deluxe Edition" and the guess would eat real titles.
    /// </summary>
    private static string? StripCutQualifier(string title)
    {
        if (title.Length == 0)
        {
            return null;
        }

        var close = title[^1];
        if (close is ')' or ']')
        {
            var open = title.LastIndexOf(close == ')' ? '(' : '[');
            if (open <= 0)
            {
                return null;
            }

            var bracketed = title.AsSpan(open + 1, title.Length - open - 2);
            return IsCutOrPressing(bracketed) ? KeepCut(title.AsSpan(0, open)) : null;
        }

        var dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && IsCutOrPressing(title.AsSpan(dash + 3)))
        {
            return KeepCut(title.AsSpan(0, dash));
        }

        return null;

        // A strip that would leave nothing behind means the "qualifier" was the title all along.
        static string? KeepCut(ReadOnlySpan<char> remainder)
        {
            var kept = remainder.TrimEnd();
            return kept.IsEmpty ? null : kept.ToString();
        }
    }

    /// <summary>
    /// Whether a tail describes the cut or the pressing rather than the song — and is not a different
    /// performance, which is checked first and always wins.
    /// </summary>
    private static bool IsCutOrPressing(ReadOnlySpan<char> tail) =>
        !IsDistinctRecording(tail) && (IsCut(tail) || IsQualifier(tail));

    /// <summary>
    /// Whether a tail says which cut of a song a release carries: every word is a cut word, a pressing
    /// qualifier or filler, and at least one is a cut word. The all-words test is what keeps a real
    /// title fragment — "(Radio Silence)" — from reading as decoration on the strength of one word.
    /// </summary>
    private static bool IsCut(ReadOnlySpan<char> tail)
    {
        var trimmed = tail.Trim();
        var sawCut = false;
        foreach (var range in trimmed.Split(' '))
        {
            var word = trimmed[range].Trim(",.-'\"");
            if (word.IsEmpty)
            {
                continue;
            }

            var text = word.ToString();
            if (Cuts.Contains(text))
            {
                sawCut = true;
                continue;
            }

            // Numbers and ordinals carry no identity of their own, same as in a pressing tail.
            if (char.IsAsciiDigit(word[0]))
            {
                continue;
            }

            if (!Qualifiers.Contains(text) && !Filler.Contains(text))
            {
                return false;
            }
        }

        return sawCut;
    }

    /// <summary>
    /// The shared typography fold — see <see cref="TypographyFold"/>. Kept as a private alias so the
    /// title rules below read unchanged, while artist names go through the very same fold via
    /// <see cref="NormalizeArtist"/> and <see cref="ArtistNameComparer"/>.
    /// </summary>
    private static string FoldTypography(string? title) => TypographyFold.Apply(title);

    /// <summary>
    /// Canonical form for an <em>artist</em> name: the typography fold and nothing else. No qualifier
    /// or article stripping — an act called "The Band" is not the act called "Band", and unlike an
    /// album pressing no source lists one act under both spellings.
    ///
    /// <para>This exists because ownership asks the artist question <em>first</em>: every check does
    /// <c>owned.TryGetValue(artist)</c> before it ever compares a title, so an unfolded artist name
    /// short-circuits the whole match. The album axis has been folded since the beginning; this is the
    /// other half.</para>
    /// </summary>
    public static string NormalizeArtist(string? name) => TypographyFold.ForArtist(name);

    /// <summary>
    /// Drops featured-artist credits — "(feat. Sia)", "[ft. Kendrick Lamar]", "(featuring Nate Dogg)" —
    /// wherever they sit in the title. A credit is never what distinguishes one release from another:
    /// sources disagree only on whether to write it at all, so "Titanium (feat. Sia)" and "Titanium"
    /// are one listing, not two. Stripped at listing granularity for that reason — unlike edition
    /// decoration, no source lists both spellings as two rows a user could act on separately.
    /// Never strips the whole title: a release actually called "(feat. Sia)" keeps its name.
    /// </summary>
    private static string StripFeaturedCredits(string title)
    {
        if (title.IndexOf('(') < 0 && title.IndexOf('[') < 0)
        {
            return title;
        }

        var stripped = FeaturedCredit().Replace(title, string.Empty).Trim();
        return stripped.Length == 0 ? title : stripped;
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

    /// <summary>
    /// Drops the trailing "which pressing is this" decoration sources disagree on — any trailing
    /// bracket that isn't a different performance or an anniversary reissue, plus a dash-separated or
    /// unbracketed qualifier ("(Deluxe Edition)", "[Remastered]", "- Remastered", "Deluxe Edition") —
    /// repeatedly, so "Every Kingdom (Deluxe Edition) [Remastered]" reduces to "every kingdom". Never
    /// strips the whole title: an album actually called "Deluxe" keeps its name.
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
        if (title.Length == 0)
        {
            return null;
        }

        // "... (deluxe edition)" / "... [remastered]" / "... (standard version)". A trailing bracket is
        // decoration by default: whatever a source parenthesised, it is still shelving the same record,
        // so the tail only survives when it names a different performance (see IsDistinctRecording) or
        // an anniversary reissue (see IsDistinctPressing).
        var close = title[^1];
        if (close is ')' or ']')
        {
            var open = title.LastIndexOf(close == ')' ? '(' : '[');
            var bracketed = title.AsSpan(open + 1, title.Length - open - 2);
            if (open > 0 && !IsDistinctRecording(bracketed) && !IsDistinctPressing(bracketed))
            {
                return Keep(title.AsSpan(0, open));
            }
            return null;
        }

        // "... - remastered". The typography fold has already turned en/em dashes into hyphens, and
        // the dash has to be free-standing so hyphenated titles ("Post-Nothing") are left alone.
        var dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && IsQualifier(title.AsSpan(dash + 3)) && !IsDistinctPressing(title.AsSpan(dash + 3)))
        {
            return Keep(title.AsSpan(0, dash));
        }

        // The same decoration written without punctuation ("Glitterbug Deluxe Edition"), which also
        // covers a bare format designator that survived Normalize's single pass ("... deluxe ep").
        var bare = BareQualifierTailStart(title);
        if (bare > 0 && !IsDistinctPressing(title.AsSpan(bare)))
        {
            return Keep(title.AsSpan(0, bare));
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
    /// Where the trailing run of unbracketed qualifier words starts ("Glitterbug Deluxe Edition" → the
    /// index of the space before "Deluxe"), or -1 when the title doesn't end in one. The run has to
    /// hold a qualifier — a tail of pure filler ("Songs of the") is just the end of a title — and the
    /// first word is never eaten, so a release called "Deluxe" keeps its name.
    /// </summary>
    private static int BareQualifierTailStart(string title)
    {
        var start = title.Length;
        var sawQualifier = false;
        while (start > 0)
        {
            var space = title.LastIndexOf(' ', start - 1);
            // space == 0 would leave nothing in front of the run; a strip has to leave a title behind.
            if (space <= 0)
            {
                break;
            }

            var word = title.AsSpan(space + 1, start - space - 1).Trim(",.-'\"");
            if (word.IsEmpty)
            {
                break;
            }

            if (Qualifiers.Contains(word.ToString()))
            {
                sawQualifier = true;
            }
            // Numbers and ordinals ("10th", "2019") carry no identity of their own, same as in a
            // bracketed tail; anything else is the title itself and ends the run.
            else if (!char.IsAsciiDigit(word[0]) && !Filler.Contains(word.ToString()))
            {
                break;
            }

            start = space;
        }

        return sawQualifier ? start : -1;
    }

    /// <summary>
    /// Drops a leading article — sources disagree on whether the record is "A Change Is Gonna Come" or
    /// "Change Is Gonna Come", and a reissue routinely drops the article the original carried ("An
    /// Awesome Wave"). Repeated, so "The A Team" and "A Team" meet in the middle. Never strips the
    /// whole title: a record called "The" keeps its name.
    /// </summary>
    private static string StripLeadingArticle(string title)
    {
        while (true)
        {
            var stripped = StripArticleOnce(title);
            if (stripped is null)
            {
                return title;
            }
            title = stripped;
        }

        static string? StripArticleOnce(string title)
        {
            foreach (var article in Articles)
            {
                if (!title.StartsWith(article, StringComparison.Ordinal))
                {
                    continue;
                }

                var kept = title.AsSpan(article.Length).TrimStart();
                return kept.IsEmpty ? null : kept.ToString();
            }

            return null;
        }
    }

    /// <summary>
    /// Whether a bracketed tail names a different performance of the songs — "(Live)", "(Remixes)",
    /// "(Acoustic)" — rather than a different pressing of the same one. Those stay separate records at
    /// every granularity: owning the studio album is not owning the live one, and a download of one
    /// must not satisfy a purchase of the other. Everything else in a trailing bracket is dropped at
    /// record granularity, because sources parenthesise the same record every way there is.
    /// </summary>
    private static bool IsDistinctRecording(ReadOnlySpan<char> tail)
    {
        var trimmed = tail.Trim();
        foreach (var range in trimmed.Split(' '))
        {
            var word = trimmed[range].Trim(",.-'\"");
            if (!word.IsEmpty && DistinctRecordings.Contains(word.ToString()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a pressing tail names a pressing distinct enough to count as a record of its own —
    /// "(25th Anniversary Edition)" — so owning the original doesn't answer for it, and it gets its
    /// own feed card instead of hiding behind the original as an alternate pressing. Record
    /// granularity only: on the song axis an anniversary remaster is still the same song.
    /// </summary>
    private static bool IsDistinctPressing(ReadOnlySpan<char> tail)
    {
        var trimmed = tail.Trim();
        foreach (var range in trimmed.Split(' '))
        {
            var word = trimmed[range].Trim(",.-'\"");
            if (!word.IsEmpty && DistinctPressings.Contains(word.ToString()))
            {
                return true;
            }
        }

        return false;
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

    /// <summary>
    /// Words that mark a pressing rather than a record. Deliberately excludes the ones that make a
    /// genuinely different release — "live", "acoustic", "demo", "remix", "instrumental" — those
    /// stay distinct albums even at record granularity.
    /// </summary>
    private static readonly HashSet<string> Qualifiers = new(StringComparer.Ordinal)
    {
        "deluxe", "edition", "editions", "remaster", "remastered", "remastering", "anniversary",
        "expanded", "extended", "reissue", "bonus", "special", "limited", "collector", "collectors",
        "collector's", "version", "explicit", "clean", "mono", "stereo", "ep", "lp", "single",
        "digipak", "definitive", "original", "standard",
    };

    /// <summary>
    /// Words that make a bracketed tail a different recording rather than a different pressing. The
    /// mirror image of <see cref="Qualifiers"/>: these are the only trailing brackets record granularity
    /// keeps.
    /// </summary>
    private static readonly HashSet<string> DistinctRecordings = new(StringComparer.Ordinal)
    {
        "live", "acoustic", "unplugged", "demo", "demos", "remix", "remixes", "remixed",
        "instrumental", "instrumentals", "karaoke", "cover", "covers", "session", "sessions",
    };

    /// <summary>
    /// Pressing words that nonetheless keep a tail at record granularity (see
    /// <see cref="IsDistinctPressing"/>). An anniversary reissue is usually a substantially different
    /// package — remastered, expanded with demos and outtakes — that people want alongside the original.
    /// </summary>
    private static readonly HashSet<string> DistinctPressings = new(StringComparer.Ordinal)
    {
        "anniversary",
    };

    /// <summary>
    /// Words that mark which cut of a song a release carries rather than which song it is. Used only by
    /// <see cref="NormalizeTrack"/> — an album called "Radio" is a record, while a track tail of "Radio
    /// Edit" is the same song as the album version. Deliberately disjoint from
    /// <see cref="DistinctRecordings"/>, which is checked first: an "Extended Mix" is a cut, a "Remix"
    /// is a different recording.
    /// </summary>
    private static readonly HashSet<string> Cuts = new(StringComparer.Ordinal)
    {
        "radio", "edit", "edits", "mix", "main", "album", "single", "original", "version", "cut",
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

    /// <summary>Leading words with no identity of their own, space included so a prefix match is a whole word.</summary>
    private static readonly string[] Articles = ["the ", "a ", "an "];

    /// <summary>A bracketed featured-artist credit: "(feat. Sia)", "[ft. Sia]", "(featuring Sia)".</summary>
    [GeneratedRegex(@"\s*[\(\[]\s*(?:feat|ft|featuring)\b\.?[^)\]]*[\)\]]")]
    private static partial Regex FeaturedCredit();

    /// <summary>A run of at least two single-letter-plus-dot pairs: the "E.P." / "M.I.A." shape.</summary>
    [GeneratedRegex(@"(?<![a-z0-9])(?:[a-z]\.){2,}")]
    private static partial Regex InitialismDots();
}
