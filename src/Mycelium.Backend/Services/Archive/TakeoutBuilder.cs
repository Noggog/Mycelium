using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Archive;

/// <summary>
/// What a takeout contains, in numbers. Shown before the download so pressing the button isn't a leap
/// of faith — and so an empty answer ("0 song ratings") reads as a fact about the account rather than
/// as a broken export.
/// </summary>
/// <param name="Artists">The whole library, which the takeout carries as the frame for the rest.</param>
public record TakeoutSummary(
    string FileName,
    int Artists,
    int Albums,
    int Liked,
    int Disliked,
    int Indifferent,
    int SongRatings,
    int Playlists,
    int Acquisitions,
    int Blocks);

/// <summary>
/// One person's copy of the archive, on demand.
///
/// <para>Deliberately the same pipeline as the nightly snapshot — same dump, same
/// <see cref="ArchiveBuilder"/>, same YAML — with <see cref="ArchiveScope"/> spliced in between. So a
/// takeout is not a second export format that can rot next to the first: it is the archive, with
/// other people's rows never read. Anything that improves the archive improves this for free, and a
/// field that starts being kept starts being handed out at the same moment, which is the correct
/// default for a "give me my data" button.</para>
///
/// <para>Independent of <c>METADATA_REPO_PATH</c>: this touches no git repository and no filesystem,
/// so it works on a deployment that never configured archiving at all.</para>
/// </summary>
public class TakeoutBuilder
{
    private readonly IArchiveDump _dump;
    private readonly ArchiveBuilder _builder;

    public TakeoutBuilder(IArchiveDump dump, ArchiveBuilder builder)
    {
        _dump = dump;
        _builder = builder;
    }

    /// <summary>Everything one person's takeout is made of, built but not yet zipped.</summary>
    public record Takeout(TakeoutSummary Summary, IReadOnlyList<ArchiveFile> Files);

    /// <summary>
    /// Builds the takeout for <paramref name="subject"/>. Never null: an account with no ratings and
    /// no playlists still gets the library and an honest set of zeroes, which is a more useful answer
    /// than an error.
    /// </summary>
    public async Task<Takeout> Build(string subject)
    {
        var scoped = ArchiveScope.ForUser(await ArchiveCollections.Read(_dump), subject);
        return new Takeout(Summarize(scoped), _builder.Build(scoped));
    }

    /// <summary>
    /// The counts alone, for the page that offers the download. Honest by construction: it scopes and
    /// counts the very rows the export would write, rather than asking a second question that could
    /// come back with a different answer.
    ///
    /// <para>Reads only the collections it counts — see <see cref="ArchiveCollections.ReadCounted"/>
    /// for what that skips and why.</para>
    /// </summary>
    public async Task<TakeoutSummary> Summary(string subject) =>
        Summarize(ArchiveScope.ForUser(await ArchiveCollections.ReadCounted(_dump), subject));

    /// <summary>
    /// Writes the files as a zip. Streamed into <paramref name="destination"/> rather than buffered
    /// into a byte array: a full library is tens of thousands of small files, and the response body is
    /// somewhere to put them that isn't the server's heap.
    /// </summary>
    public static void WriteZip(Stream destination, IReadOnlyList<ArchiveFile> files)
    {
        // leaveOpen, because the caller owns the response stream and closing it here would truncate
        // the response before ASP.NET has finished with it.
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        Add(zip, ArchiveReadme.FileName, ArchiveReadme.Takeout);
        foreach (var file in files)
        {
            Add(zip, file.RelativePath, file.Contents);
        }
    }

    private static void Add(ZipArchive zip, string path, string contents)
    {
        // The builder already produces LF endings and the entry names already use '/', which is what
        // the zip format wants on every platform.
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>The download's filename: who it is and when they asked, so two are never confused.</summary>
    public static string FileNameFor(string? username, DateOnly on)
    {
        var who = Slug(username);
        return who is null
            ? $"mycelium-takeout-{on:yyyy-MM-dd}.zip"
            : $"mycelium-takeout-{who}-{on:yyyy-MM-dd}.zip";
    }

    private static TakeoutSummary Summarize(ArchiveInput scoped)
    {
        var verdicts = scoped.ArtistVerdicts
            .Select(v => Str(v, "status"))
            .Where(s => s is not null)
            .ToList();

        return new TakeoutSummary(
            FileNameFor(scoped.Users.Select(u => Str(u, "username")).FirstOrDefault(), Today()),
            Artists: scoped.Artists.Count,
            Albums: scoped.Artists.Sum(a => a["albums"] is JsonArray albums ? albums.Count : 0),
            Liked: verdicts.Count(s => s == "Liked"),
            Disliked: verdicts.Count(s => s == "Disliked"),
            Indifferent: verdicts.Count(s => s == "Indifferent"),
            SongRatings: scoped.TrackRatings.Count,
            Playlists: scoped.Playlists.Count,
            Acquisitions: scoped.Purchases.Count,
            Blocks: scoped.Blocks.Count);
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>
    /// A username reduced to something safe in a downloaded filename — and in the
    /// <c>Content-Disposition</c> header that carries it, which is why anything outside this set is
    /// dropped rather than escaped.
    /// </summary>
    private static string? Slug(string? username)
    {
        if (username is null)
        {
            return null;
        }

        var at = username.IndexOf('@');
        var local = at >= 0 ? username[..at] : username;

        var builder = new StringBuilder(local.Length);
        foreach (var c in local.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-')
            {
                builder.Append(c);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? Str(JsonObject record, string field) =>
        record.TryGetPropertyValue(field, out var value) && value is JsonValue v
        && v.TryGetValue<string>(out var s)
            ? s
            : null;
}
