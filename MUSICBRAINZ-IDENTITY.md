# MusicBrainz as the identity authority

> **Status (2026-09-28): Phases 0 and 1 built and deployed, and the library is
> reconciled. Phase 2 is built: every settled artist has a discography, Deezer
> editions are matched, 91% of owned albums have a release group (§5, §13),
> and the panel's album groups are built but not yet deployed (§6.1).**
> Work is on `main`. §12 is the handoff for picking this up. §3 and §4 describe what shipped. See
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

**Every album is attempted, and a failure is work, not a resting state.** Each
owned album and each Deezer album is tried against the artist's release groups
with a looser matcher than artists get (§5). The artist is already settled, so
a near-miss on a title is a small, fixable error, not the wrong band. A
`deezer:` or `title:` key is where a failed attempt lands. When the album is
clearly yours it becomes a reconciliation item (§6.1), and the fix is usually
contributing it to MusicBrainz through Harmony.

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
pasted Deezer URL, slskd, and later others. (The same picker on Browse and
Discover is a follow-up, after Phase 4.)

**Assume matching is loose.** A wanted album may have several possible sources,
none of which matches it exactly. The matcher's job is to put the right
candidate near the top, with its reasons, not to be certain. A person chooses.

- **Picked automatically only when MusicBrainz says so directly:** exactly one
  candidate that a release of the group links to (a url-rel, `MbLink`), whose
  title agrees with the group (not `TitleDisagrees`), and that is available in
  the region. A barcode match is *not* enough: in the first full run, barcodes
  came back as other records (§13).
- **Everything else is a suggestion:** barcode, title, near title, search, and
  every result from a second source. Shown ranked, with the evidence for each;
  never picked unasked.
- **Several direct links:** no default edition either. The user picks.
- **A pick is remembered.** Choosing a source records it on the release group
  as a `manual` edition, which counts as certain from then on: a re-download or
  an upgrade of that album doesn't ask again.
- **A pick can be fed back.** For a Deezer pick with no MusicBrainz link, the
  panel offers to add the link to MusicBrainz (through Harmony, §6.1). Once
  MusicBrainz has it, the album is a direct-link match for everyone, so each
  confirmation makes the next one automatic.

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
> - **Pass rule:** every name-matching candidate's discography is checked, from
>   a name search that asks for up to 25 acts (`MaxCandidates`). An earlier cap
>   of 5 hid the right Iconoclast, which came sixth. If any MusicBrainz call
>   goes unanswered, nothing is stored for that artist.
> - **Releases as well as release groups.** A release group is named after one
>   edition. So an owned album that isn't found among the release groups is
>   looked for among the candidate's releases too ("Firewatch Original
>   Soundtrack" is a release in the group "Firewatch Original Score"). Releases
>   come from `BrowseReleases` and are cached as `musicbrainz:releases:{mbid}`.
>   Phase 2 can reuse that cache: it holds barcodes and url-rels.

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

> **Built: the store, the matcher and the report.** Where things live:
>
> | Piece | Code |
> |---|---|
> | Model and store | `ArtistDiscography` (Interfaces), `ArtistDiscographyRepo` → Mongo `artistDiscography` |
> | Matching rules | `DeezerEditionMatcher`, a pure function |
> | Gathering, pass, report | `ArtistDiscographyBuilder` |
> | Schedule | `DiscographyService`: daily, 90 minutes after startup. It builds artists with no discography and rebuilds expired ones |
> | Endpoints | `POST /api/dev/discography/match?count=N&all=`, `GET /api/dev/discography/report`, `GET /api/dev/discography/artists?limit=N` (built artists in brief, worst matched first), `GET` and `POST /api/dev/discography/artist/{mbid}` (read one; rebuild one now) |
>
> Deviations from the text below:
>
> - **Which artists.** Settled resolutions only: Pinned, or Resolved at High or
>   Medium. Library artists that resolve to one MBID share one discography.
> - **Stored shape.** The doc sits under `data` as nested fields, with
>   `expiresAt` also at the top level as a date. The method is an enum sent and
>   stored by name (`MbLink`, `Upc`, `Title`, `TitleFuzzy`, `Search`).
>   Confidence is derived from the method, not stored: link and barcode are
>   high, everything else low. A person's choice lives on the owned album
>   (`OwnedAlbumMatchMethod.Manual`, §6.1), not on editions yet.
> - **Deezer search is not a separate step.** The Deezer side is the artist's
>   listing plus the albums `SearchArtistAlbums` credits to the same Deezer
>   artist, as the missing-album diff already does. A title match on one of the
>   search-only albums is recorded as `Search`.
> - **No Deezer page for a shared name.** When a library name covers several
>   Plex artists, the name's Deezer page may be the other act's, so it isn't
>   used. Links and barcodes still find editions.
> - **Links and barcodes are checked against titles.** If a Deezer album tied by
>   a link or barcode shares no title word with the group or any of its
>   releases, it is kept but flagged `titleDisagrees`, at low confidence. Found
>   in the first live sample: MusicBrainz links the *Otherness* EP to the box
>   set that holds it, and *Aikea-Guinea*'s barcode came back as *Gentle
>   Creatures*.
> - **Loose titles.** A near match is: every word of the shorter title (two
>   words at least) is in the longer, or 80% of the words are shared. A word
>   that marks a different recording ("live", "remix" and so on) on one side
>   only rules it out.
> - **Caching.** Release groups and releases use the identity check's cache
>   keys, with the activity-based lifetime. A rebuild that is due asks
>   MusicBrainz again. Barcode lookups are cached as `deezer:upc:{barcode}`
>   (90 days found, 30 missing) and followed album links as
>   `deezer:album:{id}`. The Deezer listing is fetched fresh.
> - **Groups credited elsewhere.** The release-group browse finds only groups
>   credited to the artist, so the groups of the artist's own releases are
>   added (*Campfire Songs*: the reissue credits Animal Collective, the group
>   the band's earlier name). An owned album that still fits nothing, a tie
>   included, is looked up by the unmatched Deezer album of the same title:
>   when MusicBrainz links it to releases of exactly one group, and that
>   group's title shares a word with the album's, the album is that group
>   (`OwnedAlbumMatchMethod.DeezerLink`), whoever it is credited to: the duo
>   *Lipphead* for Blockhead, Zola Jesus for Johnny Jewel's remix EP. Such a
>   group is not added to the discography. Lookups are cached as
>   `musicbrainz:url:{deezer url}` (90 days found, 7 missing) and
>   `musicbrainz:release:{mbid}`.
> - **Owned albums are matched in the same build** (`OwnedAlbumMatcher`). A
>   group answers to its own title, its releases' titles and its Deezer editions'
>   titles, at record level. Several groups answering → the one core group, or
>   unmatched. Then near titles. Results go on the discography (`owned`, with
>   the method) and into the catalog's `albumIdentities`. An id from the old
>   backfill is kept (`Earlier`) only if its group is on this discography.
>   `AlbumIdentityService`, `AlbumIdentityResolver`, `AlbumIdentityConfig`,
>   `/api/dev/archive/resolve-album-ids` and `SearchReleaseGroup` are deleted.
>   The report has an `owned` section.
> - **Invalidation isn't wired yet.** A changed MBID, a Deezer album that fits no
>   group in the nightly sync, and a Re-check button don't expire anything yet.
>   `all=true` on the pass rebuilds everything from the cache.

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

**Looser title matching, for albums only.** Before an album is declared
unmatched, these are tried within the one artist's release groups:

- **Record-key equality**, as for artists.
- **Near titles.** Token similarity over record keys with a high threshold,
  to cover punctuation, "&" vs "and", a dropped subtitle, and "Maiden England"
  vs "Maiden England '88".
- **Tie-breaks.** A title matching several release groups is decided by the
  primary type (album vs single) and then the year. Only if it is still tied
  is it left unmatched.

These matches are stored as `method: "title-fuzzy"` at low confidence. They
link the album, but they are listed for review (below).

**Owned albums go through the same matcher.** It fills `albumIdentities` from
the cached release groups (§5, owned albums).

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
- Confident answers that aren't the artist linked today ("Not the artist
  linked today"). The check never changes the link itself, so until a person
  accepts one of the two, the discography build reads one and everything else
  the other. A pin in the Sources tab re-checks the artist, as Accept does, so
  a pin doesn't leave the old verdict behind
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

### 6.1 Albums, and feeding MusicBrainz through Harmony (Phase 2)

> **Built (not yet deployed): the three album groups.** Where things live:
>
> | Piece | Code |
> |---|---|
> | A person's answer | `AlbumIdentity` in the catalog's `albumIdentities`: `manual: true` with an `mbid` (picked, pasted, confirmed) or without one ("leave it"), and `rejected` (groups unlinked from the album). `IArtistCatalogRepo.Get/SetAlbumIdentities` |
> | Ties and tie-breaks | `OwnedAlbumMatcher`: tied groups go on `OwnedAlbumMatch.Candidates`, ranked |
> | Groups and answers | `AlbumReconciliation`; `GET /api/dev/identity/albums`, `POST /api/dev/identity/albums/{link,leave,unlink}?artistMbid=&libraryArtist=&title=` (`link` also takes `releaseGroup`) |
> | Re-check | `POST /api/dev/discography/artist/{mbid}?fresh=true`: the rebuild, asking MusicBrainz again |
> | Page | The "Albums that need a look" section of `Reconcile.tsx` |
>
> Deviations from the text below:
>
> - **Tie-breaks.** Among tied groups, the one core group still wins; among
>   several core groups, the one whose title is the album's at listing level
>   (`Normalize`) settles it (*Sun* over *Sun (sampler)*). Album over EP only
>   ranks the suggestions, it never settles. The Plex album year isn't used:
>   the catalog doesn't store it yet.
> - **One request for the list.** Every artist comes back with its albums
>   (about 1,100 albums), and the page renders an artist's albums when it is
>   opened. No separate per-artist request.
> - **Owned albums only.** Wanted, queued and downloaded albums aren't listed
>   yet; they have no release group attempt until Phase 4.
> - **A pasted group isn't checked** against MusicBrainz, only for the shape of
>   an MBID (the page refuses a release URL). It need not be on the artist's
>   discography: the record may be credited to another artist.
> - **An answer patches the stored discography** as well as the catalog, so the
>   page updates without a rebuild.
> - **Not built:** undoing an answer (clear the entry in `albumIdentities`),
>   the "Contribute to MusicBrainz" group, and the artist page's "Other Deezer
>   releases".

**Album reconciliation.** When an album that is clearly yours fails to link,
it becomes a panel item:

- **Clearly yours** means it is owned, or someone has wanted, queued or
  downloaded it.
- **The guard.** On a shared Deezer page like Vulture's, most unmatched Deezer
  albums belong to *other bands*. Listing them all would flood the panel with
  releases nobody wants imported under this artist. So the rest of
  `unmatchedDeezer` stays on the artist page ("Other Deezer releases"), still
  with a Harmony link, but out of the panel.

The panel gets three album groups:

| Group | What | Actions |
|---|---|---|
| **Pick the release group** | Albums whose title fits several release groups (a tie, §13: ~334 owned) | The tied groups as ranked suggestions (type, year, track count, whether it has a Deezer edition). One click links the album |
| **Albums not on MusicBrainz** | Failed attempts on albums that are clearly yours (~730 owned) | Harmony import, when there is a Deezer album. Otherwise MusicBrainz search and "add release". Paste a release-group URL to link it by hand. "Not on MusicBrainz, leave it". Re-check |
| **Loose album matches** | `TitleFuzzy` links, lower priority (17 owned) | Confirm, or unlink (sends it back to "not on MusicBrainz") |

- **One row per artist, collapsed**, ordered by artist name. Hearts of Space
  alone has 69 and Sinitus Tempo 52; one card per album would bury everything
  else.
- **A person's answer sticks.** A pick, a pasted release group, a confirm and
  "leave it" are stored as manual choices and no rebuild overrides them. Today
  the builder rewrites every owned album's `albumIdentities` entry on each
  build, so this needs a manual marker the builder respects (§12).

[Harmony](https://harmony.pulsewidth.org.uk/) takes a store release (a Deezer
URL or a barcode) and prepares a MusicBrainz import from it. Wherever
Mycelium knows about something MusicBrainz lacks, it can hand that straight
to Harmony. That turns the gaps the identity work finds into contributions,
and every contribution makes the next match automatic.

**The links.** Harmony's lookup is a plain GET form, so the links are simple:

| Harmony action | URL |
|---|---|
| Import a release | `https://harmony.pulsewidth.org.uk/release?url=<Deezer album URL>`, or `?gtin=<barcode>` (Deezer's `upc`); optional `&region=<CC>` |
| Add cover art, ISRCs and store links to a release MusicBrainz already has | `https://harmony.pulsewidth.org.uk/release/actions?release_mbid=<release MBID>` |

**Where the links appear:**

| Situation | Where | Link |
|---|---|---|
| Deezer album with no release group (`unmatchedDeezer`, D2's `deezer:` albums) | Artist page, "Other Deezer releases"; the panel | Import, from the Deezer URL + UPC |
| Artist not on MusicBrainz, but its Deezer page is known | The panel's "Not on MusicBrainz" rows | Import one of its Deezer albums. The MusicBrainz release editor then creates the artist as part of the release, which beats adding a bare artist |
| Release group matched by barcode or title, but MusicBrainz has no Deezer link on it | The panel, low priority | Release actions, to add the Deezer URL. The next match then goes through the url-rel, and the artist gains its Deezer link for §4's evidence |
| Owned album with no release group and no Deezer album | The panel | No Harmony link: there is no store release to import from. MusicBrainz search only |

**In the panel.** "Albums not on MusicBrainz" is this group. A separate,
optional **Contribute to MusicBrainz** group holds the release-actions rows:
releases already matched that only lack their Deezer link. None of that work
blocks Mycelium. After submitting in Harmony, Re-check the row.

**What Mycelium never does.** Mycelium never submits edits itself. Harmony
seeds the MusicBrainz editor, and a person reviews and submits. Mycelium only
builds the link and re-checks afterwards.

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
  - a ranked list of candidates (below), each with its evidence and track
    count, quality, date, availability
  - pasting a Deezer URL (reuses `DeezerAlbumLink`)
  - **Open in slskd** (`slskd.noggog.ing`), with "artist album" copied to the
    clipboard, or pre-filled if slskd supports it
- **Where candidates come from.** Gathered per wanted release group when the
  row needs a source, from every source there is:
  - the stored discography's Deezer editions (§5), with their match method
  - a live Deezer album search by artist and title, for what the artist's
    Deezer page doesn't list (another artist credit, a duplicate Deezer
    artist page)
  - later, an slskd search: folders, with format and file count
- **Ranking is a guide, not a decision.** Candidates are ordered by evidence,
  strongest first: direct MusicBrainz link; barcode of one of the group's
  releases; title at listing level; title at record level with the same year
  and track count; near title; search only. Each shows why it is there
  ("MusicBrainz links it", "barcode of the 2015 CD", "12 tracks, same year").
  Only the rule in D4 picks unasked.
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
| 2 | Discography + matcher (by MBID); every album attempted, failures and loose matches in the panel, Harmony import links (§6.1) | Adds a collection | **Report:** match rates per method across the library, via `POST /api/dev/discography/match?count=N` and `GET /api/dev/discography/report` |
| 3 | Re-key migration (§8) | **Yes** | Stores keyed by MBID; pending store |
| 4 | Wanted list + Downloads page source picking: ranked candidates from every source, auto-pick only on a direct MusicBrainz link, picks remembered as `manual` editions | Yes | D4, §7 |
| 5 | Browse and Discover on release groups with editions | No | §7 |
| 6 | Later: Navidrome adapter; slskd as a real source; the panel's "Contribute to MusicBrainz" group (Harmony release actions, §6.1) | — | — |
| — | **Write MBID tags into downloaded files.** Proposed to move earlier, alongside Phase 4 (see §11) | — | — |

Go/no-go points: after Phase 1 (how many library artists would disappear), and
after Phase 2 (whether matching is good enough to drive downloads).

## 11. Open questions

- **Default MusicBrainz filter** for Browse: official Album/EP/Single open,
  Live/Compilation collapsed. Confirm once real artists are visible.
- **slskd:** can a pre-filled search be opened by URL? Check before Phase 4.
- **Move MBID file tagging earlier (proposed, not yet agreed).** Write the
  MusicBrainz artist and release-group ids into every file Mycelium downloads,
  as Picard and beets do. The files then carry their own identity to any
  player: Navidrome reads the tags, and Plex can use them with local
  metadata. Every album downloaded before this lands is one more file to match
  later, which argues for doing it with Phase 4 rather than in "later".
- **The reconciliation panel is permanent.** It is not a migration tool. After
  the re-key it is the inbox for anything that arrives without an MBID: new
  library artists, recommendations from sources that lack MBIDs, albums that
  fail to link, and MusicBrainz merges and deletions (§8.2.1). Library ids
  (Plex rating keys now, Navidrome ids later) are *addresses* the adapters
  keep, never keys in Mycelium's own data.
- **What counts as "core".** Today it is Album or EP with no secondary type.
  The first full run (§13) showed that this counts bootleg-only release groups
  and 1960s regional editions, and leaves out studio albums MusicBrainz tags
  Soundtrack (*Help!*, *Magical Mystery Tour*). Proposed: core means at least
  one official release, and Soundtrack is allowed. Settle before Browse shows
  it (Phase 5), since the default filter above depends on it.
- **Classical composers.** MusicBrainz credits a composer on every recording of
  their work, so Tchaikovsky's "discography" is 1,751 core release groups of
  other people's performances. Leave them out of match rates, group them by
  performer, or treat them as umbrella artists? Undecided.
- **Artist renames break name keys, today.** On 2026-09-28 Plex's agent renamed
  "Miley Cyrus" to "MILEY". The catalog refresh marked the old name absent,
  and reconcile re-queued six liked albums that were still on disk, which
  downloaded again over the same folders. The re-key (Phase 3) is the real fix.
  Two guards proposed until then, deferred for now: detect a rename in the
  catalog sync (same Plex artist rating key under a new name), and only
  re-queue an InLibrary row once the album has been missing for two refreshes
  and isn't owned under any artist.

Resolved:

- **Hearts of Space** is pinned to MusicBrainz artist
  `bf2b19b7-9c0b-497f-bf4f-e9517c717279`, not treated as an umbrella artist.
- **Auto-picking a source:** only on exactly one direct MusicBrainz link whose
  title agrees and that is available (D4). Barcodes and titles are
  suggestions (decided 2026-09-28, after barcodes returned other records).
- **Default edition:** none. When there are several, the user picks (D4).
- **Deezer-only albums** (`deezer:` keys): never in Discover. They only appear
  in the collapsed "Other Deezer releases" section on the artist page (§7).

## 12. Handoff: finishing Phase 2

For whoever continues this. Read D2 to D4, §5 (the built discography, including
its "Built" block), §6.1 (album reconciliation) and §13 (what the first full
run found) first.

**Where things stand (2026-09-28)**

- **Artists are settled.** `artistResolutions` holds one resolution per library
  artist: per name, or per Plex artist when a name is shared
  (`{name}#plex:{key}`, `LibraryArtist`). An artist's MBID is
  `ArtistResolution.Mbid` when the status is `Pinned`, or `Resolved` at High
  or Medium.
- **Every settled artist has a discography** (`artistDiscography`, 4,110 docs),
  built by `ArtistDiscographyBuilder`, rebuilt daily as each expires by
  `DiscographyService`. Each doc holds the release groups with their Deezer
  editions (`DeezerEditionMatcher`), the unmatched Deezer albums, and `owned`:
  each owned album with its release group or null, and the tied groups when
  there is a tie (`OwnedAlbumMatcher`).
- **Owned albums are 91% matched** (10,656 of 11,726), also written to the
  catalog's `albumIdentities`, which the metadata archive reads. §13 has why
  the rest missed.
- **The panel's album groups are built** (§6.1): pick among tied groups,
  Harmony import or paste for albums not on MusicBrainz, confirm or unlink
  loose matches. A person's answer is a manual `albumIdentities` entry that no
  rebuild overrides.
- **Nothing user-facing reads discographies yet.** Browse, Discover and
  downloads still run on names and the old Deezer links. Phase 2 must keep it
  that way; the switch is Phases 4 and 5.
- **Deployed:** everything up to commit `a347e1b` (owned matching). The album
  groups are not deployed.

**Next**

1. **Deploy, then rebuild everything** (`POST /api/dev/discography/match?all=true`,
   about 85 minutes). Until each artist is rebuilt, its tied albums show as
   "not on MusicBrainz": the tie candidates are only recorded by a build. The
   report's `owned` section gains `manual` and `tied`; expect the listing-level
   tie-break to settle some of the ~334 ties.
2. **Work the panel.** Artists are listed by name; Hearts of Space (69) and
   Sinitus Tempo (52) clear the most with a few "Leave it" clicks.
3. **Optional, cheap:** artist-id verification against MusicBrainz merges
   (§8.2.1), added to the monthly identity check. And the Plex album year as a
   tie-break, which needs the catalog sync to store it.

**After Phase 2:** Phase 3 (the re-key, §8) before Phase 4 (source picking,
D4 and §7), so picks are stored against MBIDs. The Miley Cyrus → MILEY rename
(§11) is the argument.

**Conventions in this codebase that bit us**

- **Enum names on the wire.** Every enum returned to the web app needs its own
  `[JsonConverter(typeof(JsonStringEnumConverter))]`; there is no global
  setting. `ArtistResolutionJsonTests` is the pattern.
- **Build UI mocks from the real C# types**, never hand-written JSON. A
  hand-written mock hid the enum bug above.
- **Every MusicBrainz call goes through `MusicBrainzGate`.** Wrap background
  passes in `using var _ = MusicBrainzGate.Background();`.
- **"No answer" is not "nothing".** A null from a client means the source didn't
  answer. It must never be stored as a miss.
- **Stored records must read back.** `artistDiscography` is System.Text.Json
  under `data`; add new fields as optional constructor parameters so older
  docs still deserialise (`ArtistDiscographyRepo.FromDocument` returns null on
  a shape it can't read, and that artist silently waits for its next rebuild).
- **Plex tests flake.** One `Plex*Tests` failure in roughly eight full runs is
  pre-existing; re-run before chasing it.
- **HTTP clients are tested with `LoopbackHttpServer`** (tests project) against
  the real client.

**Verifying against the live deployment**

- **Address:** `http://192.168.1.232:43105`, plain HTTP on the LAN. The public
  URL `https://mycelium.noggog.ing` sits behind Pangolin's SSO and answers any
  script with a 302 to its login, whatever token it carries.
- **Token:** `Authorization: Bearer <token>`. Dev endpoints need a dev user's
  token, minted on the dev panel (Other page). The owner keeps one at
  `/mnt/bigssd/Repos/LifeWiki/tidal-to-plex/.secrets/myc-dev-token.txt`; ask
  before reading it. Strip the file's trailing newline.
- **Useful calls:**
  - `GET /api/dev/discography/report`: rates, the `owned` section, and the pass
    status
  - `GET /api/dev/discography/artists?limit=N`: every artist in brief, worst
    matched first
  - `GET /api/dev/discography/artist/{mbid}`: one discography;
    `POST` rebuilds it now
  - `POST /api/dev/discography/match?count=N&all=true`: a pass. `all=true`
    rebuilds everything from the cache in about 85 minutes; a first build
    without the cache took 13 hours
  - `GET /api/artists/sources?artist=<name>`: an artist's Deezer and
    MusicBrainz links, to find an MBID by name
- **Auditing.** The §13 analyses downloaded every discography through the two
  endpoints above and classified misses offline. That is cheap (a few minutes,
  no source requests) and is the way to check a matcher change.

## 13. Findings from the first full run (2026-09-28)

Numbers from the first library-wide discography pass (3,893 of 4,110 artists
built when read), before owned albums were matched.

**Cost.** First build about 5 artists a minute, so about 12 hours for the
library, mostly one Deezer lookup per barcode. A rebuild from the cache takes
about 1 second per artist. 11 artists went unanswered; none errored.

**Match rates** (core = Album or EP, no secondary type):

| | Count | Share |
|---|---|---|
| Core release groups | 47,544 | |
| …with a high-confidence edition | 21,996 | 46% |
| …with only title matches | 9,129 | 19% |
| …with no Deezer edition | 16,419 | 35% |
| Deezer albums unmatched | 48,343 | 34% of 143,680 |

By method: MbLink 51,683, Upc 16,371, Title 22,957, TitleFuzzy 3,122, Search
1,204. The title check demoted 1,225 link and barcode matches.

**Wrong high-confidence matches exist.** Before the title check: *Otherness*
(EP) linked by MusicBrainz to the box set "Lullabies To Violaine - Volume 2";
*Aikea-Guinea* and *Hotel Amour* answered by barcode with other records. Links
and barcodes are strong, not infallible.

**Why core groups had no Deezer edition** (16,807 at the time of the analysis):

| Cause | Share |
|---|---|
| No counterpart on the artist's Deezer page | 76% |
| Non-Latin title, no counterpart | 6% |
| Words of one title contained in the other (mostly classical noise) | 8% |
| Same title placed on another group (an EP and an LP of one name) | 5% |
| Half or more words shared (mostly different records) | 2% |
| Same title left unplaced | 1% |
| Differs only in brackets or spacing | 1% |
| No Deezer page, or an empty one | 2% |
| Numbering (Vol./Volume, II/2, Pt./Part) | 0.2% |

- **Deezer really lacks them.** A Deezer album search by artist and title for 40
  random records of the biggest bucket found 0.
- **Concentrated.** Half of the misses come from 100 artists and a third from
  25. Classical composers and orchestras alone are about 4,000 (Tchaikovsky
  1,147, London Symphony Orchestra 832, Debussy 727, Ravel 641).
- **Matcher fixes worth making, about 2% of misses:** fold accents and
  apostrophes when comparing ("Everything's Alright" / "Everythings
  Alright", "Andalucía" / "Andalucia"), normalise numbering, and compare with
  spaces removed as a last step ("GreenSky BlueTree"). Looser containment rules
  are not worth it: nearly all of those pairs are different records.
- **An empty or missing Deezer page** (waterfront dining, Impossible Nothing)
  usually means a wrong or missing Deezer artist link. They belong on the panel.

**Unmatched Deezer albums** are mostly MusicBrainz's gaps, not the matcher's:
in the 20-artist sample, 174 of 222 were singles (new releases, remixes,
live cuts), and two artists MusicBrainz barely knows (Eamonn Watt: 1 release
group against 91 Deezer releases) made up half. These are Harmony import
candidates (§6.1).

**Roughly a third of official records are not on Deezer at all.** That argues
for slskd as a real second source alongside Phase 4, not "later".

**Owned albums** (after the rebuild with owned matching, 2026-09-28 21:43 UTC;
a rebuild of all 4,110 artists from the cache took 85 minutes): 10,656 of
11,726 have a release group (90.9%): 10,639 by title, 17 by near title, 0 kept
from the old backfill. Why the other 1,070 didn't match:

| Cause | Albums | Share |
|---|---|---|
| On the artist's Deezer page, not on MusicBrainz | 511 | 48% |
| A tie between release groups | 334 | 31% |
| Not found on either | 173 | 16% |
| Artist has 3 or fewer release groups on MusicBrainz | 46 | 4% |
| Near-title and spacing cases | 6 | 1% |

- **Ties are the fixable part.** `NormalizeRecord` drops any trailing bracket
  that isn't a different recording, so *Sun* ties with the EP *Sun
  (sampler)* and *Torches* with *Torches (Redux)*, both core. Other ties are an
  album and an EP of one name (*NE-HI*, *Power of the Dragonflame*), or
  MusicBrainz duplicates (*The Beatles* 1968 against *The Beatles (White
  Album)* 2000). Proposed tie-breaks, in order: an exact title at listing
  level (`Normalize`) before record level; the album year from Plex, which the
  catalog doesn't store yet; Album over EP.
- **On Deezer, not on MusicBrainz** (305 artists) are Harmony import candidates:
  Deezer-only singles, meditation and frequency albums (Sinitus Tempo 52, Sat-Chit).
  Some are on MusicBrainz under another artist credit (*Relaxin' With the
  Miles Davis Quintet* belongs to "The Miles Davis Quintet").
- **Hearts of Space** accounts for 69: its radio programmes ("PGM 920 -
  Bhajan") aren't release groups of the pinned artist.

Scripts used (read-only, over the dev endpoints): download every discography
from `GET /api/dev/discography/artists` and `/artist/{mbid}`, then classify
each core group without an edition by its likeliest counterpart among the
artist's unmatched Deezer albums.
