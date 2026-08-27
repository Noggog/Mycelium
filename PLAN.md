# Mycelium — Project Plan

> Living document. Captures the product vision, architecture, and phased build
> order. See `DEVELOPMENT.md` for how the current code is wired.

## Vision

A music-discovery tool that works like a **tree search** over artists:

1. Sync the artists (and their albums) that exist in a user's Plex library.
2. The user thumbs **up/down** library artists they already own. A thumbs-up is a
   *taste anchor* (what used to be a "seed"); thumbs-down means "not my taste".
3. The system recommends related artists, surfaced to thumb on.
4. **Thumbs down** = dead end (prune the branch).
   **Thumbs up** = (a) the artist's related artists join the recommendation pool
   (grow the branch), and (b) the artist is added to a **purchase list** to be
   acquired and added to Plex.

The frontier of "what to recommend next" continuously grows from approvals and
shrinks from rejections — always surfacing fresh artists rooted in the user's taste.

### Ratings replace seeds (2026-06-17)

There is no separate "seed" concept. Everything the user reacts to — owned
artists, recommended artists, and missing albums — is a **rating** (👍/👎). A
👍 on an *owned* artist is exactly what a seed was: a taste anchor the frontier
grows from. The Artists page is just the list of owned artists with thumbs (no
more star toggle), and a dedicated **Ratings** page lets the user review and
adjust every rating after the fact.

### Discovery feed = three toggleable categories (2026-06-17)

The Discover area surfaces three kinds of things to react to. Checkboxes pick
which categories are shown; each is its own paged section:

1. **Recommended artists** — new artists not in the library, grown from the
   user's 👍'd artists along the similarity graph (the original behaviour). 👍 =
   queue to buy + grow the frontier; 👎 = prune.
2. **Missing albums** — albums that exist on Deezer for an artist the user
   **already owns** but that aren't in the library. 👍 = queue the album to buy;
   👎 = not interested. Keeps owned bands current. A missing album drops out of
   the feed (and out of Ratings) automatically once it appears in the library.
3. **Unrated owned artists** — library artists the user hasn't thumbed yet. This
   is the alternative to seed-starring: thumbing owned bands feeds the
   recommendation frontier. Computed as *catalog minus already-rated*.

## Core architectural principle: self-sufficient sections

Each subsystem owns a **local database store as its source of truth for daily
operations**. External services (Plex, Deezer, downloader) are touched only by
**sync jobs**, never on the hot path. The app stays fully usable — browse, seed,
swipe, review the purchase list — even when Plex or Deezer are offline.

External services are *refreshable inputs*, not runtime dependencies.

## Decisions (locked)

- **Similarity source: Deezer** (keyless `/search/artist` → `/artist/{id}/related`,
  also backfills artist images). Replaces the deprecated Spotify recommendations
  API (403s since 2024-11-27). Lives behind the existing `IRecommendationProvider`.
- **Identity: Authentik (OIDC).** Core login does *not* depend on Plex. Light
  multi-user for trusted friends — functional, not paranoid.
- **Plex is a single shared server — THE library**, host-configured (as today via
  env: `plexEndpoint` / `PLEX_TOKEN` / `preferredPlexLibrary`). The Library Catalog
  is one global store, not per-user. Authentik still scopes per-user *taste* state
  (seeds/decisions/purchase); per-user Plex linking, if ever added, is for other
  purposes, not the catalog.
- **Recommendations are precomputed into the DB**, not computed on page visit. A
  per-user recommendation queue is materialized and kept fresh **incrementally on
  each decision** (reactive tree-search) **plus a periodic replenisher** (background
  reconcile). Site visits are always an instant DB read of "the next card."
- **Review UX: one-at-a-time swipe**, showing *why* an artist was recommended
  (which seeds/approved artists point to it).
- **Purchase list is its own store.** It tracks what to grab with a status field.
  The actual downloader integration is a future pluggable sync job behind an
  interface — target (e.g. Lidarr) decided later.
- **Album title matching has two granularities, and ownership uses the looser one**
  (`AlbumTitleMatcher`, settled 2026-08-24 after trying it the other way for a day):
  - `Normalize` — the **listing** key. Typography folded, a bare trailing `EP`/`LP`
    dropped, edition decoration *kept*. `Both Sides (Deluxe Edition)` and
    `Both Sides (2015 Remaster)` are two keys. Used to dedup a Deezer catalog walk so
    each pressing keeps a discography row of its own with its own Deezer id.
  - `NormalizeRecord` — the **record** key. Also strips `(Deluxe Edition)`,
    `[10th Anniversary]`, `- Remastered`. **Everything that asks "do we have this?"
    uses this one**: the missing-album diff, the purchase reconcile, the Plex deep
    link, the upgrade swap, and the merge/block key (`AlbumOverrideKey`).

  Ownership *must* be record-level because **Plex renames what it imports** — it
  matches an album against its own metadata and drops the edition decoration (or
  folds the extra tracks into the album it already had). We buy
  `Watch The Throne (Deluxe)`; it lands on disk as `Watch the Throne`. Asked at
  listing granularity, an album we own reads as "not available" for ever and the
  purchase row can never see its own download arrive.

  The two granularities must not be mixed within one question: the diff and the
  reconcile share a key, or a queued row never closes out.

  Pressings are still told apart where it matters — every one gets its own
  discography row and Deezer id — but only the first is pushed at anyone
  (`MissingAlbum.AlternatePressing`), so one record doesn't ask the same question
  twice. Blocks are record-scoped: saying no to an album is saying no to the album,
  not to one spelling of it.

## Sections

Each is independently buildable and testable.

### 1. Library Catalog
- **Store:** `artists` — name, image, lastSeenAt, **owned `albums`** (album
  titles pulled from Plex). **Global / shared** — one Plex server, THE library.
- **Sync job:** `CatalogRefresher` ("Refresh from Plex") — upserts artists *and*
  their owned albums on startup / daily; flags artists no longer present.
- **Daily reads** (`GET /artists`, the missing-album diff) hit this store, not Plex.
- _Built:_ DB-backed `LibraryProvider` over `ArtistCatalogRepo`; the album column
  and `PlexApi.GetAlbums`/`PlexRepo.QueryAllAlbums` are the 2026-06-17 addition.

### 1b. Missing Albums (global)
- **Store:** `missingAlbums` — one doc per (owned artist, album-on-Deezer-not-owned),
  with album art. **Global / shared** (a fact about the library, not a user).
- **Sync job:** `MissingAlbumRefresher` / `AlbumSyncService` — for each owned
  artist, resolve its Deezer id, pull its discography (`record_type == "album"`),
  diff against the owned album titles, and `ReplaceForArtist` the misses. Albums
  that have since been acquired drop out on the next run.
- Heavy (one Deezer discography call per owned artist), so it is its own daily job
  separate from the cheap Plex catalog refresh.

### 2. Similarity Graph (recommendation ingestion)
- **Store:** `relatedArtists` — edges `artist → [related]`, tagged with source
  (`deezer`), fetchedAt, and related-artist images. **Global / shared** across users.
- **Sync job:** Deezer provider; results **persisted** so the graph survives
  Deezer downtime and we never re-fetch the same artist needlessly.
- _Current code:_ `SpotifyProvider` (dead). Add `DeezerProvider` behind
  `IRecommendationProvider`; register in `MainModule`.

### 3. User Taste State (per user)
- **Stores (scoped by Authentik user id):**
  - `userQueue` — per (user, artist) ratings *and* the precomputed recommendation
    queue. Status Pending (recommended, awaiting a swipe) / Liked / Disliked. A
    Liked artist is a taste anchor (the old "seed"); score/sources/depth rank the
    pending recommended ones.
  - `userAlbumRatings` — per (user, artist, album) verdict on a missing album.
- **No `seeds` store** — removed 2026-06-17. The frontier = the user's Liked
  artists (owned or recommended-then-liked). Bootstrapping: a brand-new user
  thumbs owned artists (feed category 3), which seeds the frontier.
- _Built:_ `IUserQueueRepo`/`UserQueueRepo`; `IUserAlbumRatingRepo` is the
  2026-06-17 addition. `IUserSeedRepo` deleted.

### 4. Tree-Search Engine
- **Store:** per-user `recommendationQueue` — the materialized, ranked list of
  pending cards. The swipe UI only ever reads from here (instant, offline-from-sources).
- Queue computation (per user):
  - frontier = the user's **Liked artists** (owned taste anchors + approved recs)
  - expand via the stored similarity graph
  - exclude already-in-library, rejected (dead ends), already-decided
  - rank by how many frontier artists point to a candidate (more = stronger)
- **Kept fresh two ways:**
  - **Incremental, on each decision** (reactive): thumbs-up → mark approved → if the
    artist's edges aren't in the graph, enqueue a Deezer fetch → splice its related
    artists into the queue with updated ranking. Thumbs-down → mark rejected, drop
    from queue. The next card already reflects the last swipe.
  - **Periodic replenisher** (background job): fetch missing Deezer edges, recompute
    rankings, top up and prune the queue. Reconciles anything the incremental step
    deferred (e.g. Deezer offline during a swipe).
- First-ever seeding shows a brief "building recommendations" state, then the queue
  stays pre-warmed.

### 5. Acquisition / Purchase List (global) — _Built 2026-06-17_
- **Store:** `purchases` — one doc per item (artist or missing album), status
  `pending → sent → in-library`. **Global / unified across users** (the maintainer's
  queue), keyed by `PurchaseKey` (`artist:{name}` / `album:{artist} {album}`).
  `IPurchaseRepo`/`PurchaseRepo`; display fields refresh on upsert, status/requestedAt
  are insert-only so a reconcile never demotes an ordered row.
- **`PurchaseService`** (Backend singleton) owns the lifecycle. `Reconcile()` is the one
  sync point: folds the current liked-but-unowned set in (insert as pending / dedup
  across users), flips arrivals to `in-library`, and prunes pending rows nobody wants
  any more (ordered rows are kept — in flight). Runs on each read of the list and after
  each catalog/album sync, so the loop closes without a page visit.
- **Downloader (built 2026-06-17):** `IDownloader` is the pluggable seam.
  `StreamripDownloader` shells out to **streamrip** (Deezer ARL, configured in streamrip
  itself via `rip config` — credential never enters this app) to grab **albums only** (artists
  stay as wishlist reminders). Invocation: `Process` with `FileName=STREAMRIP_BIN` (default
  `rip`, resolved via the backend process's PATH, or an absolute path) →
  `rip --folder DIR --quality Q [--codec C] url https://www.deezer.com/album/{id}`.
  **Quality defaults to FLAC (`DEEZER_QUALITY=2`); on a failed pass it retries once at
  `DEEZER_FALLBACK_QUALITY` (default `1` = 320 MP3)** so an album not available lossless still
  comes down.
  - **`DownloadService`** is a single-flight channel consumer: ids reach the queue either
    automatically (a background loop, on while the drainer switch is on, enqueues pending albums
    every `DOWNLOAD_BATCH_INTERVAL_MINUTES`) or manually via
    `RequestDownload(id)` (the "Download now"/"Retry" button — non-blocking, returns
    immediately). Each item: Pending → Downloading → Sent/Failed, throttled by
    `DOWNLOAD_ITEM_DELAY_SECONDS`. Registered as a **shared singleton hosted service** so the
    endpoint and the loop are one instance. Crash recovery: stranded `Downloading` rows reset
    to Pending on startup. A **settle pass** closes the loop for a fresh download: while a row
    downloaded within `DOWNLOAD_SETTLE_WINDOW_HOURS` is still waiting, it re-pulls the Plex
    catalog every `DOWNLOAD_SETTLE_INTERVAL_MINUTES` (file in Plex → reconcile → `in-library`)
    instead of leaving it to the daily catalog sync. That only closes a row early if Plex has
    *already* filed the album (a `PLEX_RESCAN_AFTER_DOWNLOAD` rescan, or a manual library refresh);
    left to Plex's own nightly pass, the album lands at the daily anchor below instead. That loop is
    free-running — its phase has nothing to do with when a batch finished, so a tick can fire moments
    before the rescan is even requested and then idle a full interval — which is tolerable on the
    normal pace and not in **fast mode**, where the point is that the page keeps up. So a drain in
    fast mode also fires a **settle burst**: a pass every `DOWNLOAD_FAST_SETTLE_INTERVAL_SECONDS`
    (10) for `DOWNLOAD_FAST_SETTLE_WINDOW_MINUTES` (2), which costs nothing once the albums have
    landed (a settle pass with nothing waiting is one Mongo read) and closes the row while the user
    is still looking at it. The rescan request itself is made **before** the between-albums wait, not
    after: that wait paces *Deezer* and is jittered to hide the cadence from it, and neither concerns
    the user's own Plex — behind it, a drained batch sat on a jittered 42–78 s before it could even
    ask.
  - **The automatic/manual switch** lives on the Download page and is **persisted in Mongo**
    (`appSettings`, via `IAppSettingsRepo`), so it survives a redeploy and is re-read on every
    drainer tick — flipping it needs no restart. It is **deliberately not an env var** (automatic
    until turned off), so nothing can contradict what the UI shows; manual downloads work
    regardless. streamrip is always the backend (no NoOp). Env knobs:
    `MUSIC_DOWNLOAD_DIR`, `STREAMRIP_BIN`, `DEEZER_QUALITY` (2), `DEEZER_FALLBACK_QUALITY` (1),
    `DEEZER_CODEC`, `DOWNLOAD_BATCH_SIZE` (3), `DOWNLOAD_ITEM_DELAY_SECONDS` (60),
    `DOWNLOAD_BATCH_INTERVAL_MINUTES` (30), `DEEZER_DOWNLOAD_TIMEOUT_MINUTES` (15),
    `DOWNLOAD_SETTLE_INTERVAL_MINUTES` (15), `DOWNLOAD_SETTLE_WINDOW_HOURS` (6),
    `DOWNLOAD_FAST_SETTLE_INTERVAL_SECONDS` (10), `DOWNLOAD_FAST_SETTLE_WINDOW_MINUTES` (2).
  - **Timer jitter (`JitterPolicy`, app-wide):** recurring waits on the **third-party** paths —
    between albums, between batches, and the daily missing-album / queue-replenish passes — are
    scattered by ±`TIMER_JITTER_PERCENT` (30, clamped 0–90) instead of firing on an exact cadence,
    since a perfectly periodic fetch pattern is a machine signature and Deezer/MusicBrainz have
    reason to look for one. Loops that only touch the **user's own Plex server** and Mongo — the
    daily catalog sync and the download settle pass — pass `scatter: false` and run on the dot:
    nothing there is hunting bots, so blurring the schedule only makes it vaguer (changed
    2026-08-07). `JitterPolicy` also owns `RunPeriodic`/`RunDaily`, the loop shapes that replaced
    those services' fixed `Observable.Timer`.
  - **Daily anchor (`DailySyncSchedule`, changed 2026-08-07):** the catalog and missing-album syncs
    run at a **wall-clock hour** (`DAILY_SYNC_HOUR`, default 6am server-local — set `TZ` on the
    container; album sync 30 min behind so it reads a fresh catalog) rather than 24h after boot.
    Plex only files newly-arrived music into the library on its *own* nightly pass, so a catalog read
    left to drift could land minutes ahead of it and report a finished download nearly two days late.
    Each still runs once at startup, so a deploy never serves a stale catalog. The wait is recomputed
    from the local clock each cycle, so DST self-corrects. Where the anchor *is* scattered (the Deezer
    album sync) it slips **forwards only**, because a pass that woke early would find its own target
    still ahead of it and run twice.
  - **`DownloadSchedule`** publishes when the drainer next acts (next album / next batch) for the
    monitor's countdown. Its own singleton because the snapshot is built by `PurchaseService`,
    which `DownloadService` depends on — reading it back the other way would be a cycle.
  - **Error handling:** every streamrip attempt logs its command up front; a pass that exceeds
    `DEEZER_DOWNLOAD_TIMEOUT_MINUTES` is killed (process tree) and the item marked `failed`
    rather than hanging in `downloading` forever; timeouts/non-zero exits log streamrip's
    captured stdout+stderr. (Empty ARL in streamrip's config makes it fall back to
    the unreliable deezloader, which hangs — the timeout is what rescues that case.)
  - **Verification (why exit code isn't enough):** streamrip gathers an album's tracks with
    `return_exceptions=True` and only *logs* per-track failures, so a pass where every track was
    unavailable still exits 0. Released streamrip (pinned 2.1.0) also has no per-track quality
    downgrade, so a FLAC request against an MP3-only album produces a folder holding just cover
    art — and the old exit-code check marked that `sent`. Instead each quality pass writes into
    its own staging tree under `{MUSIC_DOWNLOAD_DIR}/.mycelium-incoming/{albumId}` and the
    result is checked against Deezer's own track count (`IDeezerApi.GetAlbumTracks`, paged —
    Deezer caps a page at 25).
  - **Per-track fallback:** a pass that comes up short is re-run down the quality ladder — derived
    from `DEEZER_QUALITY` as every step below it (2 → 1 → 0), so it can't be left half-written by a
    stale `DEEZER_FALLBACK_QUALITY` — each into its own staging tree, and the
    results are merged *per track* — every pass names files from the same `track_format` and they
    differ only in extension, so a track present at FLAC keeps its FLAC and only the genuinely
    missing ones drop to MP3. A **chain** rather than one step because Deezer's formats vary per
    track: when a track has no master in the requested format the media API returns no URL and
    streamrip 2.1.0 falls back to `e-cdns-proxy-*.dzcdn.net`, a CDN Deezer retired (NXDOMAIN), so
    that track is lost unless something retries it lower. Observed case: no FLAC at all, a 320
    master for one of four tracks, 128 for the other three. Only the merged tree is promoted
    into the library, so a half-finished grab never reaches Plex. A short-but-nonempty result is
    promoted anyway (a geo-blocked track is unfixable by quality) and logged as PARTIAL. A
    timeout discards its staging tree rather than promoting it — streamrip writes each track
    straight to its final name with no partial marker, so a truncated track is indistinguishable
    from a complete one — and does not trigger the fallback pass (systemic failure; retrying
    would only burn another timeout).
  - _Why not Lidarr / a Deezer playlist:_ Deezer closed new API app registration, so the
    official playlist-write (OAuth) is unavailable; the ARL drives the unofficial API that
    streamrip uses. Lidarr's Deezer plugins exist but are flagged ban-risky and add a
    moving part; a direct, throttled, server-controlled grab was preferred.
- **Endpoints:** `GET /purchases` (active = pending/downloading/sent/failed),
  `GET /purchases/status` (live monitor snapshot), `POST /purchases/download` (manual "download
  now"/retry — non-blocking), `POST /purchases/unsend` (all by `?id=`). Frontend Purchases.tsx
  splits Downloading-now / Failed / Albums-queued / Ordered / Artists-wishlist with Download now
  / Undo / Retry actions. `PurchaseItem` carries `DeezerAlbumId` (from the missing-album set) so
  the downloader resolves the album URL without DB joins at grab time.
  - _No manual "remove" action_ (removed 2026-06-17): the list is reconciled from likes, so a
    removed-but-still-liked item just reappeared. To drop something, un-rate it; a more
    intentional dismissal (e.g. clear-the-like, or a suppressed flag) is a future addition.
    `IPurchaseRepo.Remove` stays for internal reconcile pruning of unwanted pending/failed rows.
- **Live monitor (built 2026-06-17):** a `Downloading` status is set the instant the drainer
  hands an item to streamrip (single-flight), so the page shows "Downloading: X" in real time;
  it flips to `sent`/`failed` on completion. Stranded `Downloading` rows (crash mid-fetch) are
  reset to pending on drainer startup. `GET /purchases/status` returns a `DownloadSnapshot`
  (backend, automatic on/off, throttle, counts by stage, current item); the To Buy page renders
  a monitor panel and polls it (3s) plus the list (5s) so it updates without a reload. Deeper
  activity (per-item lines, streamrip stderr) is in the backend log (`logs/backend-<date>.log`).

### 6. Web UI (React + Vite)
- **Artists** — owned-artist list with 👍/👎 per row (replaces the seed star).
- **Discover** — three category-checkbox sections (recommended artists, missing
  albums, unrated owned artists); list ⇄ swipe; "why recommended".
- **Ratings** — review/adjust every rating (artists + albums); albums that now
  exist are hidden (no longer interesting). Re-thumb or clear back to the feed.
- **To Buy** — purchase list: non-owned Liked artists + still-missing Liked albums.
- _Built:_ Home, Artists, Discover, To Buy, dev Related view.

## Phased build order

Each phase is shippable on its own.

1. **Catalog + Plex refresh job** — convert `/artists` to DB-backed; add the sync.
   The foundation everything reads from.
2. **Deezer provider + similarity graph store** — replace dead Spotify; persist edges.
3. **Authentik OIDC login + per-user seeds** — identity, then mark library artists as liked.
4. **Tree-search engine + swipe UI** — the core discovery loop.
5. **Purchase list store + status tracking** _(built 2026-06-17)_ — persisted `purchases`
   store, `pending → sent → in-library` lifecycle reconciled from likes + library state,
   downloader behind a stubbed `IDownloader` interface.
6. **Richer discovery feed (2026-06-17, in progress)** — seeds→ratings unification;
   three toggleable feed categories (recommended artists, missing albums, unrated
   owned artists); the album sync pipeline (Plex albums + Deezer discography diff →
   `missingAlbums`); and a Ratings review page. Artists page gets 👍/👎.

## Phase 7 — Discover/Acquire follow-ups (planned 2026-06-17)

Worked one at a time. Full design in `~/.claude/plans/dreamy-forging-hearth.md`.

1. **New-artist → albums, surfaced inline.** _(built 2026-06-18)_ Liking a non-owned recommended artist enumerates
   their Deezer discography (albums only) on-demand and renders them as ratable missing-album
   rows **inline under the just-rated card**. Thumbed-up albums flow through the existing
   missing-album → purchase → download path. The enumerated albums are written to the global
   `missingAlbums` store so `PurchaseService.Reconcile` can attach their `DeezerAlbumId`
   (otherwise un-downloadable). Closes the one real hole in discover→acquire. _New endpoint
   `GET /discovery/artist-albums`; shared `MissingAlbumRefresher.RefreshOne`._
2. **Post-download-batch Plex rescan.** _(built 2026-06-18)_ After a download batch drains, trigger a
   targeted Plex library scan (`PlexApi.RefreshLibrary` → `GET /library/sections/{key}/refresh`, behind
   the new `ILibraryScanner` seam; `PlexLibraryScanner` is its only impl) so new albums are picked up
   promptly. `DownloadService` calls `RequestScan()` after each successful fetch; the scanner applies a
   **trailing debounce via Rx** (`Subject` → `Throttle(selector)` → `Concat`, `PLEX_RESCAN_DEBOUNCE_MINUTES`,
   default 5) so a draining batch folds into one scan once activity quiets, and is a **no-op unless
   `PLEX_RESCAN_AFTER_DOWNLOAD`** is on (default off). The window is chosen per request rather than fixed
   (hence the duration-selector `Throttle`): a **fast-mode** burst passes `fast: true` and settles on
   `PLEX_RESCAN_FAST_DEBOUNCE_SECONDS` (default 30, never longer than the normal window), so the panel's
   in-library flip keeps up with a burst instead of trailing it by five minutes — paired with the
   fast-mode settle burst above, since a shortened debounce alone still left the flip behind the
   15-minute settle timer. The debounce clock is
   scheduler-injected so tests drive it deterministically with a `TestScheduler` (no wall-clock waits).
   Library resolution moved from `PlexRepo` to a shared `PlexApi.ResolveLibrary()` so reads and the
   rescan target the same section. Best-effort: scan failures are logged, never thrown. (The `InLibrary`
   flip still depends on the deferred title-match correctness fix below.)
3. **Snooze (Week / Month / Year).** _(built 2026-06-18)_ Third action beside 👍/👎: hides a
   recommendation for the chosen duration, auto-resurfaces it on expiry (lazy-on-read), and is
   excluded from queue rebuilds meanwhile. Added `DiscoveryStatus.Snoozed` + `snoozeUntil` to
   `userQueue`; the `Pending`/decided filters became expiry-aware (`UserQueueRepo.EligiblePending`
   OR-filter is the single source of truth for resurfacing; `GetDecidedArtists` counts a snooze as
   decided only while unexpired). `DiscoveryEngine.SnoozeArtist` never expands. `POST
   /discovery/snooze?artist=&album?=&albumArt?=&duration=week|month|year`. Albums snooze the same way
   (`UserAlbumRatingRepo.Snooze`; `GetDecidedKeys` drops expired snoozes so the album resurfaces;
   `DiscoveryEngine.SnoozeAlbum`). Frontend: on every Discover card (artists + missing albums) the
   three snooze durations show inline as direct buttons (no popover); "Snoozed until X" + un-snooze
   (✕) on Ratings. **Every feed decision is undoable inline** — like / dislike / snooze, on artists
   and albums, render a `DecisionMark` with an `undo` that clears the verdict (reuses DELETE
   `/discovery/rate`); undoing a recommended artist also clears the album decisions made in its inline
   panel and collapses it (client-side, from the react-query cache, same session).
4. **Periodic replenisher.** _(built 2026-06-18)_ Background `QueueReplenishService` (Rx
   `Observable.Timer`, mirrors `AlbumSyncService`) that per-user tops up the recommendation queue via
   a gentle additive `DiscoveryEngine.TopUp` (no `DeletePending`), which also refetches edges stale
   past `RelatedStalenessPolicy`. Per-user try/catch so one failure doesn't abort the pass. Users
   sourced from `IUserQueueRepo.GetAllUserIds`; seam is `IQueueReplenisher` (impl by `DiscoveryEngine`)
   for testability. `ReplenishConfig` (`QUEUE_REPLENISH_INTERVAL_HOURS`, default 24; +5min startup
   offset). Forwarded in AppHost. Shares the decided-set filter with #3.

_Deferred:_ title-normalize / `(Deluxe)`-tolerant correctness fix in `PurchaseService.AlbumIsOwned`
— revisit if lingering rows become a problem.

## Collections — records no artist can reach (built 2026-08-25)

Everything else in this app is found **through an artist**: the catalog lists owned acts, the
similarity graph grows from the ones a user likes, and the missing-album diff walks each owned
artist's Deezer discography. A various-artists compilation is credited to an *umbrella* rather than
to an act, and those discographies are empty — Deezer's own "Various Artists" (id 5080) answers
`/artist/5080/albums` with nothing at all. So no walk that starts from an artist will ever produce
one. `https://www.deezer.com/album/246803` ("The Breakfast Club", Various Artists) was the case that
prompted this.

- **What counts.** `UmbrellaArtist` (Interfaces/Artist.cs) — a **superset** of `PlaceholderArtist`,
  kept separate on purpose. The strict three-name list gates the recommendation feed and the
  similarity graph, where a false positive erases a real band; this wider set answers "is there an
  artist here that could carry a verdict?" and adds the soundtrack/score credits plus a pattern for
  cast recordings (Deezer appends the show: "Original Broadway Cast of Hamilton"). Bare one-word
  candidates stay out — "Cast" is a britpop band, "Various" and "VA" are real acts.
- **Finding.** `CollectionService` + `GET /api/collections/search` (Deezer `/search/album`,
  umbrella-credited hits first, singles dropped), `POST /api/collections/resolve` for a pasted album
  link, and `GET /api/collections` listing what the user owns or has judged.

  **No separate view.** Browse keeps the layout it had; collections just join it. The owned ones are
  merged into the library list itself, sorted by title among the artists — a compilation on the shelf
  is library, so it is listed as library, and that is also the only way to thumb one you already own.
  An "Albums & collections" block sits under the existing "Not in your library" artist results and
  carries everything else Deezer knows; owned umbrella records are filtered out of it, since the list
  above is already showing them (the same rule `UncatalogedResults` applies to artists). A pasted
  Deezer album link typed into the same search box is resolved instead of searched, so the escape
  hatch for a record search won't surface costs no extra UI.
- **Rating.** `POST /api/collections/rate` writes an additive row to `missingAlbums`
  (`IMissingAlbumRepo.Upsert` — *not* `ReplaceForArtist`, since every collection files under the same
  umbrella act and a replace would delete its neighbours' Deezer ids) and then an ordinary album
  rating. Acquisition needs no new pipeline: `PurchaseService.Reconcile` already folds liked albums
  into the buy list and reads the Deezer id out of that store.
- **Tagging goes on the album.** A verdict normally lands on the artist, but tagging "Various Artists"
  as liked would claim the user likes every compilation in the library. So an umbrella-credited
  record carries `<user>_liked` on the **album** — `IAlbumTagger`/`PlexAlbumTagger`, Plex metadata
  type 9, `SetAlbumMoods`. Only umbrella credits: an ordinary album's verdict is already carried by
  its artist, and stamping the record too would put single albums by disliked acts into "My Library".
  `AlbumTagBackfill` re-stamps once a download lands (there is no arrival signal at album granularity
  — a compilation arriving rarely makes its umbrella act *newly* present — so it re-checks the small
  rated set against the catalog, on the same hooks as `ArtistTagBackfill`).
- **Smart playlists.** "My Library" is now the union of the two moods —
  `artist.mood is <id@type8> OR album.mood is <id@type9>` — so a liked collection reaches the playlist
  that is supposed to be everything you like. Plex keys tag vocabularies per metadata type, so the
  same tag *name* has two different ids and both are looked up. With only one tag present the filter
  stays a single condition (Plex's editor flattens redundant brackets on save, and a playlist whose
  stored rules didn't match its definition would read as "differs" the moment the user opened it).
- **Never in the feed.** `DiscoveryEngine.ItemsForKind` drops umbrella credits (switched from
  `PlaceholderArtist` to `UmbrellaArtist`), as does the similarity expansion. Collections are
  something you go looking for, not something the frontier pushes at you.
- **Ownership is asked once.** `OwnedAlbumLookup` folds the catalog's owned albums, record-level title
  normalization and recorded merges into one "does the library have this, and under what name?" —
  shared by the collections view, the album tagger and the backfill, so a merged collection can't
  close out on the buy list yet never get tagged.
- **Umbrella acts are excluded from the discography sweep** (`MissingAlbumRefresher`): Deezer lists
  nothing for them, and the sweep's `ReplaceForArtist` would wipe every hand-added collection's row.

_Not done:_ Deezer **playlists** (`/playlist/{id}`). streamrip can fetch one, but it writes each
track with its own album-artist, so Plex shatters the result into one album per contributor — making
it land as a single taggable album needs a post-download retag pass. Out of scope here: the request
was albums.

## Open questions

- **Graph refresh policy:** _resolved_ — `RelatedStalenessPolicy` (`RELATED_STALENESS_DAYS`,
  default 30) governs re-fetch; the periodic replenisher (Phase 7 #4) drives it.
- **Replenisher cadence:** _resolved_ — periodic only (`QUEUE_REPLENISH_INTERVAL_HOURS`, default
  24); decisions already expand live via `ExpandFrom`, so no debounced decision-trigger.
