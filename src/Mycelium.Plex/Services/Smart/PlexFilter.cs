namespace Mycelium.Plex.Services.Smart;

/// <summary>
/// How the siblings inside one rule group combine — Plex's "Match all/any of the following".
/// On the wire this is the <c>and=1</c> / <c>or=1</c> token that sits <em>between</em> two terms.
/// </summary>
public enum PlexJoin
{
    And,
    Or,
}

/// <summary>
/// The comparison a rule applies. These are exactly the operators the server advertises under
/// <c>Meta.FieldType[].Operator[].key</c> on a section listing (<c>?includeMeta=1</c>); the titles in
/// the comment are how Plex's own filter editor labels them, which differ by field type — <c>Is</c>
/// reads as "is" on a tag/integer field but "contains" on a string one.
/// </summary>
public enum PlexOp
{
    /// <summary>Wire key <c>=</c>. "is" (tag/integer/date), "contains" (string).</summary>
    Is,

    /// <summary>Wire key <c>!=</c>. "is not" (tag/integer), "does not contain" (string).</summary>
    IsNot,

    /// <summary>Wire key <c>&gt;&gt;=</c>. "is greater than" (integer), "is after" (date).</summary>
    GreaterThan,

    /// <summary>Wire key <c>&lt;&lt;=</c>. "is less than" (integer), "is before" (date).</summary>
    LessThan,

    /// <summary>Wire key <c>==</c>. "is" — the exact-match form for string fields.</summary>
    StringIs,

    /// <summary>Wire key <c>!==</c>. "is not" — the exact-match form for string fields.</summary>
    StringIsNot,

    /// <summary>Wire key <c>&lt;=</c>. "begins with" (string).</summary>
    BeginsWith,

    /// <summary>Wire key <c>&gt;=</c>. "ends with" (string).</summary>
    EndsWith,
}

/// <summary>
/// One node of a smart playlist's rule tree: either a single <see cref="PlexCondition"/> or a
/// <see cref="PlexGroup"/> of them.
/// </summary>
public abstract record PlexFilter;

/// <summary>
/// A single rule — <c>Field Op Value</c>, e.g. <c>track.userRating &gt;&gt; 7</c>.
///
/// <para><paramref name="Field"/> is the dotted key Plex uses (<c>track.userRating</c>,
/// <c>artist.mood</c>), <em>not</em> the display title. <paramref name="Value"/> is always carried as
/// the raw string Plex expects, because the wire format is untyped and the meaning depends on the
/// field: ratings are 0–10 with a half-star step and <c>-1</c> for unrated; dates are relative offsets
/// like <c>-3mon</c> / <c>-2y</c>; and <b>tag fields carry a numeric tag id</b>, not the tag's name
/// (<c>artist.mood=749936</c> rather than <c>artist.mood=noggog_liked</c>) — see
/// <see cref="PlexFilterCanonicalizer"/> for why comparison has to undo that.</para>
/// </summary>
public sealed record PlexCondition(string Field, PlexOp Op, string Value) : PlexFilter;

/// <summary>
/// A bracketed set of rules combined by one <see cref="PlexJoin"/> — the wire's
/// <c>push=1</c> … <c>pop=1</c> pair.
///
/// <para>Plex only ever writes a single join within one group (its editor offers one "Match all/any"
/// dropdown per group), so <paramref name="Join"/> applies to every gap between children.</para>
/// </summary>
public sealed record PlexGroup(PlexJoin Join, IReadOnlyList<PlexFilter> Children) : PlexFilter
{
    public static PlexGroup All(params PlexFilter[] children) => new(PlexJoin.And, children);

    public static PlexGroup Any(params PlexFilter[] children) => new(PlexJoin.Or, children);

    /// <summary>
    /// Collapses nesting that carries no meaning, so that logically identical trees end up shaped
    /// identically. Two rewrites, applied bottom-up:
    ///
    /// <list type="bullet">
    /// <item>A group with a single child <em>is</em> that child — the brackets say nothing.</item>
    /// <item>A group nested directly inside a group with the <em>same</em> join is spliced into its
    /// parent: <c>(A AND B) AND C</c> ≡ <c>A AND B AND C</c>.</item>
    /// </list>
    ///
    /// <para>This matters because Plex does the same thing in its editor. Sending redundant nesting
    /// stores it verbatim, but the filter view renders it flat, and re-saving by hand writes back the
    /// flattened form — so without this, a playlist would stop matching the definition that created it
    /// the moment the user opened and saved it. Groups whose join <em>differs</em> from their parent are
    /// left alone; that nesting is load-bearing.</para>
    /// </summary>
    public static PlexFilter Flatten(PlexFilter node)
    {
        if (node is not PlexGroup group)
        {
            return node;
        }

        var flattened = new List<PlexFilter>();
        foreach (var child in group.Children)
        {
            var f = Flatten(child);
            // Same-join child groups dissolve into this one; a differently-joined group stays nested.
            if (f is PlexGroup inner && inner.Join == group.Join)
            {
                flattened.AddRange(inner.Children);
            }
            else
            {
                flattened.Add(f);
            }
        }

        return flattened.Count == 1 ? flattened[0] : group with { Children = flattened };
    }
}
