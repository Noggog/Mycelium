using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Mycelium.Backend.Services.Archive;

/// <summary>
/// Writes a JSON object to exactly one line, exactly one way.
///
/// <para>The archive commits only when file bytes change, so determinism isn't a nicety here — it is
/// what separates a readable history from one where every night rewrites every file. Two runs over
/// equal data must produce equal bytes, which rules out <see cref="System.Text.Json.JsonSerializer"/>:
/// it preserves insertion order, and Mongo does not promise to hand back a document's fields in the
/// same order twice.</para>
///
/// <para>So: keys sorted ordinally, numbers formatted round-trippably, and a space after each
/// <c>:</c> and <c>,</c>. The spacing costs a few bytes per line and buys back the thing the archive
/// exists for — that a person can read it. Non-ASCII is written through as UTF-8 rather than escaped,
/// so an artist like <c>Sigur Rós</c> reads as itself instead of as <c>Sigur Rós</c>.</para>
/// </summary>
public static class CanonicalJson
{
    /// <summary>One record as one line, with no trailing newline (the writer adds it).</summary>
    public static string Line(JsonObject record)
    {
        var builder = new StringBuilder();
        Write(builder, record);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonNode? node)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                WriteObject(builder, obj);
                break;
            case JsonArray array:
                WriteArray(builder, array);
                break;
            case JsonValue value:
                WriteValue(builder, value);
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON node {node.GetType().Name}");
        }
    }

    private static void WriteObject(StringBuilder builder, JsonObject obj)
    {
        builder.Append('{');

        var first = true;
        // Ordinal, not culture-aware: a sort that depends on the host's locale would reorder the file
        // when the container's TZ/locale changes, which is a diff nobody caused.
        foreach (var (key, value) in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            // Null means "not set", and the archive says that by leaving the field out. Keeping them
            // would make a field's arrival flip every row in the file from absent to null.
            if (value is null)
            {
                continue;
            }

            if (!first)
            {
                builder.Append(", ");
            }

            first = false;
            WriteString(builder, key);
            builder.Append(": ");
            Write(builder, value);
        }

        builder.Append('}');
    }

    private static void WriteArray(StringBuilder builder, JsonArray array)
    {
        builder.Append('[');
        for (var i = 0; i < array.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            Write(builder, array[i]);
        }

        builder.Append(']');
    }

    private static void WriteValue(StringBuilder builder, JsonValue value)
    {
        if (value.TryGetValue<string>(out var s))
        {
            WriteString(builder, s);
        }
        else if (value.TryGetValue<bool>(out var b))
        {
            builder.Append(b ? "true" : "false");
        }
        else if (value.TryGetValue<long>(out var l))
        {
            builder.Append(l.ToString(CultureInfo.InvariantCulture));
        }
        else if (value.TryGetValue<double>(out var d))
        {
            // "R" round-trips, and an integral double prints without a decimal point ("3", not "3.0").
            // Both halves matter: the first keeps a restore exact, the second keeps the common
            // whole-number score stable whichever way it was stored.
            builder.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }
        else
        {
            throw new InvalidOperationException(
                $"Archive values must be string, bool, long or double; got {value.GetValue<object>().GetType().Name}");
        }
    }

    private static void WriteString(StringBuilder builder, string value)
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
