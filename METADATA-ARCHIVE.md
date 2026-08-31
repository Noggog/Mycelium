# Metadata Archive — owning the data in git

Status: **phases 1, 3 and 4 built** (2026-08-29). Decisions in §7 are settled. Phase 2 (restore) is
deliberately **not** being built — see the note under it.

## Goal

Every fact Mycelium knows that a machine can't re-derive should live in a git repo we control,
in a format a human can read and a fresh install can eat. Plex and Mongo stay the working
stores; git becomes the **record of what we decided**.

The design test, applied to every decision below:

> The Mongo volume is gone. The Plex server is gone. Authentik has been rebuilt from scratch.
> Standing in a new house with the music files and this git repo — what comes back?

Anything that fails that test isn't owned yet.

This extends the locked principle in `PLAN.md:47` — *"external services are refreshable inputs,
not runtime dependencies"* — one step further: **Mongo is a refreshable input too.**

---

## 1. Audit: where the data actually lives today

The request was: artists/albums we have, who likes which artist, who downloaded which artist,
ratings per user, playlists. Those five turn out to sit in four very different places.

| What you asked for | Where it lives now | At risk? |
|---|---|---|
| **Artists/albums we have** | Mongo `artists` (authoritative for reads, mirrors Plex) | 🟢 Straight export |
| **Who likes which artist** | Mongo `userQueue` + `userAlbumRatings`; mirrored to Plex as `<user>_liked` moods | 🟢 Straight export |
| **Who downloaded which artist** | Mongo `purchases.addedBy` — **nullable**, first-claim-wins; plus a permanent `<user>_added` Plex album mood | 🟡 Mongo half is incomplete |
| **Ratings per user — thumbs** | Mongo `userQueue` / `userAlbumRatings` | 🟢 Straight export |
| **Ratings per user — stars (0–5)** | **Plex only**, per-Plex-account. Mongo holds only a `reconsider` summary | 🔴 Not owned at all |
| **Playlists** | **Plex only**, in each user's own account. Mycelium stores zero | 🔴 Not owned at all |

Four findings worth stating plainly, because they change the scope:

**a) `addedBy` is not the full download history.** It's set only when someone presses *Download now*
or pastes a link (`DownloadService.cs:99`, `PurchaseService.cs:169`). Anything auto-downloaded off a
like has `addedBy: null` (`IPurchaseRepo.cs:86`). The richer answer — *who wanted this* — is
recomputed at reconcile time from `userQueue`/`userAlbumRatings` and never stored. There is also no
history collection at all: `purchases` holds only the current row, and reconcile can `Remove` it
(`PurchaseService.cs:426`). streamrip's own history DB is bypassed with `--no-db`.
→ **Exporting `purchases` nightly is what creates the history.** Git accumulates what Mongo discards.

**b) Star ratings cannot currently be exported in a restorable form.** The library-wide track sweep
(`PlexApi.GetMusicTracks`) is deliberately untokenised and drops `userRating` — the doc comment says
so outright (`PlexApi.cs:487`). The read that *does* carry ratings, `GetArtistTracks(key, token)`,
returns `PlexTrack { Title, UserRating }` — no rating key, no album, nothing to re-key against.
→ **Needs a new Plex read** before stars can be archived.

**c) Manual playlists are invisible to the app.** `PlexPlaylistApi` hardcodes `smart=1`
(`PlexPlaylistApi.cs:44`), and smart playlists have no stored membership anyway — they're a live
query. A hand-built playlist with an ordered track list is neither read nor storable today.
→ **Needs new Plex reads** (`/playlists?playlistType=audio` + `/playlists/{key}/items`).

**d) The identity key is fragile.** Every per-user row is keyed on the OIDC `sub` claim
(`BffAuthentication.cs:136`). Rebuild Authentik and every subject is reissued — every rating,
every like, every attribution orphans silently. This is the single biggest rebuild risk in the
system, and it's invisible until the day it matters.

---

## 2. Design decisions

### D1 — Export at the BSON layer, not the domain layer

The domain read models are lossy. `ArtistRating` (`Discovery.cs:194`) carries artist/image/status/
snooze but drops `decidedAt`, `dislikeConfirmed`, `likeConfirmed`, and the `reconsider` block.
`AlbumRating` likewise. Exporting through them would silently discard the sticky "this verdict is
final" flags — exactly the hand-made decisions most worth keeping.

So the exporter reads `BsonDocument`s and applies a per-collection **field allow/deny list**. This
is lossless, adds no methods to thirteen interfaces, and inherits the codebase's existing
defensive-reader philosophy: a field that appears later just shows up in the next snapshot.

### D2 — JSON Lines, one record per line, sorted by key

Not one file per artist. Artist names *are* the primary keys, and they contain `/` (`AC/DC` — the
codebase already works around this at `Program.cs:403`), unicode, and case variants that collide on
case-insensitive filesystems. Any name→filename scheme needs an escaping layer that then has to be
reversed on restore. JSONL sidesteps it entirely.

One record per line, **sorted by `_id`**, means one changed artist is one changed line. That is the
best diff-signal-per-byte available and makes `git log -p` genuinely readable.

Determinism is the whole ballgame — the serializer must produce byte-identical output for identical
data or every night is a whole-file rewrite:

- object keys sorted lexicographically, always
- dates as ISO-8601 UTC to whole seconds (`2026-08-25T06:14:00Z`)
- LF endings, UTF-8 without BOM, trailing newline
- no `null` fields — omit instead
- one space after `:` and `,`, no other whitespace

Worth a dedicated round-trip unit test: serialize the same fixture twice, assert byte equality.

### D3 — Archive what was decided, not what was derived

Snapshotting everything would bury the signal. Split by *who authored the fact*:

**Archive (a human or an irreversible event produced it):**
`users` · `userQueue` · `userAlbumRatings` · `purchases` · `blockedAlbums` ·
`albumMatchOverrides` · the sticky identity pins on `artists`
(`deezerOverride`/`deezerUnlinked`/`musicBrainzOverride`/`musicBrainzUnlinked` + the resolved
Deezer/MusicBrainz identities) · owned albums and their quality.

**Skip (a job can rebuild it, and it churns):**
`relatedArtists` (large, rebuildable from Deezer/ListenBrainz, refreshed on a 30-day clock) ·
`missingAlbums` (delete-and-reinsert per artist every sync — `MissingAlbumRepo.cs:37` — so it would
rewrite most of the file nightly) · `deezerAlbumArtists` (pure memo cache) · `recommendations`
(vestigial) · `appSettings` (a UI toggle).

**Skip within `artists`:** `lastSeenAt`, `present`, `plexRatingKeys`, `albumKeys`, `deezerFans`,
`imageUrl`. The first four are Plex-local, re-captured on every sync by design
(`ArtistCatalogRepo.cs:74`), and mean nothing on a new server; `deezerFans` is a popularity counter
that drifts daily and `imageUrl` is a CDN link the enricher refills for free. All six would churn on
every snapshot. Dropping them is what keeps the history readable.

What we hold is carried by the `albums` list and its per-album quality, not by a flag about the
server that happens to be holding it. One consequence to be aware of: the catalog never deletes an
artist, it only flips `present` to false (`ArtistCatalogRepo.cs:100`), so `inventory.jsonl` includes
artists that have since left the library, carrying whatever album list they had when they did.
If that ever becomes confusing, the fix is to filter absent artists out of the file rather than to
reinstate the flag.

### D4 — Re-key on human-stable identity

The archive is keyed by **username**, not OIDC subject. `users.jsonl` carries the
`subject ↔ username` crosswalk so a restore into the *same* Authentik is exact, and a restore into
a *rebuilt* one is still possible by hand.

Same idea one level down: `inventory.jsonl` carries each artist's MusicBrainz MBID and Deezer id
alongside the name, making it the crosswalk for re-keying taste rows if names ever shift. The MBID
is the only identifier in the system that is stable forever.

### D5 — Never archive a credential

`plexLinks.serverToken` is a live plaintext Plex token (flagged as a known gap at
`IPlexLinkRepo.cs:6`). The archive records the *fact* of a link — username, plex account id, when —
and never the token. Re-linking is a 30-second PIN flow; a leaked token in git history is forever.

Emails are dropped too (see Q5) — username is the restore key, so they'd be dead weight with a
privacy cost. The archive still names people, so **the metadata repo must be private.** Worth
saying out loud in its README.

### D6 — Shell out to `git`, don't embed a library

`StreamripDownloader.RunAt` (`StreamripDownloader.cs:405`) is the established and rather careful
process-invocation pattern here — `ArgumentList` so quoting can't bite, both pipes read
concurrently so a full buffer can't deadlock, `CancellationTokenSource` timeout,
`Kill(entireProcessTree: true)`, a structured result record, never throws. Copy it.

Using the real `git` keeps the archive operable by hand — you can walk into the directory and
`git log`, `git revert`, `git bisect` with no special tooling, which is the entire point of the
exercise. Cost is one line in the Dockerfile.

Two container gotchas to handle up front:
- `git` isn't in the image — add it to the existing `apt-get install` at `Dockerfile:35`.
- A bind-mounted repo owned by a different uid trips git's ownership check. Set
  `safe.directory` (or run `git -c safe.directory=...`) rather than discovering this in prod.
- Commit identity must be explicit (`-c user.name=... -c user.email=...`); there's no global
  gitconfig in the image.

### D7 — Commit only when something changed, with a message that reads

A nightly unconditional commit produces 365 empty commits a year and buries the real ones. Snapshot,
then `git status --porcelain`; empty means skip.

This forces one small discipline: **no generated-at timestamp in any tracked file.** Git already
timestamps the commit. Put schema version and counts in `MANIFEST.json` — values that change only
when the data changes — and nothing else. (This is the classic way self-diffing archives end up
committing every night forever.)

Since the exporter has the before/after in hand, generate a real summary:

```
snapshot 2026-08-25

  inventory  +12 artists, +31 albums
  taste      kelsey +4 liked, +1 disliked; justin +2 liked
  downloads  +3 landed (kelsey 2, justin 1)
  stars      kelsey 18 changed
```

`git log --oneline` then becomes the library's history, which is most of the value.

### D8 — Push is optional and best-effort

Commit locally always; push if a remote is configured. A push failure logs and returns — it must
never fail the snapshot or take the app down. The local repo is already a durable, complete copy;
the remote is redundancy, not the product.

---

## 3. Repo layout

A **separate private repo**, bind-mounted — not this one. Code and data have different lifecycles,
and this repo is public-shaped.

```
mycelium-metadata/
  README.md                 # written by the app on first run; explains the format to a future reader
  MANIFEST.json             # schema version + per-file record counts. NO timestamp.
  users.jsonl               # subject <-> username crosswalk, quality tier, first seen
  inventory.jsonl           # artists + owned albums/quality + MBID/Deezer identity + pins
  downloads.jsonl           # purchases: what was acquired, by whom, when, at what quality
  decisions.jsonl           # album blocks + match overrides
  taste/
    <username>.jsonl        # that user's artist + album verdicts
  stars/
    <username>.jsonl        # that user's Plex star ratings          (phase 3)
  playlists/
    <username>.jsonl        # that user's playlists + membership     (phase 4)
```

Per-user files give good diff locality: one person's evening of swiping touches one file.
Usernames are already sanitized to `[a-z0-9_]` by `ArtistTag.Sanitize` (`IArtistTagger.cs:94`) —
reuse it rather than inventing a second scheme.

Star ratings need a restore key that survives a Plex rebuild. Plex rating keys don't. The honest
key is the **file path** (the files are the library and outlive the server), with
`(artist, album, track title)` carried alongside as a human-readable fallback.

---

## 4. Code shape

Follows the conventions already in place — the namespace decides registration, config records live
outside the scanned namespace, background services delegate their timer to `JitterPolicy`.

```
src/Mycelium.Interfaces/
  IArchiveDump.cs                # Dump(collection) -> JsonObject[]; the D1 read seam
  IGitRepository.cs              # EnsureInitialized / CommitAll -> GitCommitResult

src/Mycelium.MongoDB/Services/Data/
  ArchiveDumpRepo.cs             # the only repo not shaped around a domain type (D1)

src/Mycelium.Backend/
  MetadataArchiveConfig.cs       # root namespace (outside the scan — see LibraryScannerConfig.cs:1)
  Services/Archive/
    CanonicalJson.cs             # D2 deterministic serializer
    ArchiveBuilder.cs            # dumps -> files. Pure function; D3 and D4 live here
    ArchiveDelta.cs              # before/after -> commit message
    GitRepository.cs             # IGitRepository via the `git` CLI (RunAt shape)
    MetadataArchiver.cs          # orchestrates: dump -> build -> write -> prune -> commit
  Services/Background/
    MetadataArchiveService.cs    # BackgroundService -> JitterPolicy.RunDaily

src/Mycelium.Tests/
  CanonicalJsonTests.cs          # determinism
  ArchiveBuilderTests.cs         # what is kept, what is dropped, how it is keyed
  ArchiveDeltaTests.cs           # edited != removed + added
  MetadataArchiverTests.cs       # end to end against a real git repo in a temp dir
  FakeArchiveDump.cs
```

`IArchiveDump` hands back `JsonObject`, not the driver's document type, because
`Mycelium.Interfaces` has no Mongo dependency and shouldn't gain one. Flattening BSON into plain
JSON is the Mongo project's job, which also keeps every step after it testable without a database.

`Services/Archive/` is outside the assembly-scanned namespace (same as `Services/Download/`), so
register by hand in `MainModule` — noting the rule at `MainModule.cs:99`. Config comes from env,
read once in `MainModule` and `RegisterInstance`d, per the house pattern.

**Cadence:** daily at `DAILY_SYNC_HOUR + 2h`, unscattered (it touches nothing third-party). That
lands it after `CatalogSyncService` (`+0`) and `AlbumSyncService` (`+30m`), so it snapshots a freshly
synced catalog rather than racing it. Plus a manual trigger on the dev panel, matching
`/api/dev/catalog/quality-sweep` — `POST /api/dev/archive/snapshot`.

**New env vars:** `METADATA_REPO_PATH` (enables the feature; unset = off — pinned to `/archive` by
the Dockerfile, so a container deployment is always on and compose need only bind a directory
there), `METADATA_REPO_REMOTE`, `METADATA_REPO_BRANCH`, `METADATA_COMMIT_NAME`/`_EMAIL`,
`METADATA_ARCHIVE_HOUR_OFFSET`, `METADATA_GIT_TIMEOUT_MINUTES`.

---

## 5. Harvesting what Plex owns

Stars and playlists live only in Plex, per-account. Rather than have the archiver reach into Plex
directly — which would give it two source systems, two failure modes, and a different cadence per
file — they are **harvested into Mongo first**, and the archiver stays a pure Mongo→git function.

```
Plex ──(weekly, per linked user)──▶ Mongo: userTrackRatings, userPlaylists ──┐
                                                                             ├─(nightly)─▶ git
Mongo: users, artists, userQueue, userAlbumRatings, purchases, … ────────────┘
```

This is the locked principle applied unchanged (`PLAN.md:47`): Plex is a refreshable input, Mongo
is the local source of truth for daily reads, git is the archive. It also means the app *gains*
data it doesn't have today — per-track stars and playlist membership become queryable locally
instead of requiring a live Plex round-trip.

**Cadence: the weekly `ReconsiderPolicy.Interval`**, as decided. Note that the reconsider sweep's
own reads can't be reused — it visits only *thumbed, owned* artists (`GetUnconfirmedVerdicts`) and
`ArtistRatingStatsService` returns aggregates, not per-track ratings. A complete star archive needs
every rated track. That's fine, and cheaper than it sounds: one tokenised paged library sweep per
user (~22s at 82k tracks) replaces one `allLeaves` HTTP call per thumbed artist.

Worth flagging for later, not now: once `userTrackRatings` exists, `ReconsiderSweepService` could
compute its aggregates from Mongo instead of hitting Plex per artist, making it both faster and
offline-tolerant. Out of scope here.

### New Plex reads required

| Need | Today | Change |
|---|---|---|
| Per-track stars, restorable | `GetMusicTracks` is untokenised and drops `userRating` (`PlexApi.cs:487`); `GetArtistTracks` returns only `{Title, UserRating}` | Tokenised paged `type=10` sweep carrying `ratingKey`, `parentRatingKey`, grandparent/parent titles, track title, `userRating`, and the file path |
| All playlists, not just smart | `smart=1` hardcoded (`PlexPlaylistApi.cs:44`) | Drop the filter; list every audio playlist |
| Manual playlist membership | Not read at all | `GET /playlists/{key}/items` for the ordered track list |

Restore keys, since Plex rating keys don't survive a rebuild: stars key on **file path** (the files
outlive the server) with `(artist, album, title)` alongside as the human-readable fallback. Playlist
entries key the same way, keeping explicit ordering.

Smart playlists archive as their **rule expression**, not their contents — that's what they are, and
a materialized track list would both churn nightly and restore wrongly. Manual playlists archive as
the ordered list.

---

## 6. Phases

**Phase 1 — Mongo snapshot + git** ✅ built
Canonical serializer, `ArchiveDumpRepo`, snapshot builder, `GitRepository`, nightly job, dev-panel
trigger, `git` in the Dockerfile, compose bind mount. Delivers inventory, likes, thumbs, blocks,
overrides, and the download list — and starts accumulating download history immediately.

**Phase 2 — Restore** — *not being built, deliberately.* The archive is long-term storage against
Plex dying, not a way to reload this system. When something does replace Plex, the restore gets
written against whatever that turns out to be, rather than being guessed at now for a schema that
may not be the target. This is why the archive is self-describing (`ArchiveReadme`) and why keys are
chosen to mean something outside this app: the reader is a future migration, not this code.

**Phase 3 — Star harvest** ✅ built
Tokenised library sweep, `userTrackRatings` collection + repo, weekly `StarHarvestService` on
`ReconsiderPolicy.Interval`, `stars/<username>.jsonl` in the snapshot.

**Phase 4 — Playlist harvest** ✅ built
Drop `smart=1`, add the items read, `userPlaylists` collection + repo, harvest on the same weekly
pass, `playlists/<username>.jsonl`. Smart playlists as rules, manual as ordered tracks.

**Phase 5 — Polish**
Human-readable digests (a `library.md` sorted by artist, so the repo browses nicely on a phone),
archive status on the dev panel, a restore CLI.

---

## 7. Resolved decisions

- **Q1 — Star sweep cadence** → weekly, on `ReconsiderPolicy.Interval`
  (`RECONSIDER_SWEEP_INTERVAL_DAYS`, default 7). Harvested into Mongo; the nightly snapshot picks up
  whatever the last pass found.
- **Q2 — Playlists in scope** → **all of them**, smart and manual. Manual playlists carry their
  ordered membership; smart playlists carry their rules.
- **Q3 — Repo location** → `/archive` in the container, fixed by the Dockerfile; the deployment
  binds a persistent directory there in `compose.yaml`. `METADATA_REPO_REMOTE` carries the optional
  push target, plus `METADATA_REPO_BRANCH` and `METADATA_COMMIT_NAME`/`_EMAIL`.
- **Q4 — Snapshot trigger** → nightly, at `DAILY_SYNC_HOUR + 2h`. The seam allows
  commit-on-change later if the history proves too coarse.
- **Q5 — Emails** → dropped. Username is the restore key; emails aren't needed and only make the
  repo more sensitive.
