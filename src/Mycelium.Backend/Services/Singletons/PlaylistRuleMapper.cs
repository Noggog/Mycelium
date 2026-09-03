using System.Globalization;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Smart;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// Turns a Plex smart-playlist filter into the portable <see cref="PlaylistRules"/> the archive keeps.
///
/// <para>The stored query is unusable off this server, and in four separate ways: it is wrapped in a
/// <c>server://&lt;machine-id&gt;/…/sections/&lt;n&gt;/all</c> URI, its tag conditions carry numeric tag
/// ids instead of names (<c>artist.mood=749936</c>), its operators are wire tokens (<c>&gt;&gt;=</c>),
/// and its star ratings are on Plex's internal 0&ndash;10 scale while every other rating in the archive
/// is 0&ndash;5. A reader on new hardware could see all of that and still not know what the playlist
/// selects — which would make archiving the rules rather than the membership pointless.</para>
///
/// <para>All four are undone here, at harvest time rather than at export time, so the local mirror is
/// as readable as the archive is and nothing has to hold a Plex vocabulary to interpret it later.</para>
/// </summary>
public static class PlaylistRuleMapper
{
    /// <summary>Resolves (field, stored value) to a tag name, or null when it isn't a known tag.</summary>
    public delegate string? TagResolver(string field, string value);

    /// <summary>Query options worth keeping: they change what the playlist yields, or in what order.</summary>
    private const string SortOption = "sort";

    private const string LimitOption = "limit";

    /// <summary>
    /// The portable form of <paramref name="filter"/>, or null when it has no rules at all — a filter
    /// that selects everything is not a definition worth recording as one.
    /// </summary>
    public static PlaylistRules? ToPortable(PlexSmartFilter filter, TagResolver? resolveTag = null)
    {
        if (filter.Rules is null)
        {
            return null;
        }

        // Flattened first, so redundant nesting that Plex's own editor would rewrite away doesn't show
        // up in the archive as structure a reader would try to find meaning in.
        var root = PlexGroup.Flatten(filter.Rules);
        var resolve = resolveTag ?? ((_, _) => null);

        // A single bare condition is still written as a group, so "match" is always stated rather than
        // left for the reader to assume.
        var (match, rules) = root is PlexGroup group
            ? (Match(group.Join), group.Children.Select(c => Rule(c, filter.Type, resolve)).ToList())
            : ("all", new List<PlaylistRule> { Rule(root, filter.Type, resolve) });

        return new PlaylistRules(match, rules, Option(filter, SortOption), Limit(filter));
    }

    private static PlaylistRule Rule(PlexFilter node, int type, TagResolver resolve)
    {
        if (node is PlexGroup group)
        {
            return new PlaylistRuleGroup(
                Match(group.Join),
                group.Children.Select(c => Rule(c, type, resolve)).ToList());
        }

        var condition = (PlexCondition)node;

        // Bare field names mean "the queried type's own field", so they are qualified here — otherwise
        // the same rule would read differently depending on a `type` the archive doesn't keep.
        var field = condition.Field.Contains('.')
            ? condition.Field
            : PlexSmartFilter.ScopePrefix(type) + condition.Field;

        return new PlaylistCondition(field, Operator(condition.Op), Value(field, condition.Value, resolve));
    }

    /// <summary>
    /// A rule's right-hand side, made to mean something on its own: a tag id becomes the tag's name,
    /// and a star rating moves onto the 0&ndash;5 scale the rest of the archive uses.
    /// </summary>
    private static string Value(string field, string raw, TagResolver resolve)
    {
        if (resolve(field, raw) is { } tag)
        {
            return tag;
        }

        return IsRating(field) ? Stars(raw) : raw;
    }

    /// <summary>
    /// Plex stores a rating as 0&ndash;10 so half stars are whole numbers, and <c>-1</c> for "no
    /// rating at all". Written as stars here, because an album file three lines away records the same
    /// user's rating of the same track as <c>4.5</c> — leaving the rule saying <c>9</c> would read as
    /// a different measurement rather than the same one.
    /// </summary>
    private static string Stars(string raw)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return raw;
        }

        return value < 0
            ? "unrated"
            : (value / 2).ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static bool IsRating(string field) =>
        field.EndsWith(".userRating", StringComparison.OrdinalIgnoreCase);

    private static string Match(PlexJoin join) => join == PlexJoin.And ? "all" : "any";

    /// <summary>
    /// Operators spelled out. The wire tokens are unreadable and, worse, ambiguous across field types
    /// — Plex's own editor labels the same key "is greater than" on a number and "is after" on a date.
    /// The longer spelling commits to the comparison rather than to one field type's label for it.
    /// </summary>
    private static string Operator(PlexOp op) => op switch
    {
        PlexOp.Is => "is",
        PlexOp.IsNot => "is not",
        PlexOp.GreaterThan => "greater than",
        PlexOp.LessThan => "less than",
        PlexOp.StringIs => "equals",
        PlexOp.StringIsNot => "not equals",
        PlexOp.BeginsWith => "begins with",
        PlexOp.EndsWith => "ends with",
        _ => op.ToString(),
    };

    /// <summary>
    /// One query option, percent-decoded. The parser keeps option values raw on purpose, so that
    /// writing a filter back to Plex reproduces it byte for byte — but nothing writes this form back,
    /// and <c>random%3Adesc</c> in an archive is exactly the kind of wire detail being stripped here.
    /// </summary>
    private static string? Option(PlexSmartFilter filter, string name) =>
        filter.Options.FirstOrDefault(o => string.Equals(o.Key, name, StringComparison.OrdinalIgnoreCase))
            .Value is { Length: > 0 } value
            ? Uri.UnescapeDataString(value)
            : null;

    private static int? Limit(PlexSmartFilter filter) =>
        int.TryParse(Option(filter, LimitOption), out var limit) ? limit : null;
}
