using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Mycelium.Backend.Services.Archive;

/// <summary>
/// Writes a document as YAML, one way, formatted so git diffs read well.
///
/// <para>YAML rather than JSON because block style has no commas: appending an entry never touches
/// the line above it, so adding one rating to a song is one added line and nothing else moves. In
/// JSON the same change costs a rewrite of its neighbour to carry a comma — workable only with
/// leading-comma formatting, which reads oddly. It is also about half the lines, having no braces or
/// brackets to sit on their own.</para>
///
/// <para><b>Determinism:</b> the archive commits only when file bytes change, so two runs over equal
/// data must produce equal bytes. Mongo makes no promise about the order it returns a document's
/// fields in, so keys are sorted ordinally, always.</para>
///
/// <para><b>Every string scalar is quoted, unconditionally.</b> This is not fussiness — YAML coerces
/// bare scalars aggressively, and a real music library is full of names that trip it. Measured
/// against one of ~12,600 names: 307 titles contain <c>": "</c>, 30 look like numbers (<c>0034</c>
/// would silently become <c>34</c>), 20 begin with a character YAML reserves (<c>#digitalfreedom</c>
/// is a comment, <c>&amp; The Brite Lites…</c> an anchor), and five are booleans in disguise — the
/// artists <c>Yes</c>, <c>No</c>, <c>On</c>, <c>Y</c> and <c>Null</c>. Quoting everything removes the
/// entire class of problem rather than trying to enumerate it. Numbers and booleans are emitted bare,
/// so a rating stays a number.</para>
///
/// <para>Keys are quoted only when they need it, which keeps the structural field names readable
/// while still protecting a username that happens to be <c>no</c>.</para>
/// </summary>
public static class CanonicalYaml
{
    /// <summary>A complete document, with a trailing newline.</summary>
    public static string Document(JsonNode? node)
    {
        var builder = new StringBuilder();
        switch (node)
        {
            case JsonObject obj when Fields(obj).Count == 0:
                builder.Append("{}\n");
                break;
            case JsonObject obj:
                WriteMapping(builder, obj, depth: 0, firstInline: false);
                break;
            case JsonArray array when array.Count == 0:
                builder.Append("[]\n");
                break;
            case JsonArray array:
                WriteSequence(builder, array, depth: 0);
                break;
            default:
                builder.Append(Scalar(node)).Append('\n');
                break;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Null means "not set", and the archive says that by leaving the field out — otherwise a field
    /// arriving in the schema would flip every existing record from absent to null in one diff.
    /// Ordinal sort, not culture-aware: a locale-dependent order would reorder every file when the
    /// container's locale changed, which is a diff nobody caused.
    /// </summary>
    private static List<KeyValuePair<string, JsonNode?>> Fields(JsonObject obj) =>
        obj.Where(pair => pair.Value is not null)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();

    private static void Indent(StringBuilder builder, int depth) => builder.Append(' ', depth * 2);

    /// <summary>
    /// <paramref name="firstInline"/> suppresses the indent on the first key, so a mapping can start
    /// on the same line as the <c>-</c> of the sequence item holding it.
    /// </summary>
    private static void WriteMapping(StringBuilder builder, JsonObject obj, int depth, bool firstInline)
    {
        var fields = Fields(obj);
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0 || !firstInline)
            {
                Indent(builder, depth);
            }

            WriteKey(builder, fields[i].Key);
            builder.Append(':');
            WriteValue(builder, fields[i].Value, depth);
        }
    }

    private static void WriteValue(StringBuilder builder, JsonNode? value, int depth)
    {
        switch (value)
        {
            case JsonObject obj when Fields(obj).Count == 0:
                builder.Append(" {}\n");
                break;
            case JsonObject obj:
                builder.Append('\n');
                WriteMapping(builder, obj, depth + 1, firstInline: false);
                break;
            case JsonArray array when array.Count == 0:
                builder.Append(" []\n");
                break;
            case JsonArray array:
                builder.Append('\n');
                WriteSequence(builder, array, depth + 1);
                break;
            default:
                builder.Append(' ').Append(Scalar(value)).Append('\n');
                break;
        }
    }

    private static void WriteSequence(StringBuilder builder, JsonArray array, int depth)
    {
        // Order is data here — a playlist's running order, an album's track order — so unlike mapping
        // keys it is preserved exactly as given.
        foreach (var item in array)
        {
            Indent(builder, depth);
            builder.Append("- ");

            switch (item)
            {
                case JsonObject obj when Fields(obj).Count == 0:
                    builder.Append("{}\n");
                    break;
                case JsonObject obj:
                    WriteMapping(builder, obj, depth + 1, firstInline: true);
                    break;
                case JsonArray nested when nested.Count == 0:
                    builder.Append("[]\n");
                    break;
                case JsonArray nested:
                    builder.Append('\n');
                    WriteSequence(builder, nested, depth + 1);
                    break;
                default:
                    builder.Append(Scalar(item)).Append('\n');
                    break;
            }
        }
    }

    /// <summary>
    /// Bare when it is unambiguously a plain identifier, quoted otherwise. Our own field names stay
    /// readable; a username that happens to be <c>no</c> or <c>0034</c> is still protected.
    /// </summary>
    private static void WriteKey(StringBuilder builder, string key)
    {
        if (IsSafeBareKey(key))
        {
            builder.Append(key);
        }
        else
        {
            WriteQuoted(builder, key);
        }
    }

    private static bool IsSafeBareKey(string key)
    {
        if (key.Length == 0 || Reserved.Contains(key))
        {
            return false;
        }

        // Must start with a letter or underscore, so an all-digit key can't be read as a number.
        if (!char.IsAsciiLetter(key[0]) && key[0] != '_')
        {
            return false;
        }

        return key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-');
    }

    /// <summary>
    /// The words YAML reads as booleans or null. Includes the YAML 1.1 spellings, which is what most
    /// parsers in the wild still apply — this is the "Norway problem", and a library with a band
    /// called <c>No</c> meets it for real.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "y", "n", "yes", "no", "true", "false", "on", "off", "null", "~",
    };

    private static string Scalar(JsonNode? value)
    {
        if (value is not JsonValue node)
        {
            throw new InvalidOperationException($"Unsupported YAML node {value?.GetType().Name ?? "null"}");
        }

        if (node.TryGetValue<string>(out var s))
        {
            var builder = new StringBuilder();
            WriteQuoted(builder, s);
            return builder.ToString();
        }

        if (node.TryGetValue<bool>(out var b))
        {
            return b ? "true" : "false";
        }

        if (node.TryGetValue<long>(out var l))
        {
            return l.ToString(CultureInfo.InvariantCulture);
        }

        if (node.TryGetValue<double>(out var d))
        {
            // "R" round-trips, and an integral double prints without a decimal point ("3", not "3.0"),
            // so a rating stored either way produces one line rather than two.
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException(
            $"Archive values must be string, bool, long or double; got {node.GetValue<object>().GetType().Name}");
    }

    /// <summary>
    /// A double-quoted YAML scalar. The escapes are the same set JSON uses, which YAML's double-quoted
    /// style accepts verbatim. Non-ASCII is written through as UTF-8 rather than escaped, so an artist
    /// like <c>Sigur Rós</c> reads as itself.
    /// </summary>
    private static void WriteQuoted(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (char.IsControl(c))
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
