namespace Mycelium.Backend.Services.Archive;

/// <summary>
/// The README written into the archive repository on first run, and the one packed into a per-user
/// takeout zip. They share everything below the opening paragraphs — the format is the format.
///
/// <para>The archive exists to be read by something that isn't this application — most likely a
/// migration script for whatever replaces Plex, written by someone with no access to this codebase
/// and possibly years from now. A tree of JSON with no explanation is data; a tree with a key to it
/// is a record. That difference costs one file.</para>
///
/// <para>Into the repository it is written only when absent, never overwritten: once it is there it
/// belongs to whoever owns the repository, and they may well want to add to it.</para>
/// </summary>
public static class ArchiveReadme
{
    public const string FileName = "README.md";

    /// <summary>The nightly whole-library snapshot's README.</summary>
    public const string Contents = ArchiveIntro + "\n\n" + Format;

    /// <summary>
    /// The README inside a per-user takeout zip. Same format section, different opening: the reader
    /// is the person the data is about rather than a future migration, and what they most need told
    /// is which parts of the tree are theirs and which are the library everyone shares.
    /// </summary>
    public const string Takeout = TakeoutIntro + "\n\n" + Format;

    private const string ArchiveIntro = """
# Mycelium metadata archive

A durable copy of the music library's metadata: what is in it, what each person thinks of it, who
brought each record in, and the standing decisions people have made about it.

Written automatically once a night by Mycelium. **It is only ever written — nothing reads it back.**
The point is that if the database, the media server and the identity provider were all lost, this
repository plus the audio files would be enough to rebuild.

A commit is made only when something actually changed, and the message summarises what.
""";

    private const string TakeoutIntro = """
# Your Mycelium data

Everything Mycelium has recorded about **you**: the artists you liked, passed on or were indifferent
to, the records you asked for, the songs you rated, and your playlists — laid out against the library
they refer to.

A snapshot, taken at the moment you asked for it. It is the format Mycelium archives the whole
library in, with everybody else's opinions left out: the artists and albums are the shared library so
they are all here, but every judgement in these files is yours.

So in the layout below: `Library/` is everybody's, and there is exactly one person in `users.yaml`
and one file under `playlists/` — you.

Nothing reads this back and nothing expires. It is yours to keep, to read, or to feed to something
else.
""";

    /// <summary>How the tree is laid out and what its keys mean. True of both.</summary>
    private const string Format = """
## Layout

```
Library/
  Radiohead/
    metadata.yaml          the artist: resolved identities, and who likes them
    Kid A.yaml             the album: quality, who acquired it, its songs and their ratings
users.yaml                 the people, and their linked media-server accounts
decisions.yaml             blocked albums and manual match corrections
playlists/
  <person>.yaml            that person's playlists
```

A directory per artist and a file per album, so one changed record reads as
`~ Library/Radiohead/Kid A.yaml` rather than as lines shifting inside one enormous file. Keys are
sorted, and YAML block style has no commas — so adding a single rating is a single added line, with
nothing around it touched.

## An album file

```yaml
album: "Kid A"
artist: "Radiohead"
quality: "Lossless"
acquiredBy: "kelsey"
songs:
  - title: "Everything in Its Right Place"
  - title: "Idioteque"
    ratings:
      kelsey: 4.5
      noggog: 5
```

Every string is quoted deliberately, including ones that look like they don't need it. YAML coerces
bare scalars, and a music library is full of names that trip it: an album called `0034` would load as
`34`, `#digitalfreedom` would vanish as a comment, and the artists `Yes`, `No` and `Null` would come
back as booleans. Numbers and booleans are bare, so a rating is a number.

`ratings` under a song is that person's star rating, 0–5 with a half-star step, keyed by username. A
song nobody has rated simply has none.

Songs are listed in running order, so no track numbers are stored. `acquiredBy` names whoever asked
for the record by hand; most arrive automatically and name nobody. When it was acquired is not a
field — it is the commit that first added this file.

Albums carry no verdicts of their own. A thumbs-up on an album in the source application meant "fetch
this", not "this is good"; for a record the library already holds, `acquiredBy` is what that decision
produced, and the song ratings are the real per-person judgement.

## Filenames are locators, not data

Names in a real library are not filenames: album titles contain `/` and `:`, artists end in `.`, and
some pairs differ only by case, which is two directories on Linux and one on macOS. So path segments
are percent-encoded, and where two names would still collide both get a short suffix derived from
their own text.

**Always read the real name from the `artist` / `album` field inside the file.** Nothing needs to
reverse the filename transformation, and nothing should try.

## Keys, and what to join on

- **People are keyed by username.** The identity provider's subject id is deliberately not stored: it
  means nothing outside the provider that issued it, and would be reissued by a rebuild anyway.
- **Artists are keyed by name**, because that is how the source system keyed them. Where an identity
  was resolved, `metadata.yaml` also carries a MusicBrainz id — the only identifier here that is
  stable forever, and the right thing to re-key on if names have drifted.
- **Songs are identified by artist, album and title.** Not by file path or track id: both are local
  to the server that held them and neither would resolve on the system reading this.

## What is deliberately absent

- **Credentials.** No access tokens, ever.
- **Email addresses**, and the identity provider's subject ids.
- **Anything a job can rebuild** — similarity graphs, recommendation scores, popularity counts,
  server-local ids, "last seen" timestamps. They churn constantly and would bury what matters.

Because derived data is excluded, this is not a database backup and will not restore as one. It is
the record of what was decided.

## Privacy

These files name real people. Keep them somewhere private.
""";
}
