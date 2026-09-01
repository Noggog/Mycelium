using System.Security.Cryptography;
using System.Text;

namespace Mycelium.Backend.Services.Archive;

/// <summary>
/// Turns artist and album names into path segments.
///
/// <para>A directory per artist and a file per album is far and away the nicest thing to read and to
/// diff — but names in a real library are not filenames. Measured against one: 579 album titles
/// contain <c>/</c> or <c>:</c> (<c>60/40</c>, <c>Gorgeous / Fantasy</c>), 19 artists end in a dot
/// (<c>Dinosaur Jr.</c>, <c>Fred again..</c>), and 26 pairs of artists differ only by case
/// (<c>tUnE-yArDs</c> and <c>Tune-Yards</c>) — which are two directories on Linux and one on macOS or
/// Windows.</para>
///
/// <para>So names are escaped, and the escaping is allowed to be lossy in one direction only: the
/// exact name is always written <em>inside</em> the file. A path is a locator, never the data. A
/// reader that wants the real title reads the JSON, and nothing has to reverse this transformation.
/// </para>
/// </summary>
public static class ArchivePaths
{
    /// <summary>
    /// Characters that cannot appear in a path segment on some filesystem that matters. A superset of
    /// what Linux forbids, because the archive is meant to be cloneable anywhere.
    /// </summary>
    private const string Reserved = "/\\:*?\"<>|";

    /// <summary>
    /// Names that mean something else entirely on Windows, whatever the extension.
    /// </summary>
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Path segments for a set of names that will share one directory, keyed by the original name.
    ///
    /// <para>Taken as a set rather than one at a time because two names can only be known to collide
    /// in the context of their neighbours. Where they do, <em>both</em> get a short suffix derived from
    /// their own text — not from their position — so that adding or removing a third name never
    /// renames the other two and turns one changed album into a whole-directory rewrite.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> ForNames(IEnumerable<string> names)
    {
        var distinct = names.Distinct(StringComparer.Ordinal).ToList();

        // Two names collide when their escaped forms are equal ignoring case — that is what a
        // case-insensitive filesystem will see.
        var escaped = distinct.ToDictionary(n => n, Escape, StringComparer.Ordinal);
        var collisions = escaped.Values
            .GroupBy(e => e, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in distinct)
        {
            var segment = escaped[name];
            result[name] = collisions.Contains(segment) ? $"{segment}~{ShortHash(name)}" : segment;
        }

        return result;
    }

    /// <summary>
    /// One name as a path segment, with everything hostile percent-encoded. Percent-encoding rather
    /// than deletion so that two different names can't quietly become one file.
    /// </summary>
    public static string Escape(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (Reserved.IndexOf(c) >= 0 || char.IsControl(c) || c == '%')
            {
                builder.Append('%').Append(((int)c).ToString("X2"));
            }
            else
            {
                builder.Append(c);
            }
        }

        var segment = builder.ToString();

        // Windows silently strips trailing dots and spaces, which would turn "Dinosaur Jr." into
        // "Dinosaur Jr" and collide it with an artist genuinely called that.
        while (segment.Length > 0 && (segment[^1] == '.' || segment[^1] == ' '))
        {
            segment = segment[..^1] + (segment[^1] == '.' ? "%2E" : "%20");
        }

        if (segment.Length == 0)
        {
            return "_";
        }

        // A device name is claimed by the OS whatever follows it.
        var stem = segment.Split('.')[0];
        if (DeviceNames.Contains(stem))
        {
            segment = "_" + segment;
        }

        // Filesystems cap a single component at 255 bytes. Truncating alone could merge two long
        // names, so the hash of the full name goes on the end to keep them apart.
        var bytes = Encoding.UTF8.GetByteCount(segment);
        if (bytes > 200)
        {
            segment = Truncate(segment, 190) + "~" + ShortHash(name);
        }

        return segment;
    }

    /// <summary>Four hex characters of SHA-256 — enough to separate a handful of colliding names.</summary>
    private static string ShortHash(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return Convert.ToHexString(hash)[..4].ToLowerInvariant();
    }

    /// <summary>Cuts to a byte budget without splitting a UTF-8 character in half.</summary>
    private static string Truncate(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
        {
            return value;
        }

        var end = maxBytes;
        while (end > 0 && (bytes[end] & 0xC0) == 0x80)
        {
            end--;
        }

        return Encoding.UTF8.GetString(bytes, 0, end);
    }
}
