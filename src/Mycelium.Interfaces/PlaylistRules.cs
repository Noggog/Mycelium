namespace Mycelium.Interfaces;

/// <summary>
/// A smart playlist's definition, in a form that means something without the server that produced it.
///
/// <para>Deliberately not the media server's own stored query. That query is a URI carrying the
/// server's machine identifier and a numeric section id, its tag conditions reference numeric tag ids
/// rather than tag names, and its operators are wire tokens like <c>&gt;&gt;=</c>. Every one of those
/// is local to one installation — a reader on new hardware could see the rule and still not know what
/// it selects, which defeats the point of archiving the rules instead of the membership.</para>
///
/// <para>So the definition is decomposed on the way in: ids resolved to names, operators spelled out,
/// star ratings put on the same 0&ndash;5 scale the rest of the archive uses, and the server-local
/// prefix dropped entirely.</para>
/// </summary>
/// <param name="Match">
/// How the top-level rules combine: <c>all</c> or <c>any</c>. Always present, even for a single rule,
/// so a reader never has to infer it.
/// </param>
/// <param name="Sort">The ordering the playlist was saved with, if any. Verbatim — it names fields.</param>
/// <param name="Limit">A cap on how many tracks the playlist yields, if one was set.</param>
public record PlaylistRules(
    string Match,
    IReadOnlyList<PlaylistRule> Rules,
    string? Sort = null,
    int? Limit = null);

/// <summary>One node of a rule tree: a single test, or a nested group of them.</summary>
public abstract record PlaylistRule;

/// <summary>
/// A single test — <c>field</c> <c>op</c> <c>value</c>, e.g. <c>track.userRating</c> /
/// <c>greater than</c> / <c>4</c>.
/// </summary>
/// <param name="Field">
/// The scoped field name (<c>track.userRating</c>, <c>artist.mood</c>). Kept as the media server spells
/// it: it is already readable, and inventing a second vocabulary would only add a mapping a reader
/// would then have to reverse.
/// </param>
/// <param name="Op">
/// The comparison, spelled out — <c>is</c>, <c>is not</c>, <c>greater than</c>, <c>less than</c>,
/// <c>equals</c>, <c>not equals</c>, <c>begins with</c>, <c>ends with</c>.
///
/// <para>One caveat worth knowing rather than papering over: the media server uses a single operator
/// for two meanings depending on the field's type, and the type isn't recorded anywhere in the rule.
/// <c>is</c> means "is" on a tag or a number and "contains" on free text, while <c>equals</c> is the
/// exact-match form for text. The spelling here follows the tag/number reading, which is what almost
/// every rule uses; on a title or a filename, read <c>is</c> as "contains".</para>
/// </param>
/// <param name="Value">
/// What it is compared against, as text. Tag conditions carry the tag's <em>name</em>; star ratings are
/// on the 0&ndash;5 scale, with <c>unrated</c> for "no rating"; dates stay as the relative offsets they
/// were written as (<c>-3mon</c>).
/// </param>
public sealed record PlaylistCondition(string Field, string Op, string Value) : PlaylistRule;

/// <summary>A bracketed set of rules combined by one <paramref name="Match"/> — <c>all</c> or <c>any</c>.</summary>
public sealed record PlaylistRuleGroup(string Match, IReadOnlyList<PlaylistRule> Rules) : PlaylistRule;
