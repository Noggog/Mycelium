namespace Mycelium.Backend.Services.Download;

/// <summary>
/// Translates the file paths Plex reports into paths this process can actually open.
///
/// <para>They are not the same namespace, and assuming they are is the failure this class exists to
/// prevent. Plex reports a path in <em>its own</em> container/host — measured against a real server,
/// a music library spanning <c>/media/music</c> and <c>/mediadrop/Music</c> — while Mycelium sees
/// whatever was mounted into it (<c>/music</c>). Acting on Plex's path verbatim would target
/// something that doesn't exist, or worse, something that does and isn't the file meant.</para>
///
/// <para>Configured via <c>PLEX_PATH_MAP</c> as <c>plexPrefix:localPrefix</c> pairs separated by
/// commas or semicolons, e.g. <c>/media/music:/music,/mediadrop/Music:/mediadrop</c>. Anything
/// outside a mapped prefix is deliberately <b>not</b> guessed at: the caller is told it can't be
/// resolved, which surfaces as a refusal rather than a silent skip. This is the same problem
/// Radarr/Sonarr call <i>remote path mapping</i>, and it has the same answer — declare it.</para>
/// </summary>
public class LibraryPathMap
{
    private readonly IReadOnlyList<(string Plex, string Local)> _mappings;

    public LibraryPathMap(string? configured)
    {
        _mappings = (configured ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            .Select(parts => (Plex: Normalize(parts[0]), Local: Normalize(parts[1])))
            // Longest prefix first, so a nested mapping wins over the parent that contains it.
            .OrderByDescending(m => m.Plex.Length)
            .ToArray();
    }

    /// <summary>Whether any mapping is configured at all. Nothing can be resolved without one.</summary>
    public bool IsConfigured => _mappings.Count > 0;

    /// <summary>The configured prefixes, for diagnostics and for explaining a refusal.</summary>
    public IReadOnlyList<string> PlexPrefixes => _mappings.Select(m => m.Plex).ToArray();

    /// <summary>
    /// The local path for a Plex-reported one, or null when it falls outside every mapped prefix —
    /// which means "we can't safely touch this", not "it isn't there".
    /// </summary>
    public string? ToLocal(string? plexPath)
    {
        if (string.IsNullOrWhiteSpace(plexPath))
        {
            return null;
        }

        var path = Normalize(plexPath);
        foreach (var (plex, local) in _mappings)
        {
            // Prefix match on a path boundary: "/media/music" must not match "/media/musicals".
            if (path.Equals(plex, StringComparison.Ordinal))
            {
                return local;
            }
            if (path.StartsWith(plex + "/", StringComparison.Ordinal))
            {
                return local + path[plex.Length..];
            }
        }
        return null;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}
