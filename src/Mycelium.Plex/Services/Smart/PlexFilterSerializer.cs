using System.Text;

namespace Mycelium.Plex.Services.Smart;

/// <summary>
/// Writes a <see cref="PlexSmartFilter"/> as the query string Plex stores for a smart playlist.
///
/// <para><b>The format.</b> It is not a nested structure but a flat, ordered token stream evaluated as
/// a stack: <c>push=1</c> opens a group, <c>pop=1</c> closes it, and <c>and=1</c>/<c>or=1</c> sit
/// between two terms. A rule is a single param whose <em>name</em> carries both the field and the
/// operator, with the operator's trailing <c>=</c> doubling as the name/value separator — so "track
/// rating greater than 7" is the param <c>track.userRating&gt;&gt;</c> with value <c>7</c>, written
/// <c>track.userRating%3E%3E=7</c>.</para>
///
/// <para><b>Encoding.</b> Operator characters are percent-encoded in the name except <c>!</c>, which
/// Plex leaves literal; this matches byte-for-byte what Plex's own editor writes, which keeps generated
/// playlists indistinguishable from hand-made ones. The whole string is then percent-encoded <em>again</em>
/// when it is nested inside the <c>uri=</c> parameter of a create/update call — that second pass is the
/// caller's job (see <c>PlexPlaylistApi</c>), not this class's.</para>
/// </summary>
public static class PlexFilterSerializer
{
    /// <summary>
    /// The operator's wire spelling as it appears in a param name — i.e. the operator key minus its
    /// trailing <c>=</c>, which is emitted separately as the separator.
    /// </summary>
    internal static string Suffix(PlexOp op) => op switch
    {
        PlexOp.Is => "",
        PlexOp.IsNot => "!",
        PlexOp.GreaterThan => "%3E%3E",
        PlexOp.LessThan => "%3C%3C",
        PlexOp.StringIs => "%3D",
        PlexOp.StringIsNot => "!%3D",
        PlexOp.BeginsWith => "%3C",
        PlexOp.EndsWith => "%3E",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown Plex filter operator"),
    };

    /// <summary>
    /// The full query string, starting with <c>type=</c>, then the preserved options, then the rules.
    /// </summary>
    public static string Serialize(PlexSmartFilter filter)
    {
        var sb = new StringBuilder();
        sb.Append("type=").Append(filter.Type);

        foreach (var option in filter.Options)
        {
            sb.Append('&').Append(option.Key).Append('=').Append(option.Value);
        }

        if (filter.Rules is not null)
        {
            // Flatten first: nesting that repeats its parent's join is dropped by Plex's editor anyway,
            // so emitting it would make a generated playlist stop matching its own definition as soon as
            // the user opened and re-saved it.
            var rules = PlexGroup.Flatten(filter.Rules);
            sb.Append('&');
            WriteNode(sb, rules, bracket: false);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Emits one node. <paramref name="bracket"/> wraps a group in <c>push</c>/<c>pop</c>; the
    /// outermost group never is, because the top level of the query is already an implicit group.
    /// </summary>
    private static void WriteNode(StringBuilder sb, PlexFilter node, bool bracket)
    {
        if (node is PlexCondition condition)
        {
            sb.Append(condition.Field)
                .Append(Suffix(condition.Op))
                .Append('=')
                .Append(Uri.EscapeDataString(condition.Value));
            return;
        }

        var group = (PlexGroup)node;
        if (bracket)
        {
            sb.Append("push=1&");
        }

        var joiner = group.Join == PlexJoin.And ? "&and=1&" : "&or=1&";
        for (var i = 0; i < group.Children.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(joiner);
            }

            // A child group has survived flattening only if its join differs from ours, which means the
            // brackets are load-bearing and must be written.
            WriteNode(sb, group.Children[i], bracket: group.Children[i] is PlexGroup);
        }

        if (bracket)
        {
            sb.Append("&pop=1");
        }
    }
}
