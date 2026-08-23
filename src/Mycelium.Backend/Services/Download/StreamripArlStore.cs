using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Mycelium.Backend.Services.Download;

/// <summary>The outcome of writing a new ARL into streamrip's config.</summary>
/// <param name="Saved">Whether the file was updated.</param>
/// <param name="Error">Why not, when it wasn't — surfaced to the user, so it names the file.</param>
public record ArlSaveResult(bool Saved, string? Error = null);

/// <summary>
/// Reads and rewrites the Deezer ARL inside streamrip's own <c>config.toml</c>.
///
/// This is the one place the app touches the download credential, and it exists only so an expired
/// ARL can be replaced from the Download page instead of by editing TOML over SSH. Everything else
/// about the credential stays streamrip's: we do not cache it, pass it on a command line (where it
/// would land in the process table), or log it.
///
/// The file is edited in place with a targeted substitution rather than parsed and re-emitted:
/// streamrip owns this file and reads far more of it than we understand, so a round-trip through a
/// TOML writer risks dropping a key or a comment and breaking the downloader in a way that would look
/// unrelated. Replacing one assignment inside the <c>[deezer]</c> table leaves every other byte alone.
/// </summary>
public class StreamripArlStore
{
    // Matches the `arl = "..."` assignment. Anchored per-line (the config is one key per line) and
    // tolerant of spacing/quote style, since the user may have hand-edited it.
    private static readonly Regex ArlAssignment = new(
        """^(?<lead>\s*arl\s*=\s*)(?<quote>["'])(?<value>.*?)\k<quote>\s*$""",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // A bare `[table]` header line, used to find where [deezer] starts and the next table begins.
    private static readonly Regex TableHeader = new(
        @"^\s*\[(?<name>[^\]]+)\]\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly ILogger<StreamripArlStore> _logger;

    public StreamripArlStore(ILogger<StreamripArlStore> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Where streamrip reads its config. Mirrors its own resolution: <c>XDG_CONFIG_HOME</c> when set
    /// (the container pins it to the mounted <c>/config</c>), else the XDG default under the home
    /// directory — so this works both in the image and on a dev box.
    /// </summary>
    public static string ConfigPath
    {
        get
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
            {
                configHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }
            return Path.Combine(configHome, "streamrip", "config.toml");
        }
    }

    /// <summary>
    /// Whether an ARL is currently set. Deliberately returns a yes/no rather than the value: the page
    /// only ever needs to know whether one is configured, and the credential has no reason to travel
    /// back out to a browser.
    /// </summary>
    public bool HasArl()
    {
        try
        {
            return File.Exists(ConfigPath)
                   && FindArl(File.ReadAllText(ConfigPath)) is { Length: > 0 };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read streamrip config at {Path}", ConfigPath);
            return false;
        }
    }

    /// <summary>
    /// Writes <paramref name="arl"/> into the <c>[deezer]</c> table, replacing whatever was there.
    /// The caller is expected to have validated it against Deezer first — this only reports whether
    /// the file could be updated. Takes effect immediately: <c>rip</c> re-reads its config on every
    /// invocation, so nothing needs restarting.
    /// </summary>
    public ArlSaveResult Save(string arl)
    {
        arl = arl.Trim();
        if (string.IsNullOrEmpty(arl))
        {
            return new ArlSaveResult(false, "The ARL was empty.");
        }

        // An ARL is an opaque hex-ish token; a stray quote or newline would corrupt the TOML (and a
        // newline could inject an unrelated key), so refuse rather than escape it.
        if (arl.Any(c => c is '"' or '\'' or '\n' or '\r' || char.IsControl(c)))
        {
            return new ArlSaveResult(false, "That doesn't look like an ARL — it contains quotes or line breaks.");
        }

        var path = ConfigPath;
        try
        {
            if (!File.Exists(path))
            {
                return new ArlSaveResult(
                    false,
                    $"streamrip has no config yet at {path}. Generate one with `rip config` first.");
            }

            var original = File.ReadAllText(path);
            if (!TryReplaceArl(original, arl, out var updated))
            {
                return new ArlSaveResult(
                    false, $"Couldn't find an [deezer] arl setting in {path} to update.");
            }

            // Write via a temp file in the same directory, then swap: a half-written config.toml would
            // break every later download, and this file is bind-mounted from the host.
            var temp = path + ".mycelium-tmp";
            File.WriteAllText(temp, updated);
            File.Move(temp, path, overwrite: true);

            _logger.LogInformation("Deezer ARL updated in {Path}", path);
            return new ArlSaveResult(true);
        }
        catch (Exception ex)
        {
            // The message is shown to the user but must not echo the config's contents.
            _logger.LogError(ex, "Failed to write the Deezer ARL to {Path}", path);
            return new ArlSaveResult(false, $"Couldn't write {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Replaces the <c>arl</c> assignment that belongs to the <c>[deezer]</c> table. Scoped to that
    /// table because other sources (Qobuz, Tidal) have their own credential keys, and a whole-file
    /// substitution would eventually clobber the wrong one.
    /// </summary>
    internal static bool TryReplaceArl(string toml, string arl, out string updated)
    {
        updated = toml;
        var section = DeezerSection(toml);
        if (section is null)
        {
            return false;
        }

        var (start, length) = section.Value;
        var match = ArlAssignment.Match(toml, start, length);
        if (!match.Success)
        {
            return false;
        }

        updated = string.Concat(
            toml.AsSpan(0, match.Index),
            $"{match.Groups["lead"].Value}\"{arl}\"",
            toml.AsSpan(match.Index + match.Length));
        return true;
    }

    /// <summary>The current ARL, or null when the file has no <c>[deezer]</c> arl set.</summary>
    internal static string? FindArl(string toml)
    {
        if (DeezerSection(toml) is not { } section)
        {
            return null;
        }

        var match = ArlAssignment.Match(toml, section.Start, section.Length);
        return match.Success ? match.Groups["value"].Value : null;
    }

    /// <summary>The span of the <c>[deezer]</c> table: from its header to the next table header (or
    /// end of file). Null when the file has no such table.</summary>
    private static (int Start, int Length)? DeezerSection(string toml)
    {
        int? start = null;
        foreach (Match header in TableHeader.Matches(toml))
        {
            if (start is null)
            {
                if (header.Groups["name"].Value.Trim().Equals("deezer", StringComparison.OrdinalIgnoreCase))
                {
                    start = header.Index + header.Length;
                }
                continue;
            }

            // First header after [deezer] ends its table.
            return (start.Value, header.Index - start.Value);
        }

        return start is null ? null : (start.Value, toml.Length - start.Value);
    }
}
