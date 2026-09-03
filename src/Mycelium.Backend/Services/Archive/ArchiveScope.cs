using System.Text.Json.Nodes;

namespace Mycelium.Backend.Services.Archive;

/// <summary>
/// Cuts an <see cref="ArchiveInput"/> down to one person.
///
/// <para>The line it draws is between <b>the library</b> and <b>what somebody thought of it</b>. The
/// library — every artist, every album, every track listing, and the manual corrections made to match
/// them up — belongs to nobody in particular and is kept whole, because a list of your ratings with
/// the records removed is unreadable: the album files are the frame the opinions hang on. Everything
/// authored by a person is kept only where that person is the one asking.</para>
///
/// <para>A filter over the input rather than an option on <see cref="ArchiveBuilder"/>, so the shaping
/// step stays a single pure function with one behaviour. It also means a takeout is byte-for-byte the
/// format the nightly archive writes — the same files, with other people's rows never read.</para>
/// </summary>
public static class ArchiveScope
{
    /// <summary>
    /// Everything in <paramref name="input"/> that belongs to <paramref name="subject"/>, plus the
    /// library itself.
    /// </summary>
    public static ArchiveInput ForUser(ArchiveInput input, string subject)
    {
        var owned = Aliases(input.Users, subject);

        return input with
        {
            Users = Only(input.Users, u => Str(u, "_id") == subject),
            PlexLinks = Only(input.PlexLinks, l => Str(l, "_id") == subject),
            ArtistVerdicts = Only(input.ArtistVerdicts, v => Str(v, "userId") == subject),
            TrackRatings = Only(input.TrackRatings, r => Str(r, "userId") == subject),
            Playlists = Only(input.Playlists, p => Str(p, "userId") == subject),

            // These two name a person rather than pointing at one, and both hold a username: `addedBy`
            // always has, `blockedBy` since the startup migration rewrote it. The subject is still
            // matched as well, because that migration can only reach rows whose user still exists —
            // and a person's own history must not be filtered out from under them over a spelling. A
            // row crediting nobody — most acquisitions, which arrive automatically off a like —
            // belongs to no takeout and is dropped.
            Purchases = Only(input.Purchases, p => Str(p, "addedBy") is { } by && owned.Contains(by)),
            Blocks = Only(input.Blocks, b => Str(b, "blockedBy") is { } by && owned.Contains(by)),

            // Artists, MatchOverrides and LibraryTracks are deliberately untouched: they are the
            // library, not anybody's opinion of it.
        };
    }

    /// <summary>
    /// Every spelling of this person that a stored row might carry: their subject, and the username
    /// the identity provider gave them. Compared case-insensitively, because nothing normalises the
    /// username on the way in and an identity provider is free to change its mind about case.
    /// </summary>
    private static HashSet<string> Aliases(IReadOnlyList<JsonObject> users, string subject)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { subject };

        foreach (var user in users.Where(u => Str(u, "_id") == subject))
        {
            if (Str(user, "username") is { } username)
            {
                aliases.Add(username);
            }
        }

        return aliases;
    }

    private static IReadOnlyList<JsonObject> Only(
        IReadOnlyList<JsonObject> rows, Func<JsonObject, bool> keep) =>
        rows.Where(keep).ToList();

    private static string? Str(JsonObject record, string field) =>
        record.TryGetPropertyValue(field, out var value) && value is JsonValue v
        && v.TryGetValue<string>(out var s)
            ? s
            : null;
}
