using System.Text;
using System.Text.Json.Nodes;

namespace Mycelium.Backend.Services.Archive;

/// <summary>One file of the archive, as it should exist on disk.</summary>
public record ArchiveFile(string RelativePath, string Contents);

/// <summary>
/// The raw collection dumps a snapshot is built from. Passed in rather than read here so the whole
/// shaping step is a pure function — the interesting decisions (what to keep, how to lay it out) are
/// then testable with plain object literals and no database.
/// </summary>
public record ArchiveInput(
    IReadOnlyList<JsonObject> Users,
    IReadOnlyList<JsonObject> PlexLinks,
    IReadOnlyList<JsonObject> Artists,
    IReadOnlyList<JsonObject> ArtistVerdicts,
    IReadOnlyList<JsonObject> Purchases,
    IReadOnlyList<JsonObject> Blocks,
    IReadOnlyList<JsonObject> MatchOverrides,
    IReadOnlyList<JsonObject> TrackRatings,
    IReadOnlyList<JsonObject> Playlists,
    IReadOnlyList<JsonObject> LibraryTracks);

/// <summary>
/// Lays the archive out as the library itself: a directory per artist, a file per album.
///
/// <code>
/// Library/
///   Radiohead/
///     metadata.json      identity, genres, who likes the artist
///     Kid A.json         quality, who brought it in, who likes it, its songs and their ratings
/// </code>
///
/// <para>The shape is the point. A snapshot answers "what did the library look like on this day", and
/// everything true of one album sits in one file — so a diff reads as "Kelsey rated three songs on
/// <i>Kid A</i>" rather than as a line moving inside a 3,000-line blob. It also means the archive
/// browses like the thing it describes, which matters when the reader is a person, or a migration
/// script for whatever replaces Plex years from now.</para>
///
/// <para>Two rules decide the contents. <b>Keep what a person decided; drop what a job can rebuild</b>
/// — verdicts, ratings, acquisitions and hand-pinned identities are irreplaceable, while similarity
/// graphs, recommendation scores and server-local ids are not, and archiving them would rewrite the
/// tree nightly. And <b>key on what survives a rebuild</b>: people by username, tracks by file path,
/// artists by MusicBrainz id where one is known.</para>
/// </summary>
public class ArchiveBuilder
{
    private const string LibraryRoot = "Library";
    private const string ArtistMetadata = "metadata.yaml";

    /// <summary>Joins the parts of a sort key. A separator no title will contain.</summary>
    private const char KeySeparator = '\u001f';

    public IReadOnlyList<ArchiveFile> Build(ArchiveInput input)
    {
        var identities = Identities(input.Users);
        var files = new List<ArchiveFile>
        {
            new("users.yaml", CanonicalYaml.Document(Users(input, identities))),
            new("decisions.yaml", CanonicalYaml.Document(Decisions(input, identities))),
        };

        files.AddRange(Library(input, identities));
        files.AddRange(Playlists(input.Playlists, identities));
        return files;
    }

    // ---- users ----

    private static JsonArray Users(ArchiveInput input, IReadOnlyDictionary<string, string> identities)
    {
        var linkBySubject = ByKey(input.PlexLinks, "_id");
        var rows = new List<(string Sort, JsonObject Row)>();

        foreach (var user in input.Users)
        {
            var subject = Str(user, "_id");
            if (subject is null)
            {
                continue;
            }

            var username = identities.GetValueOrDefault(subject, FileName(subject));

            // No `subject`, no `email`, no `lastLoginAt`. The first is an identity-provider detail that
            // means nothing outside the provider that issued it; the second identifies nobody here and
            // only raises the cost of a leak; the third changes whenever someone opens the app and
            // would commit noise nightly.
            var row = new JsonObject { ["username"] = username };
            Copy(user, row, "displayName", "firstSeenAt", "maxQuality");

            // The fact of a Plex link, never its token — that is a live credential, and git is forever.
            if (linkBySubject.TryGetValue(subject, out var link))
            {
                var plex = new JsonObject();
                Put(plex, "username", link["username"]);
                Put(plex, "accountId", link["accountId"]);
                Put(plex, "linkedAt", link["linkedAt"]);
                row["plex"] = plex;
            }

            rows.Add((username, row));
        }

        return Sorted(rows);
    }

    // ---- library ----

    private static IEnumerable<ArchiveFile> Library(
        ArchiveInput input, IReadOnlyDictionary<string, string> identities)
    {
        var files = new List<ArchiveFile>();

        var artists = input.Artists.Where(a => Str(a, "_id") is not null).ToList();
        var artistPaths = ArchivePaths.ForNames(artists.Select(a => Str(a, "_id")!));

        var artistRatings = ArtistVerdicts(input.ArtistVerdicts, identities);
        var acquisitions = Acquisitions(input.Purchases);
        var songs = SongsByAlbum(input.LibraryTracks, input.TrackRatings, identities);

        foreach (var artist in artists)
        {
            var name = Str(artist, "_id")!;
            var directory = $"{LibraryRoot}/{artistPaths[name]}";

            files.Add(new ArchiveFile(
                $"{directory}/{ArtistMetadata}",
                CanonicalYaml.Document(ArtistFile(artist, name, artistRatings))));

            var albums = Strings(artist, "albums");
            var quality = AlbumQuality(artist);
            var albumPaths = ArchivePaths.ForNames(albums);

            foreach (var album in albums)
            {
                files.Add(new ArchiveFile(
                    $"{directory}/{albumPaths[album]}.yaml",
                    CanonicalYaml.Document(AlbumFile(name, album, quality, acquisitions, songs))));
            }
        }

        return files;
    }

    private static JsonObject ArtistFile(
        JsonObject artist,
        string name,
        IReadOnlyDictionary<string, JsonObject> artistRatings)
    {
        var row = new JsonObject { ["artist"] = name };

        // Nothing of the catalog row itself is kept. `lastSeenAt`, `present`, `plexRatingKeys` and
        // `albumKeys` are server-local and move on every sync; `deezerFans` is a popularity counter that
        // drifts daily; `imageUrl` is a CDN link the enricher refills for free; and `genres` are
        // mirrored from the media server, which clears and rewrites them on each pass. All of it is
        // re-derivable, and none of it would mean anything on new hardware.

        // The identity pins are why this file is worth keeping: each is a human correcting a bad
        // automatic match, and the MusicBrainz id is the only identifier here stable forever.
        var musicBrainz = Identity(
            artist, "musicBrainzMbid", "mbid", "musicBrainzName",
            "musicBrainzOverride", "musicBrainzUnlinked", "musicBrainzDisambiguation", null);
        if (musicBrainz.Count > 0)
        {
            row["musicBrainz"] = musicBrainz;
        }

        var deezer = Identity(
            artist, "deezerId", "id", "deezerName",
            "deezerOverride", "deezerUnlinked", null, "deezerLink");
        if (deezer.Count > 0)
        {
            row["deezer"] = deezer;
        }

        if (artistRatings.TryGetValue(Fold(name), out var ratings))
        {
            row["ratings"] = ratings.DeepClone();
        }

        return row;
    }

    private static JsonObject AlbumFile(
        string artist,
        string album,
        IReadOnlyDictionary<string, string> quality,
        IReadOnlyDictionary<string, string> acquisitions,
        IReadOnlyDictionary<string, JsonArray> songs)
    {
        var row = new JsonObject
        {
            ["album"] = album,
            ["artist"] = artist,
        };

        if (quality.TryGetValue(album, out var tier))
        {
            row["quality"] = tier;
        }

        var key = AlbumKey(artist, album);

        // Who brought this record in — and only that. The purchase row it comes from also knows when it
        // landed and at what tier, but the first is already implicit in the commit that added this file
        // and the second is the `quality` above; repeating either would be two fields to keep in step.
        //
        // No album-level verdicts here. A thumbs-up on an album in Mycelium means "fetch this", not
        // "this is good" — for an album the library already holds, the acquisition below is what that
        // decision actually produced.
        if (acquisitions.TryGetValue(key, out var by))
        {
            row["acquiredBy"] = by;
        }

        if (songs.TryGetValue(key, out var tracks))
        {
            row["songs"] = tracks.DeepClone();
        }

        return row;
    }

    // ---- songs ----

    /// <summary>
    /// Album key -> its tracks, each carrying whatever star ratings people have given it.
    ///
    /// <para>The listing comes from the library-wide sweep and the ratings from the per-user ones,
    /// joined on the file path — the one identity a track keeps when the server is rebuilt.</para>
    ///
    /// <para>A rated track missing from the listing is carried anyway, built from what the rating
    /// itself knows. The two come from separate reads, and the listing is the one more likely to fail
    /// or to disagree about a name — so keying songs off it alone would let a partial failure silently
    /// drop the ratings, which are the least reconstructable thing in the archive. Better an album
    /// whose listing is short than one that quietly forgets what somebody thought of it.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, JsonArray> SongsByAlbum(
        IReadOnlyList<JsonObject> libraryTracks,
        IReadOnlyList<JsonObject> trackRatings,
        IReadOnlyDictionary<string, string> identities)
    {
        var ratings = new Dictionary<string, SortedDictionary<string, JsonNode?>>(StringComparer.Ordinal);
        foreach (var rating in trackRatings)
        {
            var subject = Str(rating, "userId");
            var file = Str(rating, "file");
            if (subject is null || file is null || !rating.TryGetPropertyValue("stars", out var stars)
                || stars is null)
            {
                continue;
            }

            var user = identities.GetValueOrDefault(subject, FileName(subject));
            if (!ratings.TryGetValue(file, out var byUser))
            {
                ratings[file] = byUser = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
            }

            byUser[user] = stars.DeepClone();
        }

        // The listing, plus any rated track it doesn't mention. Deduped by file, so a track present in
        // both is listed once.
        var tracks = new List<JsonObject>(libraryTracks);
        var listed = libraryTracks
            .Select(t => Str(t, "file"))
            .Where(f => f is not null)
            .ToHashSet(StringComparer.Ordinal)!;

        foreach (var rating in trackRatings)
        {
            var file = Str(rating, "file");
            if (file is null || !listed.Add(file))
            {
                continue;
            }

            // The rating rows carry artist, album, title and track number of their own, which is
            // exactly what a song entry needs.
            tracks.Add(rating);
        }

        var byAlbum = new Dictionary<string, List<(int Track, string Title, JsonObject Row)>>(
            StringComparer.Ordinal);

        foreach (var track in tracks)
        {
            var artist = Str(track, "artist");
            var album = Str(track, "album");
            if (artist is null || album is null)
            {
                continue;
            }

            // Title and ratings only. The track number is implicit in the running order below, and the
            // file path is this server's own namespace — it wouldn't resolve on the system this archive
            // is meant to be read by, where artist/album/title is the portable way to find a song.
            var row = new JsonObject();
            Copy(track, row, "title");

            if (Str(track, "file") is { } file && ratings.TryGetValue(file, out var byUser))
            {
                var map = new JsonObject();
                foreach (var (user, stars) in byUser)
                {
                    map[user] = stars?.DeepClone();
                }

                row["ratings"] = map;
            }

            var key = AlbumKey(artist, album);
            if (!byAlbum.TryGetValue(key, out var list))
            {
                byAlbum[key] = list = [];
            }

            list.Add((Int(track, "trackNumber") ?? int.MaxValue, Str(track, "title") ?? "", row));
        }

        return byAlbum.ToDictionary(
            pair => pair.Key,
            // Running order, with the title as tiebreak so a disc with no track numbers still lands in
            // a stable sequence rather than shuffling between snapshots.
            pair =>
            {
                var array = new JsonArray();
                foreach (var entry in pair.Value
                             .OrderBy(t => t.Track)
                             .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(t => t.Title, StringComparer.Ordinal))
                {
                    array.Add(entry.Row);
                }

                return array;
            },
            StringComparer.Ordinal);
    }

    // ---- verdicts ----

    private static IReadOnlyDictionary<string, JsonObject> ArtistVerdicts(
        IReadOnlyList<JsonObject> verdicts, IReadOnlyDictionary<string, string> identities)
    {
        var byArtist = new Dictionary<string, SortedDictionary<string, JsonObject>>(StringComparer.Ordinal);

        foreach (var verdict in verdicts)
        {
            var subject = Str(verdict, "userId");
            var artist = Str(verdict, "artist");
            var status = Str(verdict, "status");

            // Pending rows are the recommendation queue, not taste: the replenisher rebuilds them from
            // the similarity graph, so archiving them would churn constantly while preserving nothing
            // anyone chose.
            if (subject is null || artist is null || status is null || status == "Pending")
            {
                continue;
            }

            var user = identities.GetValueOrDefault(subject, FileName(subject));
            if (!byArtist.TryGetValue(Fold(artist), out var byUser))
            {
                byArtist[Fold(artist)] = byUser =
                    new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
            }

            byUser[user] = Verdict(verdict, status);
        }

        return Collapse(byArtist);
    }

    private static JsonObject Verdict(JsonObject source, string status)
    {
        var row = new JsonObject { ["verdict"] = status };
        Copy(source, row, "decidedAt", "snoozeUntil");

        // The two directional flags collapse to one, since a row only carries a single verdict.
        // "I meant it" is a hand-made decision nothing can re-derive.
        var confirmed = status switch
        {
            "Liked" => Bool(source, "likeConfirmed"),
            "Disliked" => Bool(source, "dislikeConfirmed"),
            _ => false,
        };

        if (confirmed)
        {
            row["confirmed"] = true;
        }

        return row;
    }

    // ---- acquisitions ----

    /// <summary>
    /// Album key -> who asked for it. Only rows that actually name someone: most acquisitions were
    /// automatic (downloaded off a like rather than a button press) and have nobody to credit, and an
    /// empty field on those would say nothing at length.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Acquisitions(IReadOnlyList<JsonObject> purchases)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var purchase in purchases)
        {
            var artist = Str(purchase, "artist");
            var album = Str(purchase, "album");
            var by = Str(purchase, "addedBy");
            if (artist is null || album is null || by is null)
            {
                continue;
            }

            result[AlbumKey(artist, album)] = by;
        }

        return result;
    }

    // ---- decisions ----

    /// <summary>
    /// Blocks and manual match corrections. Outside <c>Library/</c> deliberately: a block is usually
    /// about a record the library does <em>not</em> have, so there is no album file for it to live in.
    /// </summary>
    private static JsonArray Decisions(ArchiveInput input, IReadOnlyDictionary<string, string> identities)
    {
        var rows = new List<(string Sort, JsonObject Row)>();

        foreach (var block in input.Blocks)
        {
            var row = new JsonObject { ["kind"] = "block" };
            Copy(block, row, "artist", "album", "scope", "createdAt", "retryAfter");

            // The block endpoint stores the OIDC subject where the purchase rows store a username, so
            // left alone this is an opaque hash — and meaningless once the identity provider is rebuilt.
            // A value matching no known subject is kept as-is: older rows may already hold a name.
            if (Str(block, "blockedBy") is { } by)
            {
                row["blockedBy"] = identities.TryGetValue(by, out var name) ? name : by;
            }

            rows.Add((SortKey("block", Str(block, "artist"), Str(block, "album")), row));
        }

        foreach (var over in input.MatchOverrides)
        {
            var row = new JsonObject { ["kind"] = "match" };
            Put(row, "artist", over["matchArtist"]);
            Put(row, "album", over["deezerTitle"]);
            Copy(over, row, "libraryTitle", "createdAt");
            rows.Add((SortKey("match", Str(over, "matchArtist"), Str(over, "deezerTitle")), row));
        }

        return Sorted(rows);
    }

    // ---- playlists ----

    private static IEnumerable<ArchiveFile> Playlists(
        IReadOnlyList<JsonObject> playlists, IReadOnlyDictionary<string, string> identities)
    {
        var byUser = new Dictionary<string, List<(string Sort, JsonObject Row)>>(StringComparer.Ordinal);

        foreach (var playlist in playlists)
        {
            var subject = Str(playlist, "userId");
            var title = Str(playlist, "title");
            if (subject is null || title is null)
            {
                continue;
            }

            var row = new JsonObject { ["title"] = title };

            // Smart playlists keep their rules, hand-built ones their tracks. The rules are the durable
            // thing; a smart playlist's membership is only their current answer and would go stale the
            // moment the library changed.
            Copy(playlist, row, "smart", "rules");
            if (!Bool(playlist, "smart") && playlist["tracks"] is JsonArray tracks)
            {
                row["tracks"] = Entries(tracks);
            }

            var user = identities.GetValueOrDefault(subject, FileName(subject));
            if (!byUser.TryGetValue(user, out var rows))
            {
                byUser[user] = rows = [];
            }

            rows.Add((title, row));
        }

        return byUser
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ArchiveFile(
                $"playlists/{pair.Key}.yaml", CanonicalYaml.Document(Sorted(pair.Value))))
            .ToList();
    }

    /// <summary>
    /// A playlist's tracks, cut to what identifies a song anywhere: artist, album, title. The stored
    /// position is implicit in the running order below it, and the file path is the source server's own
    /// namespace — it wouldn't resolve on the system this archive is meant to be read by.
    /// </summary>
    private static JsonArray Entries(JsonArray tracks)
    {
        var array = new JsonArray();
        foreach (var track in tracks.OfType<JsonObject>())
        {
            var entry = new JsonObject();
            Copy(track, entry, "artist", "album", "title");
            array.Add(entry);
        }

        return array;
    }

    // ---- helpers ----

    /// <summary>
    /// Subject -> the name this person is filed under. Collisions are broken by appending part of the
    /// subject: two accounts whose usernames reduce to the same thing must not silently merge.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Identities(IReadOnlyList<JsonObject> users)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var candidates = users
            .Select(u => (Subject: Str(u, "_id"), Name: Str(u, "username")))
            .Where(u => u.Subject is not null)
            // Ordered by subject so a collision resolves the same way on every run.
            .OrderBy(u => u.Subject, StringComparer.Ordinal);

        foreach (var (subject, username) in candidates)
        {
            var name = FileName(username ?? subject!);
            if (taken.TryGetValue(name, out var owner) && owner != subject)
            {
                name = $"{name}-{FileName(subject!)[..Math.Min(6, FileName(subject!).Length)]}";
            }

            taken[name] = subject!;
            result[subject!] = name;
        }

        return result;
    }

    /// <summary>
    /// A username reduced to something safe in a path. Its own rule rather than a call to
    /// <c>ArtistTag</c>'s sanitizer, which produces Plex mood tags: those can be re-derived at will,
    /// whereas a filename that changes orphans everything git knows about that person's history.
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

    private static JsonObject Identity(
        JsonObject artist, string idField, string idName, string nameField,
        string overrideField, string unlinkedField, string? disambiguationField, string? linkField)
    {
        var identity = new JsonObject();
        Put(identity, idName, artist[idField]);
        Put(identity, "name", artist[nameField]);

        if (linkField is not null)
        {
            Put(identity, "link", artist[linkField]);
        }

        if (disambiguationField is not null)
        {
            Put(identity, "disambiguation", artist[disambiguationField]);
        }

        // Only written when true: a `false` on every artist would be thousands of lines saying nothing.
        if (Bool(artist, overrideField))
        {
            identity["pinned"] = true;
        }

        if (Bool(artist, unlinkedField))
        {
            identity["unlinked"] = true;
        }

        return identity;
    }

    private static IReadOnlyDictionary<string, string> AlbumQuality(JsonObject artist)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (artist["albumQuality"] is not JsonArray entries)
        {
            return result;
        }

        foreach (var entry in entries.OfType<JsonObject>())
        {
            if (Str(entry, "title") is { } title && Str(entry, "quality") is { } quality)
            {
                result[title] = quality;
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, JsonObject> Collapse(
        Dictionary<string, SortedDictionary<string, JsonObject>> source)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (key, byUser) in source)
        {
            if (byUser.Count == 0)
            {
                continue;
            }

            var map = new JsonObject();
            foreach (var (user, value) in byUser)
            {
                map[user] = value.DeepClone();
            }

            result[key] = map;
        }

        return result;
    }

    private static JsonArray Sorted(List<(string Sort, JsonObject Row)> rows)
    {
        var array = new JsonArray();
        foreach (var entry in rows
                     .OrderBy(r => r.Sort, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.Sort, StringComparer.Ordinal))
        {
            array.Add(entry.Row);
        }

        return array;
    }

    private static string SortKey(params string?[] parts) =>
        string.Join(KeySeparator, parts.Select(p => p ?? ""));

    private static string AlbumKey(string artist, string album) =>
        $"{Fold(artist)}{KeySeparator}{Fold(album)}";

    private static string Fold(string value) => value.ToLowerInvariant();

    private static Dictionary<string, JsonObject> ByKey(IReadOnlyList<JsonObject> rows, string field) =>
        rows.Where(r => Str(r, field) is not null)
            .GroupBy(r => Str(r, field)!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private static List<string> Strings(JsonObject source, string field) =>
        source[field] is JsonArray array
            ? array.OfType<JsonValue>()
                .Select(v => v.TryGetValue<string>(out var s) ? s : null)
                .Where(s => s is not null)
                .Select(s => s!)
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : [];

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

    private static int? Int(JsonObject record, string field) =>
        record.TryGetPropertyValue(field, out var value) && value is JsonValue v
        && v.TryGetValue<long>(out var n)
            ? (int)n
            : null;

    private static bool Bool(JsonObject record, string field) =>
        record.TryGetPropertyValue(field, out var value) && value is JsonValue v
        && v.TryGetValue<bool>(out var b) && b;
}
