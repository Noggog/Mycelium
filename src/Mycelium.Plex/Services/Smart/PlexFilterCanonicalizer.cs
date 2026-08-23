using System.Text;

namespace Mycelium.Plex.Services.Smart;

/// <summary>
/// Reduces a smart filter to a canonical string so two definitions can be compared by <em>meaning</em>.
///
/// <para>This is what lets the app answer "does the user already have this playlist?" without going by
/// name — they may have built it by hand years ago and called it anything. A plain string comparison of
/// the stored query would be far too brittle; four things have to be normalised away first:</para>
///
/// <list type="number">
/// <item><b>Tag ids.</b> Tag fields store a numeric id, not a name (<c>artist.mood=749936</c>), and those
/// ids are per-server. Resolving them back to names is what makes a definition portable at all.</item>
/// <item><b>Redundant nesting.</b> <c>(A AND B) AND C</c> and <c>A AND B AND C</c> are the same rule set,
/// and Plex's own editor rewrites the first into the second — see <see cref="PlexGroup.Flatten"/>.</item>
/// <item><b>Sibling order.</b> <c>and</c>/<c>or</c> are commutative, so children are sorted.</item>
/// <item><b>Field spelling.</b> Bare field names are qualified by the query's scope, and case is
/// levelled — Plex is inconsistent about both.</item>
/// </list>
///
/// <para><c>type</c> and the query options (<c>sort</c>, <c>limit</c>, …) are deliberately <em>not</em>
/// part of the canonical form: they don't change which tracks a playlist selects, so a user who
/// re-sorted their copy should still be told they already have it.</para>
/// </summary>
public static class PlexFilterCanonicalizer
{
    /// <summary>
    /// Resolves a tag field's stored value to the tag's name — e.g. (<c>artist.mood</c>, <c>749936</c>)
    /// to <c>noggog_liked</c>. Returns null when the field isn't a tag field, or the id is unknown to
    /// this server, in which case the raw value is used as-is.
    /// </summary>
    public delegate string? TagResolver(string field, string value);

    /// <summary>A resolver for tests and for callers with no tag maps loaded: never resolves anything.</summary>
    public static readonly TagResolver NoTags = (_, _) => null;

    /// <summary>
    /// The canonical text of a filter's rules. Empty string when it has none (which matches nothing
    /// meaningful, and so never compares equal to a real definition — see <see cref="AreEquivalent"/>).
    /// </summary>
    public static string Canonical(PlexSmartFilter filter, TagResolver? resolveTag = null)
    {
        if (filter.Rules is null)
        {
            return "";
        }

        var sb = new StringBuilder();
        Write(sb, PlexGroup.Flatten(filter.Rules), filter.Type, resolveTag ?? NoTags);
        return sb.ToString();
    }

    /// <summary>
    /// Whether two filters select the same thing. Rule-less filters are never equivalent to anything,
    /// including each other — "no rules" is a shape we can't meaningfully claim to have generated.
    /// </summary>
    public static bool AreEquivalent(
        PlexSmartFilter a, PlexSmartFilter b, TagResolver? resolveTag = null)
    {
        var left = Canonical(a, resolveTag);
        return left.Length > 0 && left == Canonical(b, resolveTag);
    }

    private static void Write(StringBuilder sb, PlexFilter node, int type, TagResolver resolveTag)
    {
        if (node is PlexCondition condition)
        {
            var field = Qualify(condition.Field, type).ToLowerInvariant();
            var value = resolveTag(field, condition.Value) ?? condition.Value;
            sb.Append(field).Append(Token(condition.Op)).Append(value.ToLowerInvariant());
            return;
        }

        var group = (PlexGroup)node;

        // Children are canonicalised independently, then ordered by their own text: and/or are
        // commutative, so the order Plex happened to store them in carries no meaning.
        var parts = new List<string>(group.Children.Count);
        foreach (var child in group.Children)
        {
            var childText = new StringBuilder();
            Write(childText, child, type, resolveTag);
            parts.Add(childText.ToString());
        }

        parts.Sort(StringComparer.Ordinal);
        sb.Append(group.Join == PlexJoin.And ? "and(" : "or(")
            .Append(string.Join(",", parts))
            .Append(')');
    }

    /// <summary>
    /// Gives a bare field name the scope its query implies. Plex's editor always writes
    /// <c>track.lastViewedAt</c>, but an API-written filter may use the bare <c>lastViewedAt</c> to mean
    /// the queried type's own field; qualifying both makes them compare equal.
    /// </summary>
    private static string Qualify(string field, int type) =>
        field.Contains('.') ? field : PlexSmartFilter.ScopePrefix(type) + field;

    /// <summary>
    /// A stable, unambiguous spelling per operator. Not the wire form — the canonical string is only
    /// ever compared to another canonical string, so readability in a failing test wins.
    /// </summary>
    private static string Token(PlexOp op) => op switch
    {
        PlexOp.Is => "=",
        PlexOp.IsNot => "!=",
        PlexOp.GreaterThan => ">>",
        PlexOp.LessThan => "<<",
        PlexOp.StringIs => "==",
        PlexOp.StringIsNot => "!==",
        PlexOp.BeginsWith => "^=",
        PlexOp.EndsWith => "$=",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown Plex filter operator"),
    };
}
