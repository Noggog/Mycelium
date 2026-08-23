namespace Mycelium.Plex.Services.Smart;

/// <summary>
/// Reads a smart playlist's stored rules back into a <see cref="PlexSmartFilter"/> — the inverse of
/// <see cref="PlexFilterSerializer"/>.
///
/// <para>Needed because "does this playlist already exist?" is answered by comparing <em>rules</em>,
/// not names: the user may have built the same playlist by hand and called it anything. Both sides go
/// through this parser and then <see cref="PlexFilterCanonicalizer"/>, so the comparison is of meaning
/// rather than of spelling.</para>
/// </summary>
public static class PlexFilterParser
{
    /// <summary>
    /// Operator spellings as they appear at the end of a decoded param name, longest first so that
    /// <c>field!=</c> (string "is not") is never mistaken for <c>field!</c> (tag "is not") followed by a
    /// stray character, and <c>field&gt;&gt;</c> never for <c>field&gt;</c>.
    /// </summary>
    private static readonly (string Suffix, PlexOp Op)[] Operators =
    {
        ("!=", PlexOp.StringIsNot),
        ("!", PlexOp.IsNot),
        (">>", PlexOp.GreaterThan),
        ("<<", PlexOp.LessThan),
        (">", PlexOp.EndsWith),
        ("<", PlexOp.BeginsWith),
        ("=", PlexOp.StringIs),
    };

    /// <summary>
    /// Query params that are never rules. <c>type</c> is lifted onto the record; the rest are carried
    /// through <see cref="PlexSmartFilter.Options"/> untouched.
    /// </summary>
    private static readonly HashSet<string> StackTokens = new(StringComparer.Ordinal)
        { "push", "pop", "and", "or" };

    /// <summary>
    /// Parses the query portion of a stored filter (everything after <c>?</c> in the decoded
    /// <c>content</c> URI). Throws <see cref="FormatException"/> on unbalanced <c>push</c>/<c>pop</c>.
    /// </summary>
    public static PlexSmartFilter Parse(string query)
    {
        var type = PlexSmartFilter.ArtistType;
        var options = new List<KeyValuePair<string, string>>();

        var stack = new Stack<Frame>();
        var current = new Frame();

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            var rawName = split < 0 ? pair : pair[..split];
            var rawValue = split < 0 ? "" : pair[(split + 1)..];
            var name = Uri.UnescapeDataString(rawName);

            if (StackTokens.Contains(name))
            {
                switch (name)
                {
                    case "push":
                        stack.Push(current);
                        current = new Frame();
                        break;
                    case "pop":
                        if (stack.Count == 0)
                        {
                            throw new FormatException($"Unbalanced pop in smart filter: {query}");
                        }

                        var closed = current.Build();
                        current = stack.Pop();
                        if (closed is not null)
                        {
                            current.Items.Add(closed);
                        }

                        break;
                    default:
                        current.Join ??= name == "or" ? PlexJoin.Or : PlexJoin.And;
                        break;
                }

                continue;
            }

            if (name == "type")
            {
                type = int.TryParse(rawValue, out var t) ? t : type;
                continue;
            }

            if (TryReadCondition(name, rawValue, out var condition))
            {
                current.Items.Add(condition);
            }
            else
            {
                // Not a rule (no operator suffix and no field scope) — sort/limit/group/having. Kept
                // raw so writing the filter back reproduces it exactly, encoding included.
                options.Add(new KeyValuePair<string, string>(rawName, rawValue));
            }
        }

        if (stack.Count > 0)
        {
            throw new FormatException($"Unbalanced push in smart filter: {query}");
        }

        return new PlexSmartFilter(type, current.Build(), options);
    }

    /// <summary>
    /// Splits a decoded param name into field + operator. A param counts as a rule when it carries an
    /// operator suffix (<c>track.viewCount&gt;&gt;</c>) or names a scoped field (<c>track.viewCount</c>);
    /// anything else — <c>sort</c>, <c>limit</c>, <c>group</c>, <c>having</c> — is a query option.
    /// </summary>
    private static bool TryReadCondition(string name, string rawValue, out PlexCondition condition)
    {
        foreach (var (suffix, op) in Operators)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                condition = new PlexCondition(
                    name[..^suffix.Length], op, Uri.UnescapeDataString(rawValue));
                return true;
            }
        }

        if (name.Contains('.'))
        {
            condition = new PlexCondition(name, PlexOp.Is, Uri.UnescapeDataString(rawValue));
            return true;
        }

        condition = null!;
        return false;
    }

    /// <summary>
    /// Pulls the section key and filter out of a playlist's stored <c>content</c>, which wraps the whole
    /// thing as <c>library://&lt;x&gt;/directory/&lt;percent-encoded path and query&gt;</c>. Returns false
    /// when the content isn't a section query (a non-smart playlist, or a shape we don't model) or its
    /// rules don't parse, so a survey can skip it rather than fail.
    /// </summary>
    public static bool TryParseContent(string? content, out int sectionKey, out PlexSmartFilter filter)
    {
        sectionKey = 0;
        filter = null!;

        const string marker = "/directory/";
        var at = content?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        if (content is null || at < 0)
        {
            return false;
        }

        var decoded = Uri.UnescapeDataString(content[(at + marker.Length)..]);
        var q = decoded.IndexOf('?');
        var path = q < 0 ? decoded : decoded[..q];
        var query = q < 0 ? "" : decoded[(q + 1)..];

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sectionAt = Array.IndexOf(segments, "sections");
        if (sectionAt < 0 || sectionAt + 1 >= segments.Length
            || !int.TryParse(segments[sectionAt + 1], out sectionKey))
        {
            return false;
        }

        try
        {
            filter = Parse(query);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>One nesting level while the token stream is being folded into a tree.</summary>
    private sealed class Frame
    {
        public List<PlexFilter> Items { get; } = new();

        /// <summary>
        /// Set by the first <c>and</c>/<c>or</c> seen at this level. Plex writes one join per group (its
        /// editor offers a single "Match all/any" per group), so later joiners can only repeat it.
        /// </summary>
        public PlexJoin? Join { get; set; }

        public PlexFilter? Build() => Items.Count switch
        {
            0 => null,
            // A lone rule needs no group around it, and wrapping it would only have to be flattened away.
            1 => Items[0],
            _ => new PlexGroup(Join ?? PlexJoin.And, Items),
        };
    }
}
