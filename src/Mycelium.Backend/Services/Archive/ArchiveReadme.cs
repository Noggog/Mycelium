namespace Mycelium.Backend.Services.Archive;

/// <summary>
/// The README written into the archive repository on first run.
///
/// <para>The archive exists to be read by something that isn't this application — most likely a
/// migration script for whatever replaces Plex, written by someone who has no access to this
/// codebase and possibly years from now. A pile of JSON Lines with no explanation is data; a pile
/// with a key to it is a record. This is the difference, and it costs one file.</para>
///
/// <para>Written only when absent, never overwritten: once it is in the repository it belongs to
/// whoever owns the repository, and they may well want to add to it.</para>
/// </summary>
public static class ArchiveReadme
{
    public const string FileName = "README.md";

    public static string Contents => $"""
# Mycelium metadata archive

This repository is a durable copy of the music-library metadata that a machine cannot re-derive:
what is in the library, what each person thinks of it, who brought each record in, and the standing
decisions people have made about it.

It is written automatically, once a night, by Mycelium. **It is only ever written — nothing reads it
back.** The point is that if the database, the Plex server and the identity provider were all lost,
this repository plus the music files would be enough to rebuild.

Schema version: {ArchiveBuilder.SchemaVersion} (see `MANIFEST.json`).

## Format

Every `.jsonl` file is [JSON Lines](https://jsonlines.org): one self-contained JSON object per line,
sorted, with object keys in alphabetical order. That is deliberate — it means one changed record is
one changed line, so `git log -p` reads as a history of decisions rather than a wall of noise.

A commit is made only when something actually changed, and its message summarises what.

There is no file-per-artist, because artist names are the primary keys here and they contain `/`
(`AC/DC`), non-ASCII characters, and case variants that collide on some filesystems. Keeping names
as *data* rather than as filenames avoids an escaping scheme that would have to be reversed on the
way back in.

## Files

| File | What it holds |
|---|---|
| `users.jsonl` | One row per person: their username, the identity-provider subject they were stored under, and their Plex account link (never its token) |
| `inventory.jsonl` | One row per artist: the albums held and at what audio quality, plus resolved MusicBrainz/Deezer identities and any hand-pinned corrections |
| `taste/<user>.jsonl` | That person's verdicts on artists and albums — liked, disliked, snoozed, and whether they confirmed it |
| `stars/<user>.jsonl` | That person's song ratings, 0–5 stars, harvested from Plex |
| `playlists/<user>.jsonl` | That person's playlists. Rule-driven ones keep their rules; hand-built ones keep their ordered track list |
| `downloads.jsonl` | Every record acquired: what, when, at what quality, and who asked for it |
| `decisions.jsonl` | Standing decisions — albums blocked, and manual "this release is that one we already own" corrections |
| `MANIFEST.json` | Schema version and per-file record counts |

## Keys, and what to join on

Read this part before writing anything that consumes the archive.

- **People are keyed by `username`**, not by the identity-provider subject. Subjects are reissued if
  the provider is ever rebuilt, which would silently orphan every rating in here. The original
  `subject` is kept as a field in `users.jsonl` so an exact restore is still possible where the
  provider survived.
- **Artists are keyed by name**, because that is how the source system keyed them. Where an identity
  was resolved, `inventory.jsonl` also carries `musicBrainzMbid` — the only identifier in here that
  is stable forever, and the right thing to re-key on if names have drifted.
- **Tracks are keyed by file path.** Media-server track ids are local handles that a rebuilt server
  reissues; the files outlive the server that indexed them. Artist/album/title travel alongside for
  the cases where the paths have moved.

## What is deliberately absent

- **Credentials.** No access tokens, ever. Git history is permanent.
- **Email addresses.** Not needed to restore anything, and they only raise the cost of a leak.
- **Anything a job can rebuild** — similarity graphs, recommendation scores, popularity counts,
  server-local ids, "last seen" timestamps. They would churn every night and bury the lines that
  matter.

Because derived data is excluded, this is not a database backup and will not restore as one. It is
the record of what was decided.

## Privacy

This repository names real people. Keep it private.
""";
}
