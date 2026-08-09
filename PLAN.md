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
    left to Plex's own nightly pass, the album lands at the daily anchor below instead.
  - **The automatic/manual switch** lives on the Download page and is **persisted in Mongo**
    (`appSettings`, via `IAppSettingsRepo`), so it survives a redeploy and is re-read on every
    drainer tick — flipping it needs no restart. It is **deliberately not an env var** (automatic
    until turned off), so nothing can contradict what the UI shows; manual downloads work
    regardless. streamrip is always the backend (no NoOp). Env knobs:
    `MUSIC_DOWNLOAD_DIR`, `STREAMRIP_BIN`, `DEEZER_QUALITY` (2), `DEEZER_FALLBACK_QUALITY` (1),
    `DEEZER_CODEC`, `DOWNLOAD_BATCH_SIZE` (3), `DOWNLOAD_ITEM_DELAY_SECONDS` (60),
    `DOWNLOAD_BATCH_INTERVAL_MINUTES` (30), `DEEZER_DOWNLOAD_TIMEOUT_MINUTES` (15),
    `DOWNLOAD_SETTLE_INTERVAL_MINUTES` (15), `DOWNLOAD_SETTLE_WINDOW_HOURS` (6).
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
  - **Per-track fallback:** a pass that comes up short is re-run at `DEEZER_FALLBACK_QUALITY`
    into a second staging tree, and the two are merged *per track* — both passes name files from
    the same `track_format` and differ only in extension, so a track present at FLAC keeps its
    FLAC and only the genuinely missing ones are taken from MP3. Only the merged tree is promoted
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
   **trailing debounce via Rx** (`Subject` → `Throttle(Debounce)` → `Concat`, `PLEX_RESCAN_DEBOUNCE_MINUTES`,
   default 5) so a draining batch folds into one scan once activity quiets, and is a **no-op unless
   `PLEX_RESCAN_AFTER_DOWNLOAD`** is on (default off). The debounce clock is scheduler-injected so tests
   drive it deterministically with a `TestScheduler` (no wall-clock waits).
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

## Open questions

- **Graph refresh policy:** _resolved_ — `RelatedStalenessPolicy` (`RELATED_STALENESS_DAYS`,
  default 30) governs re-fetch; the periodic replenisher (Phase 7 #4) drives it.
- **Replenisher cadence:** _resolved_ — periodic only (`QUEUE_REPLENISH_INTERVAL_HOURS`, default
  24); decisions already expand live via `ExpandFrom`, so no debounced decision-trigger.
