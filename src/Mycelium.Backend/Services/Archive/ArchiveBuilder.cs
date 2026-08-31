using System.Text;
using System.Text.Json.Nodes;

namespace Mycelium.Backend.Services.Archive;

/// <summary>
/// One file of the archive, as it should exist on disk.
/// </summary>
/// <param name="KeyFields">
/// The fields that identify a record within this file — the same ones it is sorted by. Carried so the
/// change summary can tell an edited record from a removal plus an addition, which raw line counts
/// can't. Empty for files that aren't record-oriented (the manifest).
/// </param>
public record ArchiveFile(string RelativePath, string Contents, IReadOnlyList<string> KeyFields);

/// <summary>
/// The raw collection dumps a snapshot is built from. Passed in rather than read here so the whole
/// shaping step is a pure function — the interesting decisions (what to keep, how to key it, how to
/// sort it) are then testable with plain object literals and no database.
/// </summary>
public record ArchiveInput(
    IReadOnlyList<JsonObject> Users,
    IReadOnlyList<JsonObject> PlexLinks,
    IReadOnlyList<JsonObject> Artists,
    IReadOnlyList<JsonObject> ArtistVerdicts,
    IReadOnlyList<JsonObject> AlbumVerdicts,
    IReadOnlyList<JsonObject> Purchases,
    IReadOnlyList<JsonObject> Blocks,
    IReadOnlyList<JsonObject> MatchOverrides,
    IReadOnlyList<JsonObject> TrackRatings,
    IReadOnlyList<JsonObject> Playlists);

/// <summary>
/// Turns collection dumps into the archive's file tree.
///
/// <para>Two rules run through everything here. <b>Keep what a person decided; drop what a job can
/// rebuild.</b> A verdict, a block, a pinned Deezer id and an acquisition are facts nothing can
/// reconstruct once lost. A similarity edge, a recommendation score, a Deezer fan count and a Plex
/// rating key are all re-derivable, and archiving them would rewrite most of the tree nightly and
/// bury the handful of lines that actually mattered.</para>
///
/// <para><b>Key on what survives a rebuild.</b> Per-user files are named and sorted by username, not
/// by the OIDC subject they're stored under: subjects are reissued if the identity provider is ever
/// rebuilt, which would silently orphan every rating in the system. The subject is kept as a field so
/// a restore into the same provider stays exact, and a restore into a new one is still possible by
/// hand.</para>
/// </summary>
public class ArchiveBuilder
{
    /// <summary>
    /// Bumped only when the shape of the files changes in a way a reader has to know about. Recorded
    /// in the manifest so a future importer can tell what it's looking at.
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// Separates key fields when they're joined into a record's identity. A unit separator rather than
    /// a space, so an artist called "Kids" and an album called "See" can't collide with an artist
    /// called "Kids See".
    /// </summary>
    private const char KeySeparator = '\u001f';

    public IReadOnlyList<ArchiveFile> Build(ArchiveInput input)
    {
        var files = new List<ArchiveFile>();

        // Subject -> archive identity. Everything per-user routes through this, so a user who somehow
        // has taste rows but no `users` document still lands somewhere sensible rather than vanishing.
        var identities = Identities(input.Users);

        files.Add(Jsonl("users.jsonl", Users(input.Users, input.PlexLinks, identities), "username"));
        files.Add(Jsonl("inventory.jsonl", Inventory(input.Artists), "artist"));
        files.Add(Jsonl("downloads.jsonl", Downloads(input.Purchases), "key"));
        files.Add(Jsonl("decisions.jsonl", Decisions(input.Blocks, input.MatchOverrides), "kind", "artist", "album"));

        files.AddRange(Taste(input.ArtistVerdicts, input.AlbumVerdicts, identities));
        files.AddRange(Stars(input.TrackRatings, identities));
        files.AddRange(Playlists(input.Playlists, identities));

        files.Add(Manifest(files));
        return files;
    }

    // ---- users ----

    private static IEnumerable<JsonObject> Users(
        IReadOnlyList<JsonObject> users,
        IReadOnlyList<JsonObject> links,
        IReadOnlyDictionary<string, string> identities)
    {
        var linkBySubject = links
            .Where(l => Str(l, "_id") is not null)
            .GroupBy(l => Str(l, "_id")!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var user in users)
        {
            var subject = Str(user, "_id");
            if (subject is null)
            {
                continue;
            }

            // `lastLoginAt` and `email` are both deliberately absent. The first changes every time
            // anyone opens the app, which would commit noise nightly for every active user; the second
            // isn't needed to restore anything (username is the key) and only makes the repo more
            // sensitive than it has to be.
            var row = new JsonObject
            {
                ["username"] = identities.GetValueOrDefault(subject, subject),
                ["subject"] = subject,
            };

            Copy(user, row, "displayName", "firstSeenAt", "maxQuality");

            // The fact of a Plex link, never the token. `serverToken` is a live credential; a leaked
            // one is forever, and re-linking is a 30-second PIN flow.
            if (linkBySubject.TryGetValue(subject, out var link))
            {
                Copy(link, row, "accountId", "linkedAt");
                Rename(row, "accountId", "plexAccountId");
                Rename(row, "linkedAt", "plexLinkedAt");
                Put(row, "plexUsername", link["username"]);
            }

            yield return row;
        }
    }

    // ---- inventory ----

    private static IEnumerable<JsonObject> Inventory(IReadOnlyList<JsonObject> artists)
    {
        foreach (var artist in artists)
        {
            var name = Str(artist, "_id");
            if (name is null)
            {
                continue;
            }

            var row = new JsonObject { ["artist"] = name };

            // What we hold, and at what quality. Everything else the catalog row carries is Plex- or
            // Deezer-local churn: `lastSeenAt` and `present` move on every sync, `plexRatingKeys` and
            // `albumKeys` are server-local handles re-captured each time and meaningless on new
            // hardware, `deezerFans` is a popularity counter that drifts daily, and `imageUrl` is a CDN
            // link the enricher refills for free.
            Copy(artist, row, "albums", "albumQuality", "genres");

            // The identity pins are the whole reason this file is worth keeping. Each one is a human
            // correcting a bad automatic match, and nothing can re-derive that judgement.
            Copy(artist, row,
                "deezerId", "deezerName", "deezerLink", "deezerOverride", "deezerUnlinked",
                "musicBrainzMbid", "musicBrainzName", "musicBrainzDisambiguation",
                "musicBrainzOverride", "musicBrainzUnlinked");

            yield return row;
        }
    }

    // ---- taste ----

    private static IEnumerable<ArchiveFile> Taste(
        IReadOnlyList<JsonObject> artistVerdicts,
        IReadOnlyList<JsonObject> albumVerdicts,
        IReadOnlyDictionary<string, string> identities)
    {
        var byUser = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);

        void Add(string subject, JsonObject row)
        {
            var user = identities.GetValueOrDefault(subject, FileName(subject));
            if (!byUser.TryGetValue(user, out var rows))
            {
                byUser[user] = rows = new List<JsonObject>();
            }

            rows.Add(row);
        }

        foreach (var verdict in artistVerdicts)
        {
            var subject = Str(verdict, "userId");
            var artist = Str(verdict, "artist");
            var status = Str(verdict, "status");

            // Pending rows are the recommendation queue, not taste — the replenisher rebuilds them
            // from the similarity graph, so archiving them would churn the file constantly while
            // preserving nothing anybody chose.
            if (subject is null || artist is null || status is null || status == "Pending")
            {
                continue;
            }

            var row = new JsonObject
            {
                ["kind"] = "artist",
                ["artist"] = artist,
                ["status"] = status,
            };

            Copy(verdict, row, "decidedAt", "snoozeUntil");

            // The two confirm flags collapse to one: a row has a single verdict, so only the flag
            // matching it can be meaningful. `score`/`sources`/`depth`/`addedAt` are queue mechanics
            // and `reconsider` is a cached read of Plex stars — all re-derived, none archived.
            var confirmed = status switch
            {
                "Liked" => Bool(verdict, "likeConfirmed"),
                "Disliked" => Bool(verdict, "dislikeConfirmed"),
                _ => false,
            };

            if (confirmed)
            {
                row["confirmed"] = true;
            }

            Add(subject, row);
        }

        foreach (var verdict in albumVerdicts)
        {
            var subject = Str(verdict, "userId");
            var artist = Str(verdict, "artist");
            var album = Str(verdict, "album");
            var status = Str(verdict, "status");
            if (subject is null || artist is null || album is null || status is null)
            {
                continue;
            }

            var row = new JsonObject
            {
                ["kind"] = "album",
                ["artist"] = artist,
                ["album"] = album,
                ["status"] = status,
            };

            Copy(verdict, row, "decidedAt", "snoozeUntil");
            Add(subject, row);
        }

        // Sorted by artist first so everything one person thinks about an act sits together, with the
        // artist-level verdict ahead of its albums.
        return byUser
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => Jsonl($"taste/{pair.Key}.jsonl", pair.Value, "artist", "kind", "album"))
            .ToList();
    }

    // ---- stars ----

    /// <summary>
    /// Per-user song ratings, harvested out of Plex into Mongo by <c>StarHarvester</c> and archived
    /// from there like everything else.
    ///
    /// <para>These are one of only two things (playlists being the other) that exist nowhere but the
    /// Plex server, which is precisely why they're worth committing. Each row keeps the file path,
    /// because that is the identity a rating can be re-attached by on a system that has never heard of
    /// this Plex server — rating keys don't survive a rebuild, and files do.</para>
    /// </summary>
    private static IEnumerable<ArchiveFile> Stars(
        IReadOnlyList<JsonObject> ratings,
        IReadOnlyDictionary<string, string> identities)
    {
        var byUser = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);

        foreach (var rating in ratings)
        {
            var subject = Str(rating, "userId");
            if (subject is null)
            {
                continue;
            }

            var row = new JsonObject();
            Copy(rating, row, "artist", "album", "title", "trackNumber", "file", "stars");

            var user = identities.GetValueOrDefault(subject, FileName(subject));
            if (!byUser.TryGetValue(user, out var rows))
            {
                byUser[user] = rows = new List<JsonObject>();
            }

            rows.Add(row);
        }

        // Keyed by file as well as by the readable triple: a library can hold two tracks with the same
        // title on the same album, and without the path they'd collapse into one another.
        return byUser
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => Jsonl($"stars/{pair.Key}.jsonl", pair.Value, "artist", "album", "title", "file"))
            .ToList();
    }

    // ---- playlists ----

    /// <summary>
    /// Per-user playlists, harvested out of Plex into Mongo by <c>PlaylistHarvester</c>.
    ///
    /// <para>Smart playlists archive as their rules and hand-built ones as their ordered tracks — the
    /// harvester already made that split, and it is preserved here rather than flattened, because the
    /// two are durable in different places. A smart playlist's membership would go stale the moment
    /// the library changed; a curated one's *is* the playlist.</para>
    /// </summary>
    private static IEnumerable<ArchiveFile> Playlists(
        IReadOnlyList<JsonObject> playlists,
        IReadOnlyDictionary<string, string> identities)
    {
        var byUser = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);

        foreach (var playlist in playlists)
        {
            var subject = Str(playlist, "userId");
            var title = Str(playlist, "title");
            if (subject is null || title is null)
            {
                continue;
            }

            var row = new JsonObject { ["title"] = title };
            Copy(playlist, row, "smart", "rules", "tracks");

            var user = identities.GetValueOrDefault(subject, FileName(subject));
            if (!byUser.TryGetValue(user, out var rows))
            {
                byUser[user] = rows = new List<JsonObject>();
            }

            rows.Add(row);
        }

        return byUser
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => Jsonl($"playlists/{pair.Key}.jsonl", pair.Value, "title"))
            .ToList();
    }

    // ---- downloads ----

    private static IEnumerable<JsonObject> Downloads(IReadOnlyList<JsonObject> purchases)
    {
        foreach (var purchase in purchases)
        {
            var id = Str(purchase, "_id");
            if (id is null)
            {
                continue;
            }

            var row = new JsonObject { ["key"] = id };

            // This file is the only durable record of what came into the library and who asked for it:
            // Mongo keeps just the current row and the reconcile is allowed to delete it, so the git
            // history *is* the download history. `score`/`sources`/`imageUrl` are recommendation
            // machinery that moves on its own and would obscure that.
            Copy(purchase, row,
                "kind", "artist", "album", "albumArtist", "status",
                "requestedAt", "sentAt", "deezerAlbumId", "addedBy", "manual",
                "targetQuality", "acquiredQuality", "ownedQuality", "failure");

            yield return row;
        }
    }

    // ---- decisions ----

    private static IEnumerable<JsonObject> Decisions(
        IReadOnlyList<JsonObject> blocks,
        IReadOnlyList<JsonObject> overrides)
    {
        foreach (var block in blocks)
        {
            var row = new JsonObject { ["kind"] = "block" };
            Copy(block, row, "artist", "album", "scope", "blockedBy", "createdAt", "retryAfter");
            yield return row;
        }

        foreach (var over in overrides)
        {
            // Filed under the same artist/album key fields as a block, so the two sort together and a
            // reader sees every standing decision about a record in one place.
            var row = new JsonObject { ["kind"] = "match" };
            Put(row, "artist", over["matchArtist"]);
            Put(row, "album", over["deezerTitle"]);
            Copy(over, row, "libraryTitle", "createdAt");
            yield return row;
        }
    }

    // ---- manifest ----

    /// <summary>
    /// Schema version and per-file record counts — and pointedly no "generated at" stamp. A timestamp
    /// in a tracked file would change on every run, so every night would produce a commit and the
    /// commit-only-on-change rule that keeps this history readable would quietly stop working.
    /// </summary>
    private static ArchiveFile Manifest(IReadOnlyList<ArchiveFile> files)
    {
        var builder = new StringBuilder();
        builder.Append("{\n  \"schemaVersion\": ").Append(SchemaVersion).Append(",\n  \"files\": {\n");

        var counted = files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList();
        for (var i = 0; i < counted.Count; i++)
        {
            builder.Append("    \"").Append(counted[i].RelativePath).Append("\": ").Append(LineCount(counted[i]));
            builder.Append(i == counted.Count - 1 ? "\n" : ",\n");
        }

        builder.Append("  }\n}\n");
        return new ArchiveFile("MANIFEST.json", builder.ToString(), Array.Empty<string>());
    }

    private static int LineCount(ArchiveFile file) =>
        file.Contents.Length == 0 ? 0 : file.Contents.TrimEnd('\n').Split('\n').Length;

    // ---- helpers ----

    private static ArchiveFile Jsonl(string path, IEnumerable<JsonObject> records, params string[] keyFields)
    {
        var builder = new StringBuilder();
        foreach (var record in records
                     .OrderBy(r => KeyOf(r, keyFields), StringComparer.OrdinalIgnoreCase)
                     // Ordinal tiebreak so two names differing only in case can't swap places between
                     // runs. Case-insensitive first because that is the order a person expects to read.
                     .ThenBy(r => KeyOf(r, keyFields), StringComparer.Ordinal))
        {
            builder.Append(CanonicalJson.Line(record)).Append('\n');
        }

        return new ArchiveFile(path, builder.ToString(), keyFields);
    }

    /// <summary>
    /// A record's identity within its file: its key fields joined. Doubles as the sort key, so a file's
    /// order and its notion of "the same record" can never disagree.
    /// </summary>
    public static string KeyOf(JsonObject record, IReadOnlyList<string> keyFields) =>
        string.Join(KeySeparator, keyFields.Select(f => Str(record, f) ?? ""));

    /// <summary>
    /// Subject -> the name this user is filed under. Collisions are broken by appending the subject:
    /// two accounts whose usernames reduce to the same thing must not silently share one file and
    /// overwrite each other's history.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Identities(IReadOnlyList<JsonObject> users)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Ordered by subject so the winner of a collision is the same on every run.
        var candidates = users
            .Select(u => (Subject: Str(u, "_id"), Name: Str(u, "username")))
            .Where(u => u.Subject is not null)
            .OrderBy(u => u.Subject, StringComparer.Ordinal);

        foreach (var (subject, username) in candidates)
        {
            var name = FileName(username ?? subject!);
            if (taken.TryGetValue(name, out var owner) && owner != subject)
            {
                name = $"{name}-{FileName(subject!)}";
            }

            taken[name] = subject!;
            result[subject!] = name;
        }

        return result;
    }

    /// <summary>
    /// A username reduced to something safe to put in a path, on any filesystem.
    ///
    /// <para>Intentionally its own rule rather than a call to <c>ArtistTag</c>'s sanitizer, which
    /// produces Plex mood tags. These two look alike today but answer to different masters: a tag can
    /// be re-derived and rewritten at will, whereas a filename that changes orphans everything git
    /// knows about that user's history. This one must stay put.</para>
    /// </summary>
    private static string FileName(string username)
    {
        var at = username.IndexOf('@');
        var local = at >= 0 ? username[..at] : username;

        var builder = new StringBuilder(local.Length);
        foreach (var c in local.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
            {
                builder.Append(c);
            }
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }

    private static void Copy(JsonObject source, JsonObject target, params string[] fields)
    {
        foreach (var field in fields)
        {
            if (source.TryGetPropertyValue(field, out var value))
            {
                Put(target, field, value);
            }
        }
    }

    private static void Rename(JsonObject target, string from, string to)
    {
        if (target.TryGetPropertyValue(from, out var value) && value is not null)
        {
            target[to] = value.DeepClone();
        }

        target.Remove(from);
    }

    private static void Put(JsonObject target, string field, JsonNode? value)
    {
        if (value is null)
        {
            return;
        }

        // Cloned before re-parenting: a JsonNode belongs to exactly one parent, and assigning one that
        // still belongs to the dump would throw.
        target[field] = value.DeepClone();
    }

    private static string? Str(JsonObject record, string field) =>
        record.TryGetPropertyValue(field, out var value) && value is JsonValue v
        && v.TryGetValue<string>(out var s)
            ? s
            : null;

    private static bool Bool(JsonObject record, string field) =>
        record.TryGetPropertyValue(field, out var value) && value is JsonValue v
        && v.TryGetValue<bool>(out var b) && b;
}
