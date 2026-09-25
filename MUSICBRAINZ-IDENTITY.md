# MusicBrainz as the identity authority

> **Status: Phases 0 and 1 built (2026-09-24); Phases 2+ not started.** See §3
> and §4 for what shipped. See
> `PLAN.md` for the product vision, `METADATA-ARCHIVE.md` §9.3 for the earlier
> decision to give owned albums MusicBrainz release-group ids, and
> `QUALITY-TIERS.md` for the upgrade flow this replaces parts of.

## Goal

Make MusicBrainz the authority on **which artists exist** and **what they
released**. Deezer, Plex, and later Navidrome and slskd become *sources*
attached to MusicBrainz identities, not identities themselves.

Why:

- **Deezer artist pages are unreliable.** Deezer artist `291446` ("Vulture")
  mixes about 15 unrelated acts: 120 releases, of which 14 are the German speed
  metal band MusicBrainz knows as `56cda0cf-5d98-4f34-9049-c3e744f65edd`. Deezer
  also renumbers albums: Iron Maiden's catalogue was reissued under new album
  ids, and the old ids now redirect.
- **Deezer is not a permanent home.** A MusicBrainz id is the only identifier
  here that is meant to be stable forever (`METADATA-ARCHIVE.md` D4).
- **Plex is not permanent either.** A move to Navidrome, or offering it as
  another library, is planned, so Plex's names and ids can't be the key.
- **Names make bad keys.** They collide (two bands called Vulture) and they
  change (renames, capitalisation, library re-spellings).

## Decisions

### D1: An artist must have a MusicBrainz id

The artist key everywhere is the **MusicBrainz artist id**. An artist without
one does not exist to Browse, Discover, recommendations or the queue. It shows
up in the reconciliation panel (§6), and gets fixed by resolving it or by
adding it to MusicBrainz.

This is strict on purpose. Being unable to show an artist until it is on
MusicBrainz is an accepted cost.

### D2: An album *prefers* a MusicBrainz id but does not require one

Album identity is relaxed. An album key is, in order of preference:

| Key | When | Example |
|---|---|---|
| `rg:{releaseGroupMbid}` | MusicBrainz has the release group | Vulture – *Sentinels* |
| `deezer:{albumId}` | Deezer has it, MusicBrainz does not | Vulture's pre-release singles, which only Deezer has |
| `title:{artistMbid}:{recordKey}` | Only in the library, with no match anywhere | A bootleg ripped by hand |

`recordKey` is `AlbumTitleMatcher.NormalizeRecord`. When a weaker key later
gains a release group, it is re-keyed (§8.2) onto the `rg:` key.

The artist is always a MusicBrainz id, even for a `deezer:` or `title:` album.

### D3: A release group is the album; Deezer albums are editions

One MusicBrainz release group is one row in the UI. Deezer albums matched to it
(remaster, deluxe, regional reissue) are **editions** listed under that row.
This replaces:

- `MissingAlbum.AlternatePressing`
- `albumMatchOverrides` (the "merge" feature), because ownership becomes "the
  library copy and the Deezer edition have the same release group"
- the idea of a "wrong match, relink" action; you simply pick a different
  edition

A live album is usually its **own release group**, not an edition. Editions are
different pressings of the same record.

### D4: Picking a source happens on the Downloads page

Browse and Discover only say *"I want this album"*, which adds a wanted row with
no source. The Downloads page is where a source is picked: a Deezer edition, a
pasted Deezer URL, slskd, and later others.

- **Exactly one candidate, high confidence:** it is picked automatically, so the
  common case still needs no clicks.
- **Several candidates:** there is **no default edition**. The user picks one,
  either in the edition subpanel on Browse or in the Downloads page.
- **Only low-confidence candidates:** they are never picked automatically.

### D5: Mongo backs the memory cache

MusicBrainz responses live in Mongo with a `fetchedAt` timestamp. The in-memory
cache is filled from Mongo, and each entry's lifetime is set from that
timestamp. A restart therefore never forces a refetch, and staleness is just
"the cache entry expired".

### D6: Automatic artist matches need evidence

A wrong artist match now moves every decision onto the wrong band. A name match
on its own is never enough to link automatically (§4).

## 1. What exists today

Everything album-level and artist-level is keyed by **names**:

| Store | Key today | Notes |
|---|---|---|
| `artists` (catalog) | `_id` = Plex artist name | Holds `deezerId*`, `musicBrainz*`, `albums`, `albumKeys`, `albumQuality`, `albumIdentities` |
| `userQueue` (artist verdicts) | `{userId}:{artist lower}` | `UserQueueRepo.cs` |
| similarity edges | artist names | `RelatedArtistRepo` |
| `missingAlbums` | `"{artist} {album}"` | Carries a non-nullable `deezerAlbumId` (`Album.cs:75`) and is rebuilt per artist every night |
| `userAlbumRatings` | `"{userId}:{artist} {album}"` | Case-sensitive `_id` but a lower-cased read key, so duplicates can creep in |
| `blockedAlbums` | `"{artist}\|{album}[\|scope]"`, lower-cased | Compared at record granularity (`AlbumOverrideKey`), written under both the listing and the credited artist |
| `albumMatchOverrides` | `"{matchArtist}\|{deezerTitle}"` → library title | The "merge" feature |
| `purchases` | `album:{artist} {album}` | `deezerAlbumId` reaches it through a title join in `PurchaseService.Reconcile` |
| `deezerAlbumArtists`, `deezerAlbumTracks` | Deezer album id | Rebuildable memo caches |
| Metadata archive | `Library/<Artist>/<Album>.yaml` | Already writes `musicBrainz.releaseGroup` per album |

What already exists on the MusicBrainz side:

- **API client.** `MusicBrainzApi` has artist search and lookup, plus
  release-group search limited to one artist. There is no paged browse, no
  `inc=` options, no retry, and releases and url-relationships are not used.
- **Artist resolution.** `MusicBrainzArtistResolver` resolves name → MBID (pin,
  unlink, a 30-day cache that is in-memory only) and writes the result onto the
  catalog doc.
- **Owned albums.** `AlbumIdentityResolver` / `AlbumIdentityService` slowly
  resolve owned albums to release groups (`albumIdentities`).
- **Relinking.** `MusicBrainzRelinker` re-checks automatic links, as a dev tool.
- **Rate limiting.** One gate shared by the whole app spaces request *starts*
  1.1 s apart. It does not serialise requests: a slow response can overlap the
  next one.

## 2. Evidence: how well MusicBrainz release groups match Deezer

This is a small sample: Vulture's 7 release groups and 12 of Iron Maiden's.

| Method | Vulture | Iron Maiden | False matches |
|---|---|---|---|
| MusicBrainz Deezer url-rel, following redirects | 7/7 | 6/12 | 0 |
| Release barcode → `api.deezer.com/album/upc:{barcode}` | 7/7 | 7/12 | 0 |
| Normalised title within the Deezer artist's albums | 7/7 | 8/12 | 2 |
| Deezer search | 7/7 | 2/12 | — |

What the sample showed:

- **Barcodes.**
  - Only the barcode of the digital release usually matches, so try every
    release in the group.
  - The barcode must be exact: dropping a leading zero finds nothing.
  - One Deezer album can answer to several barcodes.
- **Redirects.** An old Deezer album id still resolves, but to a *new* id.
  Store the id Deezer returns.
- **Missing listings.** `/artist/{id}/albums` can leave out albums that exist
  (Iron Maiden's *Somewhere in Time*). The barcode lookup and url-rels still
  find them.
- **Dates.** Deezer `release_date` is often a reissue date, so the year can only
  break ties.
- **Title normalisation.** Strip `(Remastered 2015)`, `[... Version]` and curly
  apostrophes. Keep `(Live)`: it separates a different record.
- **False title matches.** The 1984 *Aces High* single matched a 2020 live
  single, and *Speed of Light* has both a studio and a live single.
- **Coverage.** Iron Maiden has 338 release groups on MusicBrainz, 111 of them
  Live and 61 Compilation. Deezer carries about 38. An unfiltered MusicBrainz
  list is mostly noise that can't be downloaded.
- **MusicBrainz gaps.** MusicBrainz is sometimes the *less* complete side:
  Vulture has 7 singles that only Deezer has, which is D2's case.

The first real deliverable (Phase 1) is this table for the **whole library**.

## 3. MusicBrainz access (Phase 0)

> **Built.** Where things live:
>
> | Piece | Code |
> |---|---|
> | Gatekeeper | `MusicBrainzGate` (ListenBrainz project). Priority is ambient: `using var _ = MusicBrainzGate.Background();`. Already applied to `AlbumIdentityResolver.ResolveSome`, `MusicBrainzRelinker.RunAsync` and `QueueReplenishService.ReplenishAll` |
> | Interval | `MUSICBRAINZ_MIN_INTERVAL_MS` (default 1100) → `ListenBrainzEndpointInfo.MusicBrainzMinInterval` |
> | New MusicBrainz calls | `IMusicBrainzApi.BrowseReleaseGroups`, `BrowseReleases`, `LookupUrl`; models in `MusicBrainzRelease.cs` |
> | Deezer additions | `DeezerAlbum.upc/label/available/contributors`, `DeezerTrack.isrc`, `IDeezerApi.GetAlbumByUpc` |
> | Cache | `SourceCache` (Backend singleton) over `ISourceCacheStore` → Mongo `sourceCache` |
>
> Deviations from the text below:
>
> - **The cache loads lazily.** Nothing is read into memory at startup. A read
>   that misses memory loads that one entry from Mongo, so a restart still costs
>   no source calls.
> - **Nothing uses the cache yet.** Its TTL policy (activity-based, with jitter)
>   belongs to the discography service in Phase 2, which is its first user. The
>   top-up sweeper also comes with Phase 2.
> - **Old cache entries are deleted automatically.** A TTL index removes an
>   entry 180 days after it expired.
> - **A browse over 100 pages (10,000 rows) returns null**, rather than a
>   partial list.

### 3.1 Gatekeeper

One queue and one worker for every MusicBrainz call.

- At most one request in flight, at about 1 per second (MusicBrainz asks for 1
  per second on average, per IP).
- Two priorities: **interactive** (someone is waiting on a page) jumps ahead of
  **background** (sweeps, resolution backfills).
- A 503 or `Retry-After` pauses the *whole queue*. Individual requests also
  retry with backoff.
- It replaces the `_nextAllowed` gate in `MusicBrainzApi`.
- `MUSICBRAINZ_BASE_URI` already allows a self-hosted mirror. The gate's
  interval should be configurable so a mirror can run unthrottled.

### 3.2 New calls

- Browse an artist's release groups: `/ws/2/release-group?artist={mbid}`, paged,
  with `secondary-types` and `first-release-date`.
- Browse an artist's releases with barcodes and url-rels, filtered by
  `type`/`status` to official albums and EPs. **Check this against the live
  API.** The filter matters: without it, Iron Maiden's bootlegs cost hundreds
  of calls.
- Reverse-lookup a URL:
  `/ws/2/url?resource=https://www.deezer.com/artist/{id}&inc=artist-rels`. It
  maps a Deezer artist straight to an MBID.
- Always keep the id MusicBrainz *returns*. Merged artists redirect.

### 3.3 Deezer model additions

- `DeezerAlbum.upc`, `available`, `label`, `contributors`
- `DeezerTrack.isrc`
- `GetAlbumByUpc(upc)`
- Store the id Deezer returns, not the id that was requested

### 3.4 Cache

- Mongo documents carry `fetchedAt`. On a memory miss, load from Mongo and set
  the entry's lifetime to `fetchedAt + ttl - now`. Only if Mongo misses too,
  call MusicBrainz. Every fetch writes to both.
- **TTL per artist:** about 7 days if the artist released something in the last
  ~2 years, otherwise 60–90 days, with random jitter so entries don't all
  expire together.
- **Serve stale while refreshing.** An expired entry is still returned while a
  background refresh is queued. An expired read never blocks a page on the
  1-per-second queue.
- **Top-up sweeper.** A low-priority background job refreshes entries that are
  about to expire. The Discover feed reads stored rows for artists nobody opens,
  so without this those rows would never refresh.
- **Explicit invalidation.** These don't wait for the TTL:
  - the artist's MBID changes
  - the nightly Deezer sync finds a Deezer album that matches no release group
    (new releases usually reach Deezer first)
  - the Re-check button (§6)

## 4. Artist resolution (Phase 1)

> **Built.** Where things live:
>
> | Piece | Code |
> |---|---|
> | Evidence and pass | `ArtistIdentityAuditor` |
> | Rules | `ArtistIdentityJudge`, a pure function |
> | Storage | Mongo `artistResolutions`, keyed by library artist name (`IArtistResolutionRepo`) |
> | Schedule | `ArtistIdentityService`: daily, 60 minutes after startup. It checks new artists, and any not checked in 30 days |
> | Endpoints | `/api/dev/identity/{report,pass,reconcile,reconcile/count,recheck,accept}` |
> | UI | `/reconcile` page. The nav badge is visible to dev users only |
>
> Deviations from the text below:
>
> - **No Plex MusicBrainz ids yet.** Evidence 1 is not gathered: the Plex
>   listing drops the `Guid` array, and Plex is on its way out. Owned albums on
>   a candidate's discography do the same job.
> - **Library artists only.** Artists that exist only in user decisions or
>   similarity edges are not checked yet. That is sizing for the Phase 3
>   migration.
> - **The panel only shows artists.** Album rows (owned albums with no release
>   group, Deezer-only albums) need the discography, so they come in Phase 2.
> - **Accept pins the MBID.** It makes the same pin the Sources tab makes, so the
>   ListenBrainz follow-up uses it immediately. Everything else in this phase is
>   read-only.
>
> **Checked per Plex artist, not per name.** Found in the first real run:
> Doldrums covers two Plex artists, a Canadian electronic act and a US space-rock
> band, and the name-keyed catalog merged their eight albums.
>
> - **Plex artist on each album.** Owned albums now carry their Plex artist
>   (`OwnedAlbum.PlexArtistRatingKey`, from the album listing's
>   `parentRatingKey`).
> - **Shared names are split.** A name whose albums sit under two or more Plex
>   artists becomes one library artist per Plex artist (`LibraryArtist`). Each is
>   judged on its own albums and stored as `{name}#plex:{key}`.
> - **Pins on a shared name.** They go in `libraryArtistPins`, because the
>   catalog's name-level pin would pin both acts.
> - **New status `Mixed`.** Candidates hold disjoint shares of one library
>   artist's albums: one Plex artist holds albums by two acts, and it needs
>   splitting in Plex.
> - **Candidates show their evidence:** the titles they matched and how many
>   release groups they have.
>
> **Confidence rules.** "Overlap" means library albums found on the candidate's
> discography, compared by record-level title.
>
> - **High:** an overlap of 2 or more; or the library's only albums are on it; or
>   overlap plus the Deezer link.
> - **Medium:** 1 album out of many. Also a Deezer link alone, when the library
>   owns no albums.
> - **Low:** a name alone. Also a Deezer link whose discography holds none of the
>   owned albums.
> - **Tied overlap:** broken by the Deezer link, otherwise Ambiguous.
> - **Pass rule:** only 5 candidates' discographies are fetched per artist, and
>   if any MusicBrainz call goes unanswered, nothing is stored for that artist.

Evidence, strongest first:

1. **A MusicBrainz id the library already has.** Plex's `Guid` array, which
   `PlexApi` currently strips, or Navidrome's file tags later.
2. **Deezer url-rel reverse lookup.** Exact, for any artist with a known Deezer
   id. This also covers recommendations from Deezer's related artists.
3. **ListenBrainz.** Its similar artists already come with MBIDs.
4. **Name search confirmed by album overlap.** A candidate is accepted only if
   its release groups overlap the albums the library owns (only one Vulture has
   *Sentinels*). With no owned albums, a strict name match with a single
   candidate is accepted, and flagged as low confidence.
5. **Anything else** goes to the reconciliation panel with its candidates.
   Nothing is linked automatically.

User pins still win. Unlinking an artist now means *"this artist is
unresolved"*, so it shows in the panel.

## 5. Discography and matching (Phase 2)

New `artistDiscography` collection, one doc per artist MBID:

```js
{ _id: "<artist mbid>",
  name: "Vulture", deezerArtistId: 291446,
  fetchedAt, expiresAt,
  releaseGroups: [{
    mbid, title, primaryType: "Album", secondaryTypes: [], firstReleaseDate,
    editions: [{ source: "deezer", albumId, title, upc, recordType, releaseDate,
                 available, method: "mb-link"|"upc"|"title"|"search"|"manual",
                 confidence: "high"|"low" }],
    rejected: [/* deezer ids the user marked "not this album" */] }],
  unmatchedDeezer: [{ albumId, title, recordType, releaseDate }] }
```

**Matching order:**

1. MusicBrainz Deezer url-rel, following the redirect
2. Barcode of every release → `album/upc:`
3. Normalised title within the artist's Deezer albums. The year only breaks
   ties, and an ambiguous title means no match.
4. Deezer search

`title` and `search` matches are **low confidence**. They are never picked
automatically, and they only reach Discover if a high-confidence edition exists
too.

**Shared Deezer artist pages** such as Vulture's: Deezer albums that match
nothing go to `unmatchedDeezer`, and are shown collapsed (§7). Grouping them by
label or ISRC prefix, to tell the bands apart, could come later.

**Choosing an edition:** there is no ranking and no default (D4). The edition
list shows enough to choose from: title, track count, release date, whether it
is available in the region, and quality (`DeezerQualityProbe`). Editions not
available in the region are shown but can't be picked.

**Owned albums:** match library titles against the stored release groups
locally, and write `albumIdentities` from them (the archive reads that field).
This **replaces** the one-search-per-album backfill:

- **Why replace it.** `AlbumIdentityService` was never registered as a hosted
  service, so it only ever ran from its dev endpoint. It is deliberately left
  unscheduled.
- **Why it can't be fixed instead.** It searches by exact title, under the
  artist's *current* MBID, and that MBID comes from the name-only resolver and
  can point at the wrong act.
- **Cost.** Once this lands the owned-album ids cost no extra requests: the
  auditor has already cached each resolved artist's release groups.
- **Cleanup.** Delete `AlbumIdentityService`, `AlbumIdentityResolver`, its dev
  endpoint, and `IMusicBrainzApi.SearchReleaseGroup`, unless something else
  still needs that search by then.

**Replacing `missingAlbums`:** it becomes a view derived from
`artistDiscography` plus ownership. It is keyed by album key (D2) and has a
nullable Deezer id.

## 6. Reconciliation panel (Phase 1)

A page with an in-app badge count on the nav. No push notifications for now.

**Rows:**

- Library artists with no MBID
- Ambiguous artist candidates (accept with one click)
- Recommended artists dropped because they have no MBID
- MBIDs that now redirect (MusicBrainz merges): re-key them
- Owned albums with no release group
- Deezer-only albums for artists you care about, as candidates to add to
  MusicBrainz

**Actions:**

- Accept a candidate
- Paste a MusicBrainz URL
- Open MusicBrainz search or the add-artist / add-release page
- **Re-check.** It bypasses every cache (like the relinker's `fresh: true`),
  so edits you just made on MusicBrainz show up immediately

## 7. UI

### Browse (artist page)

- **One row per release group.** Sections come from MusicBrainz types: Albums,
  EPs and Singles open; Live, Compilations and Other collapsed. Release groups
  with a Deezer edition always show.
- **Expanding a row lists its editions.** Owned rows expand too, so a user can
  grab a different edition or a better-quality one.
- **Rows with no editions** read "No download source". The row can still be
  wanted (D4).
- **Collapsed "Other Deezer releases (N)"** holds `unmatchedDeezer`. These are
  `deezer:`-keyed albums and can still be wanted.
- **Plumbing:**
  - React keys become album keys, not titles
  - covers come from the Cover Art Archive when no Deezer edition exists
  - Deezer-specific copy is removed

### Discover

- It only shows release-group albums with at least one high-confidence edition.
- It never shows `deezer:` or `title:` albums.

### Downloads page

- **Wanted rows have states:**
  - *Needs source*
  - *Source chosen*
  - *Downloading*
  - *Done*
  - *Failed*: pick another source
- **In *Needs source*, the row offers:**
  - a list of candidate editions (track count, quality, date, availability)
  - pasting a Deezer URL (reuses `DeezerAlbumLink`)
  - **Open in slskd** (`slskd.noggog.ing`), with "artist album" copied to the
    clipboard, or pre-filled if slskd supports it
- `DownloadService` only processes rows that have a chosen source.
- The manual paste-a-link path (`PurchaseService.AddManual`) becomes just
  another way to choose a source.

## 8. Migration (Phase 3)

### 8.1 Before

- Take a Mongo snapshot and an archive commit.
- The migration must be safe to run more than once.
- A dry-run mode reports what would move, what would go to pending, and any
  collisions.

### 8.2 Re-key operation

One operation used for the migration and for every later id change:
MusicBrainz merges, user corrections, a `deezer:` album gaining a release group.

It moves every reference from key A to key B:

- verdicts, ratings, blocks, purchases
- similarity edges, catalog data
- archive entries

When both keys already have data (a duplicate), it merges them. The newer
decision wins, and any conflict is logged.

### 8.3 Order

1. **Artists.**
   - Resolve every name-keyed artist to an MBID (§4).
   - Re-key `artists`, `userQueue`, similarity edges, and the artist part of
     every album key.
   - Name-keyed data for artists that can't be resolved goes to a **pending**
     store. It is kept, not deleted, and not shown. Resolving the artist later
     replays it through re-key.
2. **Library layer.**
   - The catalog stops being "the Plex artist doc". It becomes library links:
     library id ↔ artist MBID and library album ↔ album key.
   - **The library id is the Plex artist's rating key, not the name.** Doldrums
     proves one name can be two acts, each with its own Plex artist. Phase 1
     already resolves shared names per Plex artist (`LibraryArtist`), so the
     re-key maps `{name}#plex:{key}` resolutions straight across.
   - Name-keyed decisions on a shared name (a like on "Doldrums") are ambiguous
     by nature. Send them to the panel instead of guessing.
   - Plex is the first adapter; Navidrome comes later.
3. **Albums.**
   - Map `(artist, title)` keys to album keys (D2) using the matcher.
   - Pressing-level verdicts (`AlbumRatingKey`) fold into one per release
     group. On a conflict the newest wins.
4. **Merges.** Each `albumMatchOverride` says *Deezer title ≡ library title*.
   Use it as a match hint (seed data for the matcher), then drop the store.
5. **Archive.** Folder names stay human-readable, but files carry the MBID. The
   layout is decided in `METADATA-ARCHIVE.md`.
6. **Stores that don't migrate.** Rebuildable ones (`missingAlbums`,
   `deezerAlbumArtists`, `deezerAlbumTracks`) are dropped and rebuilt.

## 9. Edge cases

| Case | Handling |
|---|---|
| Wrong automatic artist match | Evidence rules (§4). Fixing it is one re-key |
| MusicBrainz merges two artists | Lookup returns the new id → re-key. The panel lists it |
| Same-name bands | No longer collide: the keys are MBIDs, and names are only displayed |
| Collaborations | An album credited to several MBIDs shows under each artist. This replaces `MatchArtist` double-keying and `BlockActsFor` |
| Various Artists / soundtracks | MusicBrainz's Various Artists MBID. Deezer-id collections need release groups, or go to the panel |
| Deezer album renumbered | Store the returned id. The stored edition id updates on refresh |
| Region-locked edition | `available: false` → not picked by default. If it is the only edition, the row shows as having no source |
| Album on Deezer but not MusicBrainz | A `deezer:` key (D2). Re-keyed once MusicBrainz adds it |
| Owned album not on MusicBrainz or Deezer | A `title:` key (D2). It shows in the panel |
| New release on Deezer before MusicBrainz | Unmatched Deezer album → invalidate the artist → it shows as `deezer:` until MusicBrainz catches up, then gets re-keyed |
| Plex ratings | Unchanged. They follow the Plex agent match, not MusicBrainz ids |

## 10. Phases

| # | Phase | Changes stored data? | Deliverable |
|---|---|---|---|
| 0 | MusicBrainz and Deezer plumbing: gatekeeper, new calls, Mongo-backed cache **(done)** | No | — |
| 1 | Artist resolution + reconciliation panel + inventory of every name-keyed store **(done; inventory is §1)** | Adds data, no re-key | **Report:** resolved, ambiguous and missing artists |
| 2 | Discography + matcher (by MBID) | Adds a collection | **Report:** match rates per method across the library, via `POST /api/dev/discography/match?count=N` and `GET /api/dev/discography/report` |
| 3 | Re-key migration (§8) | **Yes** | Stores keyed by MBID; pending store |
| 4 | Wanted list + Downloads page source picking | Yes | D4 |
| 5 | Browse and Discover on release groups with editions | No | §7 |
| 6 | Later: Navidrome adapter; write MBID tags into downloaded files; slskd as a real source | — | — |

Go/no-go points: after Phase 1 (how many library artists would disappear), and
after Phase 2 (whether matching is good enough to drive downloads).

## 11. Open questions

- **Default MusicBrainz filter** for Browse: official Album/EP/Single open,
  Live/Compilation collapsed. Confirm once real artists are visible.
- **slskd:** can a pre-filled search be opened by URL? Check before Phase 4.

Resolved:

- **Auto-picking a source:** yes, but only when there is exactly one
  high-confidence candidate (D4).
- **Default edition:** none. When there are several, the user picks (D4).
- **Deezer-only albums** (`deezer:` keys): never in Discover. They only appear
  in the collapsed "Other Deezer releases" section on the artist page (§7).
