# Per-user quality tiers & upgrade detection

> **Implemented 2026-08-24.** Steps 1-5 are built; this document is now the
> record of *why* it is shaped the way it is, not a proposal. See `PLAN.md` for
> the product vision and `DEPLOYMENT.md` for how to configure it.
>
> **What shipped**
>
> | | |
> |---|---|
> | Per-user tiers | `AudioQuality`, `AppUser.MaxQuality`, dev panel table, `DEFAULT_AUDIO_QUALITY` (default-deny, existing users backfilled to lossless) |
> | Download at the right tier | `PurchaseItem.TargetQuality` = best entitlement among likers; `DEEZER_QUALITY` is the ceiling |
> | Owned quality | Plex track sweep (majority rule), gap-filled per album on ordinary syncs, full re-derivation from *Dev tools → Audio quality sweep* |
> | Upgrade detection | `MissingAlbum.OwnedQuality`, diffed against the ceiling, filtered per user into the `UpgradeAlbum` feed category (shown with missing albums) |
> | Skip / snooze | `AlbumBlockScope.Upgrade` + `RetryAfter`; a thumbs-down is a skip, "Deezer has nothing better" a 180-day snooze |
> | The swap | `LibraryPathMap`, `LibraryTrash` (move-aside + manifest, never delete), `UpgradeSwap` (complete *and* strictly-better gates) |
> | Availability pre-check | `DeezerQualityProbe` (gateway `FILESIZE_FLAC`, tri-state) + `UpgradeAvailability`, consulted from the sweep — added 2026-09-17, reversing the "go blind" decision below |
>
> **Deviations from the plan below, all deliberate:** gap-fill replaced the
> periodic full sweep (per-album reads are ~14ms, so new arrivals resolve within
> minutes from *any* source, not just downloads); `AcquiredQuality` was added so
> a row records what it actually got rather than what it asked for; and the
> `AlbumIsOwned` helpers were unified into a tier-aware `AlbumIsSatisfied`.

## What this is actually for

The primary want is **per-user download tier going forward**: Kelsey is marked
lossy, so albums she queues land as 320 MP3 instead of FLAC. That's it. It caps
what one user's requests cost on the shared volume without touching anything
already in the library.

**Upgrade detection** — "the library has this, but only as MP3, and I want it
lossless" — is a *separate, optional* feature that fell out of the same design.
It is not needed for the primary want, and it is what carries all the risk (see
below). Keeping the two apart is the single most important decision here,
because without upgrades **ownership stays a boolean** and most of the invasive
work disappears.

**Trigger to revisit at all:** the music volume starts filling up, or the user
base grows past the people whose requests we're happy to take at FLAC.

## Verified against the live library (2026-08-24)

Ran the proposed Plex sweep against the real server (`Music Hub`, section key
`1`). Everything the design assumed holds:

- `/library/sections/1/all?type=10` **does** return populated
  `Media[].audioCodec`, `bitrate`, `container`, and `Part[].file` (a real path
  under the `/media/music` mount) in the bulk listing. No per-album calls needed.
- `parentRatingKey` gives the album, `grandparentRatingKey` the artist — an
  exact join onto the `PlexRatingKey` already stored on `OwnedAlbum`.
- Paging via `X-Plex-Container-Start` / `-Size` works; `totalSize` is reported
  on every page. `excludeElements=Genre,Image,Mood,Style,Collection` trims the
  payload.
- **82,340 tracks in 22.4s** over 17 pages of 5000 (~7.3 MB/page, ~120 MB
  total). Entirely acceptable once a day.

### Library composition

| | tracks | share |
|---|---|---|
| flac | 73,544 | 89.3% |
| mp3 | 6,899 | 8.4% |
| aac | 1,631 | 2.0% |
| pcm | 264 | 0.3% |

8,137 albums: **7,436 lossless (91.4%)**, 700 lossy (8.6%), 1 unknown.
Total 2.05 TiB — 1,979 GiB lossless, 118 GiB lossy.

### Real sizing (measured, not estimated)

Median album, single-codec albums only: **260 MiB lossless vs 86 MiB lossy** —
a **3.0× ratio**, matching the theoretical estimate. Mean FLAC track bitrate is
850 kbps; median MP3 is 256 kbps.

So marking a user lossy saves roughly **two thirds** of what their requests
would otherwise cost — about 175 MiB per album.

### Two findings worth keeping

**The upgrade flood is a non-issue.** Only 700 albums are lossy at all, so even
if upgrade detection were switched on wholesale it surfaces hundreds of rows,
not thousands. The edition-multiplication concern (deluxe + remaster each
getting their own row) still applies but against a much smaller base.

**40 albums are mixed-codec, and they look self-inflicted.** The pattern is
overwhelmingly "N flac + 1 mp3" — Ben Howard's *I Forget Where We Were* is 15
flac + 1 mp3, Aether's *Von* is 20 + 1. That is the signature of
`StreamripDownloader`'s fallback ladder doing its job: the album downloaded at
FLAC, one track wasn't available lossless (Deezer's catalogue is patchy per
track), and `DownloadStaging.Graft` filled the gap at 320.

**Decision: an album's tier is the *majority* track tier, not the worst one**
(ties go to lossless; `unknown` rides with the lossless side). A stray 320 in an
otherwise-lossless album is normal and shouldn't mark the album for upgrade.

Validated against the real library — majority vs worst-track changes 30 verdicts,
all correctly:

- Flipped to lossless: the "N flac + 1 mp3" cases (Aether *Von* 20+1, Ben Howard
  15+1, Vampire Rodents *Premonition* 24+1, Various Artists *Dark Was the Night*
  29+1 …).
- Correctly still lossy: the genuinely-mostly-MP3 ones (Rye Rye vs. Filthy
  Fidgets at 1 flac + 19 mp3, Flying Lotus *Pattern+Grid World* at 1 + 7,
  Ufomammut *Idolum* at 1 + 6).

Net: **7,466 lossless / 671 lossy** under the majority rule, vs 7,436 / 700
under worst-track.

## The core problem

**Ownership is a boolean everywhere.** `OwnedAlbum` is `(Title, PlexRatingKey)`,
`IArtistCatalogRepo.GetOwnedAlbums()` returns `artist → HashSet<title>`, and
`MissingAlbumRefresher.FetchAndDiff` reduces the whole question to
`isOwned = scannedOwned.Contains(key)`. Nothing in the app knows what format
anything is in — `PlexMusicAlbum` carries only `{RatingKey, Title, ParentTitle}`,
and quality exists solely as the global `DEEZER_QUALITY` env var (streamrip
`2`=FLAC / `1`=320 / `0`=128) consumed by `StreamripDownloader`.

So the change is: **owned-ness becomes a tier comparison, and the target tier
becomes per-user.**

## Design

### 1. A quality tier vocabulary

New `Mycelium.Interfaces/AudioQuality.cs`:

```csharp
public enum AudioQuality { Lossy = 1, Lossless = 2 }   // room for HiRes = 3
```

plus a mapper to/from streamrip's `"0"/"1"/"2"` and from a Plex codec.

**"Don't know" is `null`, not an enum member.** C#'s lifted comparison makes
`null < AudioQuality.Lossless` evaluate to `false`, so an album whose quality
hasn't been determined never triggers an upgrade — safe by default at every
comparison site, with no guard clause to forget. An `Unknown = 0` member would
do the opposite: it sorts *below* `Lossy`, making every un-swept album look
upgradeable to everyone.

Three states, matching the domain:

| state | meaning |
|---|---|
| absent from the map | don't own it — missing, as today |
| present, `null` | own it; quality not yet determined |
| present, `Lossy` / `Lossless` | own it at a known tier |

**Migration:** old docs with no quality field read as `null`. Not an assumed
`Lossless` — the behaviour is identical (null never triggers an upgrade in
either direction) but it avoids storing a claim that might be wrong, and
`SyncAlbums` rewrites everything on the next daily sweep anyway.

Worth isolating in one file: it's the single place the app's vocabulary and
streamrip's meet, and `MainModule.ParseQualities` will now derive the fallback
ladder from a per-item target rather than the env var.

### 2. Capture owned quality from Plex

Plex's album endpoint (`type=9`) carries no media info; only tracks do. Add a
paged library-wide track sweep (`/library/sections/{key}/all?type=10` with
`X-Plex-Container-Start` / `X-Plex-Container-Size`); each track carries
`parentRatingKey` plus `Media[].audioCodec` / `bitrate` / `container`. Join on
the album `RatingKey` already stored on `OwnedAlbum`.

Reduce each album to its **majority** track tier (see the mixed-codec finding
above) — ties to lossless, `unknown` riding with the lossless side. Not the
worst track: Deezer's per-track gaps mean a stray 320 in an otherwise-lossless
album is routine, and worst-track would mark 30 healthy albums for upgrade.

Cost, measured: 82,340 tracks in 22.4s over 17 pages of 5000. Once a day.

`OwnedAlbum` gains a `Quality`; `ArtistCatalogRepo.SyncAlbums` persists it.

**Change `GetOwnedAlbums()` itself** — `Dictionary<string, HashSet<string>>`
becomes `Dictionary<string, Dictionary<string, AudioQuality?>>`. An earlier
draft suggested a parallel `GetOwnedAlbumQualities()` to keep the blast radius
down, but once upgrades are in scope that leaves two maps that can disagree
about the same album. One source of truth is worth the ~8 mechanical call
sites (`MissingAlbumRefresher` ×4, `DiscoveryEngine` ×3, `PurchaseService` ×4,
plus ~15 test setup lines).

**Unify the two `AlbumIsOwned` helpers while doing it.** There are currently
two private implementations — `DiscoveryEngine.cs:640` (no overrides) and
`PurchaseService.cs:335` (with override keys) — already encoding slightly
different rules. Adding a tier comparison to both invites drift, and that drift
produces "the feed says missing but reconcile says owned," which is miserable
to debug.

### 3. Diff against a ceiling, filter per user

This is the key architectural move. The missing-album sync is global and runs
once; making it per-user would multiply the Deezer discography walk by the user
count, which is a non-starter. Instead:

- `MissingAlbumRefresher` diffs against the **highest tier any user is entitled
  to**, and persists `OwnedQuality` on each `MissingAlbum` row (`Unknown` = don't
  own it at all; `Lossy` = own it, but only as MP3).
- `DiscoveryEngine.MissingAlbumItems(userId)` adds one filter:
  `user.MaxQuality > row.OwnedQuality`. A 320 user never sees the upgrade row.

Same treatment for `DiscographyAlbum.Owned` → carry `OwnedQuality` too, so the
Browse drill-down can badge "MP3 · upgradeable" instead of a plain owned check.

### 4. Per-user entitlement + dev panel

**Chicken-and-egg:** `IUserRepo` is login-populated by design ("populated on
login, no self-registration; the IdP is the source of truth"), so a panel listing
local users only shows people who have signed into Mycelium at least once. A user
who has never logged in cannot be tagged.

Three options, in order of preference:

1. **List local users** — add `GetAll()` to `IUserRepo` (it has only `Get` /
   `UpsertOnLogin` today). The user logs in once, appears, gets a tier. Simplest;
   for a household deployment "log in once" is not a real obstacle.
2. **Pre-seed by username**, matched on first login — avoids the ordering problem
   with no new dependency. Precedent exists: `DevUsers` gates purely on the
   `preferred_username` claim, no local record needed. Keep in reserve.
3. **Query Authentik's API** for the full directory — a new integration with its
   own API token, beyond the three `OIDC_*` values. Marginal payoff at this scale.

**The panel:** a table in `Dev.tsx` alongside `PlexTagTools`, one row per user
showing username, display name, last login and a tier selector — all already on
`AppUser`. Backed by `GET`/`POST /api/dev/users` under the existing `DevUser`
policy.

**Default lossy, with a one-time backfill** (decided 2026-08-24).
`DEFAULT_AUDIO_QUALITY=Lossy` is the right policy posture for a shared library —
a new account can't quietly cost 3× disk before anyone notices; tiers get raised
deliberately in the dev panel.

But a bare flag flip makes **every existing user lossy on first deploy**, which
is confusing in two ways:

- Queued downloads quietly drop to 320 until each user is tagged.
- The upgrade feature looks broken — no upgrade cards appear at all, because the
  feed filter is `user.MaxQuality > row.OwnedQuality` and with no lossless user
  in existence that is never true. You would be debugging a feature working
  exactly as configured.

So: **backfill `MaxQuality = Lossless` for every user already in the collection
at deploy**, and let the env default apply only to accounts created afterwards.
Default-deny going forward, nobody silently regressed. (Tagging everyone by hand
straight after deploy is equivalent in principle, but the drainer and daily sync
can fire in that window.)


`AppUser` gains `MaxQuality` (null = fall back to a `DEFAULT_AUDIO_QUALITY` env
default). `UpsertOnLogin` must write it with `$setOnInsert` so a login doesn't
clobber it. `IUserRepo` gains `GetAll()` and `SetMaxQuality()`.

New `api.MapGroup("/dev/users").RequireAuthorization("DevUser")` alongside the
existing `/dev/plex-tags` and `/dev/similarity` groups — GET the user list with
their tier, POST to set one. Frontend: a `UserQuality` section in `Dev.tsx`
(same shape as the existing `PlexTagTools`), plus `api/dev.ts` and `types.ts`.
`auth/me` returns the caller's tier so the UI can label cards.

### 5. Download at the requested tier

`PurchaseItem` gains `TargetQuality`. In `PurchaseService.Reconcile`, an album's
target is the **max entitlement among the users who liked it** — which needs a
userId-carrying variant of `IUserAlbumRatingRepo.GetAllLiked()` (it currently
drops the userId). `StreamripDownloader.RunAt` takes the item's target instead
of `_config.Quality` and derives its fallback ladder from that;
`DownloaderConfig.Quality` becomes the ceiling/default.

### 6. The close-out trap

`Reconcile` currently flips a row to `InLibrary` when the title matches an owned
album. **An upgrade row would flip to `InLibrary` on the very next reconcile**,
before anything downloaded, because the MP3 copy is right there. `nowOwned` has
to become `ownedQuality >= row.TargetQuality`. Miss this and upgrades silently
never download. The same trap exists in `PurchaseService.AddManual`'s
`AlreadyOwned` check.

## Upgrades: move-aside, don't delete

`DownloadStaging` notes that streamrip's `folder_format` embeds `{container}`
and `{bit_depth}`, so an upgraded FLAC lands in `Album [FLAC] [16B-44kHz]` next
to the existing `Album [MP3]` — Plex then shows the album twice. `Promote`
merges directories; it has no notion of superseding.

**Decision: move the old copy aside rather than delete it.** A configured trash
path (e.g. `/music-to-remove`, mounted into the Mycelium container) receives the
superseded album; the upgrade is promoted in its place; actual deletion happens
later, by hand or on a retention sweep. Reversible, and it keeps a destructive
filesystem operation out of the download path.

Order of operations matters: `RunStaged` already verifies the download before
promoting, so the sequence is **download → verify → move old to trash → promote
new → rescan**. A failure between the last two steps leaves the album absent
from the library but recoverable from the trash, which is the right failure mode.

### Blocker: Plex's paths are not Mycelium's paths

Measured against the live server. Plex reports `Part[].file` in **its own**
namespace, and the music library spans two roots:

| tracks | share | Plex root | albums |
|---:|---:|---|---:|
| 55,255 | 67.1% | `/media/music` | 5,727 |
| 27,084 | 32.9% | `/mediadrop/Music` | 2,410 |
| 1 | 0.0% | `/media/download` | 1 |

Only one album straddles two roots.

Mycelium's container, meanwhile, sees a **single** mount at `/music`
(`compose.yaml`: `${MUSIC_DOWNLOAD_DIR_HOST}:/music`). So Plex's paths are not
usable verbatim — and worse, **380 of the 671 upgrade candidates live under
`/mediadrop/Music`**, a root the container almost certainly cannot see at all.
A naive implementation would work for `/media/music` albums and silently fail
for the majority of the ones that need it.

**Decision (2026-08-24): disregard `/mediadrop/Music` for now** — but for
*upgrade actions only*, not for quality tracking. Reading a codec comes from the
Plex API and works for every album; only the trash-swap needs a mapped
filesystem root. So tier everything, and mark albums outside a mapped root as
non-upgradeable so they never surface as candidates. Costs nothing, keeps the
stored data honest, and mounting `/mediadrop` later becomes a config line
rather than a backfill.

That puts **291 albums** under `/media/music` in scope as upgrade candidates,
out of 671 lossy overall.

This is the same problem Radarr/Sonarr call *remote path mapping*. Two things
are needed:

1. An explicit **path map** in config — e.g.
   `PLEX_PATH_MAP=/media/music:/music,/mediadrop/Music:/mediadrop` — applied to
   every `Part[].file` before touching the filesystem. Never guess a prefix.
2. A hard **refuse-with-reason** for any album whose path doesn't resolve under
   a mapped root, surfaced on the row like `DownloadFailure` already is. Silent
   skips are the failure mode to design out.

The trash path should also be **per-root and on the same filesystem as its
source** — `/media` and `/mediadrop` are near-certainly different mounts, and a
cross-filesystem move degrades to copy+delete (slow, non-atomic) for a 300 MB
album.

### Move-aside is mandatory, not deferrable

Measured: **zero** of the 8,313 album directories under either root contain a
`[FLAC]` / `[MP3]` / bit-depth marker. The library is uniformly
`{root}/{Artist}/{Album}/`, and the 20 most recently added albums (all landed
2026-08-24, i.e. live downloads) follow it exactly. So the streamrip config is
already customised away from the default `folder_format` that `DownloadStaging`'s
comment describes.

**Consequence:** an upgrade download lands at the *same path* as the copy it
replaces. `Promote` → `MoveInto` merges directories, so promoting first would
interleave `01 - Track.flac` beside `01 - Track.mp3` in the live folder and Plex
would show a doubled album. The old copy therefore has to be moved out **before**
the promote, not cleaned up afterwards:

    download → verify → move old aside → promote new → rescan

(This is the opposite of what `DownloadStaging`'s comment predicts, because that
comment describes streamrip's defaults rather than the deployed config. Worth
re-reading that comment when implementing — it's accurate about staging, stale
about promotion.)

### Ratings on swap — they follow the Plex match

An earlier version of this section said ratings live on the track item's
ratingKey, so a swap would keep them as long as the ratingKeys held. **That was
wrong, and the first real upgrade showed it.**

Plex stores `userRating` against what an item is **matched** to: the agent
guid (`plex://album/…` and the `plex://track/…` ids under it) plus the account.
It is not stored against the file or the ratingKey. A swap brings in new files,
which Plex indexes as new items. They get the old ratings back only if Plex
matches them to the same release.

Measured on 2026-09-18: Children of Bodom — *Hate Crew Deathroll*, MP3 → FLAC.
Plex matched the FLAC copy to a different release than the MP3s had, and every
track rating disappeared except the first track's (whose track guid presumably
matched in both releases). A **Fix Match back to the original release restored
all of them.** Deezer often serves a remaster or reissue, so expect this to
happen regularly rather than rarely.

**What the code does about it** (`UpgradeMatchKeeper`):

1. Before any file moves, `UpgradeSwap` saves the old album's match on the row
   (`UpgradeReport.OldMatch`). An album that was never matched (`local://…`)
   has no release to keep and is marked `NotMatched`.
2. After the promote, each settle pass finds the album in Plex. It checks that
   Plex is listing the *new* copy (its quality beats what was held, so the old
   copy still listed before the rescan isn't taken as confirmation), then
   compares the match.
3. Same match → `Kept`. A different match → rematch via
   `PUT /library/metadata/{key}/match?guid=…&name=…` (what "Fix Match" does),
   read it back, → `Rematched`. Still different after the settle window →
   `NeedsFixMatch`, surfaced on the Download page's Upgrades section with a
   Recheck button.

Rematching beats capturing and re-applying each rating (the approach an
earlier version of this section proposed). It restores every account's ratings
at once, including accounts that never linked Plex to Mycelium, which
`/:/rate` can only reach through each user's own token. Re-rating stays a
possible fallback if rematching ever proves unreliable.

The trade-off: rematching pins the upgrade to the old release, so bonus tracks
on a deluxe or remastered edition have no counterpart there and stay unrated.
For keeping existing ratings, that is the right call.

**Scan timing is still a smaller risk.** `autoEmptyTrash = True`, so a scan
that catches the album mid-swap (old moved aside, new not yet promoted) trashes
its items. Plex isn't watching the filesystem (`watchMusicSections`,
`FSEventLibraryUpdatesEnabled` and `FSEventLibraryPartialScanEnabled` are all
off; only scheduled scans run), and move-aside and promote run back to back
before Mycelium asks for its debounced rescan, so that window is rarely hit.
Because ratings follow the match rather than the item, an item trashed by an
unlucky scan loses nothing that the rematch can't restore.

**Play history isn't known to survive.** Whether play counts follow the match
the way ratings do is unverified. If they don't, they can't be restored
(`/:/scrobble` only increments), which is probably acceptable where losing
ratings wouldn't be.

Scale, for context: 16.1% of tracks carry a rating and 66.8% have plays; of the
671 upgrade candidates, 407 have rated tracks and 620 have play history.

### mediadrop is organised by contributor, not artist

`/mediadrop/Music/` splits into **Brennan** (18,877 tracks), **Rachel** (8,128)
and **Misc** (79) before the artist level — one directory level deeper than
`/media/music/{Artist}/{Album}`. Two implications:

- Path handling can't assume a fixed depth. (The library also has 156 loose
  tracks at `{root}/{Artist}/track.ext` with no album folder, and 77 at depth 9
  — presumably multi-disc. Folder-level moves need to handle both.)
- Upgrading a mediadrop album *relocates it out of someone's collection* into
  the shared `/media/music`. That's a consolidation, and probably the desired
  end state, but it's a social question as much as a technical one — worth
  deciding deliberately rather than as a side effect of an upgrade.

### Smaller trash mechanics

## Pre-flight findings (2026-08-24)

Sampled 40 of the 291 in-scope lossy albums against the Deezer API.

**Yield is ~63%, and the misses are systematic.** 15 of 40 had no Deezer match
at all — Hearts of Space radio programs (9 in the sample alone), obscure ambient
(Mathias Grassow, gorse panshawe), odd EPs. These are MP3 *because* they were
never commercially released. Realistic upgrade yield is **~180 of 291**, and the
sync will retry the impossible ones every pass unless a "Deezer doesn't have
this" verdict is persisted (`IAlbumBlockRepo` could serve, or a new
`DownloadFailure` state).

**Deezer sometimes has fewer tracks than we own.** 1 of 25 matches was smaller
(Richard M. Jones — *Black Rider*: own 5, Deezer 4). The swap replaces a folder
wholesale, so that is **silent music loss**. `RunStaged` computes `expected` from
`GetAlbumTracks` but only compares it against what downloaded — never against
what is already owned. Add that comparison and refuse the swap when the new copy
is smaller. Zero albums had *more* tracks on Deezer, so there is no deluxe-edition
upside to trade against this.

## Remaining gaps before a first attempt

**Decision (2026-08-24): go blind, with a skip mechanism.** No gateway
pre-check. If an owned album is lossy and a lossless user likes the artist,
recommend the upgrade assuming FLAC exists; if the download can't deliver it,
record a skip. The skip mechanism is needed anyway for "I don't want to upgrade
this one," so the failure path reuses it.

*(Rejected alternative: probing `deezer.pageAlbum` on the internal gateway for
`FILESIZE_FLAC` per track. Feasible — `DeezerSessionCheck` already logs in with
the ARL and `StreamripArlStore` already reads it — but it widens an undocumented
private-API dependency onto a per-album path, runs into Akamai bot protection
(`_abck`/`bm_sz` cookies) at sweep volume, and makes recommendations depend on a
credential that expires. Not worth it for a once-ever determination.)*

### Reversed 2026-09-17: the pre-check is in

The rejected alternative is now `DeezerQualityProbe` + `UpgradeAvailability`,
consulted from `FetchAndDiff`. Two of the three objections above turned out to
be weaker than they read, and the third was answered rather than accepted.

**"Akamai at sweep volume" assumed a sweep.** That assumption died in the same
sitting — the bulk sweep was dropped for feed-driven trickle, a few paragraphs
down. The verdict is persisted where the post-hoc snooze already goes
(`AlbumBlockScope.Upgrade` + `RetryAfter`), so it is one call per album *ever*,
not per pass: a few dozen a night in steady state, paced at 400ms, on a session
minted once and reused. That is not a volume signature.

**"Recommendations depend on a credential that expires" is true, and turned out
to be the whole design rather than an objection to it.** Measured while
building this: the gateway's per-track sizes are scoped to the asking account,
and a session with no lossless entitlement is answered `FILESIZE_FLAC: 0` *and*
`FILESIZE_MP3_320: 0` for every track. Checked against Daft Punk's *Discovery*,
which returns fourteen tracks of zeroes to a guest — and an expired ARL **is** a
guest, because the gateway answers `USER_ID: 0` and carries on rather than
refusing.

So the hazard was never a failure. It is a well-formed 200 carrying a confident
wrong "no", and the naive implementation writes off the entire library from one
expired cookie without a single error to show for it. The shape that survives it
is a tri-state — available / unavailable / **unknown** — where `LossyOnly` is
returned *only* from a session that positively reported `web_lossless`, the same
flag `DeezerSessionCheck` already reads. Everything else is `Unknown`.

**`Unknown` offers the album anyway.** This is the load-bearing line. No
credential, a dead one, a non-HiFi account, Deezer unreachable, a response shape
we don't recognise — all degrade to exactly the blind behaviour decided on
2026-08-24, which is known-acceptable, and nothing is persisted, so the next
sweep asks again. "Try again later" needs no scheduling: it falls out of not
writing a verdict. Suppressing on `Unknown` would be the silent-kill, and is the
one thing this must never do.

The asymmetry driving all of it: a false *yes* costs one drain slot and
self-corrects via `NoBetterQualityAvailable`; a false *no* is a 180-day snooze
on an album that was upgradeable, invisibly, at library scale. Only a
positively-entitled session earns the right to say no.

This is the same distinction the codebase already draws twice — `IDeezerApi`
telling an empty result (a real miss) apart from `null` (never answered), and
`AudioQuality?` using null-means-unknown so the lifted comparison is safe by
default. Third instance, same reasoning.

**What it deliberately doesn't cover.** The probe runs on
`ArtistResolution.Full` only — the nightly sweep — never in front of a click,
the same line that enum already draws. A drill-down therefore shows candidates
the sweep hasn't reached yet. `DiscographyAlbum.OwnedQuality` is untouched too,
so the Browse badge can still read "upgradeable" for an album the sweep has
since ruled out; the badge doesn't consult upgrade verdicts today either, so
that is pre-existing rather than new.

**"Lossless exists" is the wrong question; "would the download come out
better" is the right one.** The first two albums probed against the real gateway
were 14/14 and 2/12. The second is the instructive one: the ladder fills the ten
gaps at 320, so what lands reads *lossy* under `AudioQualityTier.Majority`,
loses `UpgradeSwap`'s strictly-better gate, and leaves the MP3 exactly where it
was. A pre-check keyed on "has any FLAC" would have offered it and spent the
slot anyway — so the check applies `Majority` to the album it predicts will
land, reusing the same function the library is tiered by rather than restating
the arithmetic.

**Spot check against the real candidate population (2026-09-17).** Probed the
lossy albums this document names by title — the "genuinely-mostly-MP3" list
above, plus the misses it calls systematic:

| album | Deezer | verdict |
|---|---|---|
| Rye Rye vs. Filthy Fidgets | 16 tracks, 13 FLAC | offer |
| Flying Lotus — *Pattern+Grid World* | 7 tracks, 0 FLAC | suppressed |
| Ufomammut — *Idolum* | 7 tracks, 0 FLAC | suppressed |
| Richard M. Jones — *Black Rider* | 4 tracks, 1 FLAC | suppressed (1/4) |
| Mathias Grassow, gorse panshawe, Hearts of Space | no match | never a row |

Three of the four matched albums would be suppressed — including *Black Rider*,
the own-5/Deezer-4 case set aside as "accepted, silent music loss": the quality
rule catches it before the completeness gate has to.

**Caveat, and it is a large one: n=4, and not a random four.** These are the
albums this document picked out *because* they are mostly MP3, which skews
heavily toward obscure and unreleased material. It is evidence that the dud rate
is not negligible, not a measurement of it.

**The real count is still owed** and needs a working Plex token or the Mongo
tunnel — derive the 291 in-scope candidates, probe each, count. The 15-of-40
Deezer miss rate above does not answer it: those are albums with *no match at
all*, which never produce an upgrade row in the first place. What matters is the
share of matched-but-lossy candidates with no usable FLAC, and the spot check
says that share is worth knowing.

### Skip vs snooze

Two distinct verdicts, both needed:

| verdict | meaning | lifetime |
|---|---|---|
| **skip** | "I don't want to upgrade this album" — a deliberate choice | permanent until lifted |
| **snooze** | "Deezer had no FLAC" — a discovered fact that can change | carries `RetryAfter`; lapses back into candidacy |

`IUserAlbumRatingRepo` already models exactly this pair
(`DiscoveryStatus.Snoozed` + `SnoozeUntil`, with `GetDecidedKeys` dropping
expired snoozes so the album resurfaces), so the semantics are proven in the
codebase. The upgrade version needs to live on a **global** record rather than a
per-user one — "Deezer has no FLAC" is a fact about the album, not about a
person.

Neither existing store is the right axis as-is:

- `IUserAlbumRatingRepo.Rate(Disliked)` would record that a user *dislikes an
  album they own and like* — polluting the Ratings page and `GetAllLiked`.
- `IAlbumBlockRepo` means "a release nobody should be offered," and is consulted
  by every album surface — an owned album would render as blocked in the
  discography drill-down.

**Cheapest correct thing: a scope + optional `RetryAfter` on `AlbumBlock`** —
scope `Release` vs `Upgrade`, with `RetryAfter` null for a skip and set for a
snooze. Reuses `AlbumOverrideKey` keying, the global store, and the existing
lift-the-block UI, while keeping "don't offer this record" distinct from "this
record stays as it is."

**Both readers of the verdict must agree** (corrected 2026-09-01). The feed is
not the only route to an upgrade download: `PurchaseService.Reconcile` derives
the shared to-buy list straight from liked albums + owned quality, so it reaches
the same conclusion independently. It therefore consults the upgrade verdict too
(`UpgradeSkippedKeys`, the same read `DiscoveryEngine` does, expiry included).
Without that, the snooze the downloader writes on `NoBetterQualityAvailable` only
silences the *card*: reconcile re-wants the album on the very next pass, the
prune can never reach the `Failed` row, and it sits in the download list for ever
offering a Retry guaranteed to fail identically. Note the asymmetry with scope
`Release`, which deliberately does **not** prune purchase rows — a block stops an
album being offered and does not retract a purchase someone chose to queue.

### The fallback ladder stays ON (corrected 2026-08-24)

An earlier draft said to disable the ladder for upgrades. **Wrong** — it would
discard the partial-FLAC case, which is the common one. Where Deezer has FLAC
for 10 of 12 tracks:

- ladder off → 10 files, incomplete, abort — a real upgrade thrown away
- ladder on → 10 FLAC + 2 MP3, complete, reads *lossless* under the majority
  rule, genuinely better than the 12 MP3 it replaces

This is literally how the library's 40 mixed-codec albums came to exist — the
ladder doing exactly this on first acquisition.

The ladder is therefore identical to an acquisition's: prefer FLAC, fall back as
needed. What prevents pointless churn is not the ladder but **the promote gate**:

1. **Complete** — `IsComplete(got, expected)`, so a short album never swaps in.
   (This also retires the fewer-tracks-on-Deezer problem set aside above.)
2. **Strictly better than owned** — take the majority tier of what landed and
   compare against the owned tier. An all-MP3 result against an MP3 original is
   not better: abort and snooze. Same majority rule as the library tiering, so
   one definition serves both labelling an album and deciding a swap is worth it.

**Consequence: the tie rule becomes load-bearing.** "Ties go to lossless" was a
display decision; under gate 2 a 6-FLAC/6-MP3 result would count as an upgrade
over 12 MP3 and trigger a real file swap. Defensible — but the *acting* threshold
can be made stricter than the *labelling* one (require a strict majority to swap,
while still calling a tie lossless) if half-measures aren't worth the churn.

### The systemic-failure trap

If the ARL expires mid-sweep every upgrade fails identically, and a naive
auto-snooze marks all ~180 candidates "no FLAC available" in one go.
`DownloadFailureExtensions.IsSystemic()` exists for exactly this shape and must
gate it: auto-snooze on `NoTracksAvailable` only, **never** on `DeezerAuth` /
`DeezerCredentialsMissing`. This is the one that would quietly ruin the dataset.

**Where the tier comparison goes.** The Deezer id is *not* a problem — 
`FetchAndDiff` iterates Deezer's discography listing, so `album.id` is in hand
for owned albums too, and `DiscographyAlbum` already carries it regardless of
`isOwned`. The only change is the persistence condition: `if (!isOwned)` becomes
a tier comparison, **in `FetchAndDiff`**, not only in the feed filter. Implement
it in `DiscoveryEngine` alone and upgrades show in the feed while being
un-queueable.

**Entry point: the normal Discover feed** (decided 2026-08-24 — no bulk sweep).
Once `FetchAndDiff` persists a row for an owned-but-lossy album, it flows into
`MissingAlbumItems` like any other missing album and the existing thumb mechanic
already means the right things:

| action | meaning | writes to |
|---|---|---|
| 👍 | queue the upgrade | purchase row, `TargetQuality` set |
| 👎 | permanent skip | **upgrade-skip record** — *not* `Rate(Disliked)` |
| snooze | check again later | upgrade-skip record with `RetryAfter` |

The card needs to *read* differently — "you have this as MP3" rather than "you
don't have this" — which is a badge off `OwnedQuality`.

**The gesture is shared; the destination is not.** A 👎 on an upgrade card must
write the dedicated upgrade verdict (see *Skip vs snooze*), never an album
dislike — the user owns and likes this album, and recording a dislike would
pollute the Ratings page and `GetAllLiked`. The card already knows which kind it
is (`OwnedQuality` is set), so the rate handler branches on that.

*Dropping the bulk sweep also retires the queue-starvation problem* — upgrades
now trickle in at whatever rate people rate them, rather than 180 rows landing
in a FIFO queue at once.

**Match overrides carry no tier.** An `IAlbumMatchOverrideRepo` entry means
"treat as owned" full stop. Unless it carries the owned album's quality through,
override-matched albums can never be upgraded.

**Trash retention.** Unbounded — ~25 GiB if all 291 upgrade. Needs a sweep
eventually, not before the first attempt.

### Explicitly out of scope (2026-08-24)

- **Fewer tracks on Deezer than owned.** Measured at 1 in 25; accepted — and
  incidentally handled for free by the promote-only-if-complete rule above.
- **Albums missing from Deezer.** Simply don't offer the upgrade.
- **Ratings/play-count loss on swap.** Accepted; not worth guarding.
- **Gateway pre-check of FLAC availability.** ~~Rejected in favour of blind +
  skip.~~ Reversed 2026-09-17 — see *the pre-check is in*, above. Blind remains
  the fallback for every case the check can't answer.

## Operational notes for the first sweep

**Queue starvation — retired.** No bulk sweep, so this no longer applies. Kept
for context: the drainer is strict FIFO (`OrderBy(p => p.RequestedAt)`) with no
priority concept, at `BatchSize` 3 per `BatchInterval` 30 min = 6 albums/hour. A
180-row bulk queue would have been ~30 hours deep, starving anything liked during
it. Feed-driven upgrades trickle instead, so the queue never sees that shape.

**Dry run + a move manifest.** Preview what would move where before anything
moves; that is where a path-mapping mistake surfaces cheaply rather than after 50
albums have shuffled. Write a small `source → trash` manifest alongside each
trash entry so a swap that fails mid-way is reversible by hand — nothing
automates restoring from trash, and without a manifest you are reconstructing
where files came from.

**Disk headroom.** ~180 upgrades ≈ 65 GiB of new FLAC, plus ~15 GiB in trash
until cleared. Modest against 2 TiB, but worth checking the volume before
starting.

**Step 3 verifies step 2.** Without the Plex quality sweep there is no way to
confirm a lossy user's downloads actually landed as MP3 short of checking files
by hand. It is cheap and safe, so running it before the upgrade work gives a way
to see the tiering is right before anything acts on it.

## Invasiveness

**This splits in two, and the split is the whole point.**

### Path A — per-user download tier only (the actual want)

Ownership stays a boolean. No Plex changes, no diff changes, no close-out trap,
no deletion story. **~14 files, all additive.**

| Area | Files |
|---|---|
| New `AudioQuality` type + streamrip mapping | 1 |
| Entitlement storage (`AppUser`, `IUserRepo`, `UserRepo`) | 3 |
| Dev panel endpoints (`Program.cs`) | 1 |
| `PurchaseItem.TargetQuality` (`IPurchaseRepo`, `PurchaseRepo`) | 2 |
| Reconcile target = max entitlement among likers (`PurchaseService`, `IUserAlbumRatingRepo` + impl) | 3 |
| Downloader honours per-item target (`StreamripDownloader`, `MainModule`) | 2 |
| Frontend (`types.ts`, `api/dev.ts`, `Dev.tsx`) | 3 |
| Tests | ~3 |

The only non-mechanical bit: `IUserAlbumRatingRepo.GetAllLiked()` currently
drops the userId, so it needs a userId-carrying variant for "whose entitlement
drives this row." Everything else is threading one enum through.

### Path B — upgrade detection + clean swap (committed, additive on top of A)

Adds the Plex sweep, ownership-as-tier throughout, per-user feed filter, the
`nowOwned` close-out fix, path mapping, and the trash-swap. **~15 more files**,
and it is where the risk lives — chiefly the path mapping and the swap's
failure modes, both covered above.

Note that `MissingAlbum` / `PurchaseItem` / `OwnedAlbum` each gain a field that
existing Mongo docs won't have. Defaulting absent to `Unknown` is safe on the
*diff* side (reads as "don't own it") but wrong on the *owned* side — an album
synced before the sweep would read as `Unknown` and look upgradeable. Treat
`Unknown` as "not upgradeable, only missing-if-absent" until the first
quality-aware sync has run.

## Phasing

1. **`AudioQuality` type + entitlement storage + dev panel.** No behaviour
   change; Kelsey can be marked lossy but nothing reads it yet.
2. **Purchase target + downloader honours it.** Path A is done — this is the
   feature as actually wanted.
3. **Plex quality sweep, stored but unused.** Pure observability: see what the
   library is, verify step 2 did what you asked. Cheap and safe — validated at
   22.4s for the full library. Also de-risks step 4 by letting the tiering be
   eyeballed before anything acts on it.
4. **Ownership becomes a tier** (nullable enum through `GetOwnedAlbums`,
   unified `AlbumIsOwned`, quality-aware diff, per-user feed filter, `nowOwned`
   close-out fix). Upgrades become *visible* — nothing moves files yet.
5. **Path mapping + trash-swap.** Upgrades become *actionable*. Gate on a
   mapped root; refuse-with-reason otherwise. Dry-run first.

Steps 1–2 deliver the Kelsey case on their own. 3 is cheap and worth doing
regardless. 4–5 are the mp3→FLAC upgrade path, and 5 is the only step that
touches existing files.

## Notes

**Path A has one hole: first-liker-wins on shared albums.** If Kelsey (lossy)
likes an album first, it downloads as MP3. If you like it afterwards, the
purchase row is already `Sent`/`InLibrary` — `Upsert` refreshes display fields
without touching status, and boolean ownership says "we have it", so nothing
re-fetches and your FLAC want is silently satisfied by her 320.

The cheap fix needs no Plex sweep: persist `AcquiredQuality` on `PurchaseItem`
(what it was actually fetched at) and have `Reconcile` flip the row back to
`Pending` when the computed target rises above it. That closes the hole within
Path A. Whether the *re-download* then replaces the MP3 copy is the Path B /
trash-swap question — but at minimum the row should stop claiming to be done.


**Downgrades never need a download.** If a 320-entitled user likes an album
already held in FLAC, they're covered — the `ownedQuality >= targetQuality`
check treats it as owned and the row closes out with no work. Only upgrades
(MP3 held, FLAC wanted) trigger a fetch. That asymmetry is what makes the
missing-album change worth building.

**Playback is already solved.** Plex transcodes on the fly, so a 320 user
streaming from a FLAC library gets whatever bitrate their client negotiates.
The tier is purely about *acquisition* cost, not playback.

**Transcoding is not a shortcut around the tier.** FLAC → MP3 320 is clean (one
lossy generation from a lossless master, the same operation Deezer performs to
make its own 320s):

```bash
ffmpeg -i in.flac -codec:a libmp3lame -b:a 320k -map_metadata 0 out.mp3
# or -q:a 0 for V0 (~245 kbps VBR, transparent for most material)
```

But downloading FLAC and transcoding saves nothing over downloading MP3 — same
disk once the FLAC is deleted, more bandwidth, plus a transcode step. Where it
*does* earn its keep is a separate derived library (a phone or car sync target
built from the FLAC originals) — orthogonal to this work, and a script over
`/music` rather than something Mycelium models.

## Open decisions

- **Is upgrade detection (Path B) wanted at all?** At 91% lossless it buys
  little. Deferring it keeps ownership a boolean and halves the work.
- **Tier granularity.** Lossy/lossless is enough while Deezer is the only
  source (it serves 16/44.1 FLAC only). Hi-res would need its own tier.
- **Per-track upgrade retry** for the 40 mixed-codec albums — separable from
  everything else, and `DownloadStaging.Graft` already has the machinery.
- **Phase 5 deletion** — wanted, or is manual cleanup fine indefinitely?
