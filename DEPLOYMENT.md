# Deploying Mycelium (Podman + Komodo)

Two containers: the **app** (ASP.NET Core API + the built React SPA, served together on one HTTP
port) and **MongoDB**. The app speaks plain HTTP — put it behind your own reverse proxy for TLS and
public routing. The Aspire AppHost (`src/AppHost`) is a local-dev orchestrator and is **not** used
here; the backend DLL runs directly and every setting comes from environment variables.

```
  your reverse proxy ──HTTP──▶  app  :43105 ┬─ /            React SPA (static, with deep-link fallback)
   (TLS, public DNS)                        ├─ /api/*        REST API
                                            ├─ /auth/*, /signin-oidc, /signout-callback-oidc  (BFF/OIDC)
                                            └─ rip (streamrip) ─▶ /music  (your Plex library)
                                       app ─▶ mongo :27017
```

| Service | Image (built from) | Purpose                                              |
|---------|--------------------|------------------------------------------------------|
| `app`   | `Dockerfile`       | API + SPA on one HTTP port, with bundled `streamrip` |
| `mongo` | `mongo:7`          | Primary data store                                   |

## Files (all at repo root)

- `compose.yaml` — the stack
- `Dockerfile` — builds the SPA and the API into one image
- `.env.example` — copy to `.env` and fill in (`.env` is gitignored)

## 1. Configure

```bash
cp .env.example .env
# edit .env — see the inline comments
```

Required: `PUBLIC_ORIGIN`, the three `OIDC_*` values, `PLEX_ENDPOINT`, `PLEX_TOKEN`,
`MUSIC_DOWNLOAD_DIR_HOST`, `STREAMRIP_CONFIG_HOST`.

`MUSIC_DOWNLOAD_DIR_HOST` **must be the same storage Plex scans** for its music library — that's how
downloaded albums show up in Plex (the app can also trigger a Plex rescan via
`PLEX_RESCAN_AFTER_DOWNLOAD=true`).

## 2. Reverse proxy + Authentik

Point your reverse proxy at `app:${HTTP_PORT}` and serve it at `PUBLIC_ORIGIN` over HTTPS (the
auth session cookies need HTTPS in practice). Then, in the Authentik OAuth2/OIDC provider for this
app, register the redirect URI:

- `${PUBLIC_ORIGIN}/signin-oidc`  (e.g. `https://music.example.com/signin-oidc`)

Authentik must be reachable from the app container at `OIDC_AUTHORITY`.

## 3. Deploy via Komodo

Create a **Stack** in Komodo pointing at this repo:

- **Compose file:** `compose.yaml`
- **Environment:** paste the contents of your `.env`, or have Komodo write the `.env` file
- Deploy. Komodo (with the Podman engine) builds the `app` image from the top-level `Dockerfile`
  (which builds the SPA too) and starts the stack.

`app` uses a plain `depends_on: [mongo]` — start order without a health gate. Gating on
`condition: service_healthy` cost ~5 minutes per deploy here: Podman runs container healthchecks off
a transient systemd timer, and when that doesn't fire the compose wait blocks on "starting" long
after mongod is actually serving. Mongo still starts first and the driver retries, so the app comes
up fine. (The healthcheck itself is kept — it's just for status, not for sequencing.)

Two follow-on traps, both hit for real:

- **Don't "fix" the CPU cost by stretching `interval`.** A container's health stays `starting` until
  the *first* probe runs, and the first probe doesn't run until one `interval` has elapsed — so a
  `mongosh` probe at `interval: 5m` behind a `service_healthy` gate stalls every deploy by five
  minutes while mongod has been serving since second one. `start_period` doesn't help; it only stops
  early failures counting against `retries`. Cheapen the probe (above), don't rarefy it.
- **`start_interval` is not available here.** The Compose field that would give "probe fast until
  healthy, then back off" needs Docker Engine 25+; Podman doesn't implement it and silently ignores
  it — which, paired with a long `interval`, means the gate never opens at all. Podman's own
  `--health-startup-*` flags have no Compose equivalent.

### Separate Komodo Build + Stack (no registry)

If you split this into a Komodo **Build** (clones this repo, runs `docker build`) and a **Stack**
that references the built image by name (e.g. `localhost/<image>:latest`), two non-obvious things
are required to make it work without pushing to a registry:

1. **Build: add `--load`.** Komodo builds with BuildKit's `docker-container` driver, which keeps the
   result in the build cache and exports *no* image unless told to. Put `--load` in the Build
   resource's **Extra Args** so it imports the finished image into the local engine store. (Only
   works when the build and the Stack run on the **same host**; otherwise push to a registry.)
   Success looks like `exporting to docker image format` + `importing to docker` at the end of the
   build log — if you instead see "Build result will only remain in the build cache", `--load` is
   missing.
2. **Stack: add `pull_policy: never`** on the `app` service. Komodo runs `docker compose pull` before
   `up`; without this it tries to pull the local image as if `localhost/` were a registry and dies
   with `tls: internal error`. `pull_policy: never` makes the pull step report `Skipped`.

## 4. First-run streamrip (Deezer ARL)

The download path shells out to `streamrip` (`rip`), which keeps the Deezer **ARL** session token in
its own config — not in this app's env. After the stack is up:

```bash
# generate a default config into the mounted config dir, if not present
podman exec -it mycelium-app-1 /opt/streamrip/bin/rip config
```

Then edit `config.toml` on the host (at `STREAMRIP_CONFIG_HOST/streamrip/config.toml`) and set:

```toml
[deezer]
arl = "<your deezer ARL>"

[filepaths]
folder_format = "{albumartist}/{title}"
track_format  = "{tracknumber}. {title}"
```

The "Download now" button works as soon as the ARL is set. The queue also drains in the background by
default — flip the **auto/manual switch** on the Download page to change that. That choice is stored
in Mongo (not an env var), so it survives redeploys and takes effect without a restart.

## Per-user download quality

Lossless runs roughly **3x** the size of 320kbps MP3 for the same album (measured against a real
library: ~260 MiB vs ~86 MiB for a median album). `DEFAULT_AUDIO_QUALITY` sets the tier for an
account nobody has decided about, and **Dev tools -> Download quality** sets it per user.

- The default is `Lossy`, so a new account can't quietly cost 3x the disk before you've looked at it.
- Users already in the database when this first ships are **backfilled to `Lossless`** at startup, so
  nobody is silently demoted. The default only ever applies to accounts created afterwards.
- `DEEZER_QUALITY` remains the deployment **ceiling**: a user marked lossless on a deployment pinned
  to 320 still gets 320.
- An album several people want is downloaded **once**, at the best of their tiers — a lossy user
  riding along on a lossless request costs nothing, where the reverse would cheat the lossless user.
- This affects acquisition only, not listening: Plex transcodes on playback regardless.
- Accounts appear in the panel only **after they have signed in at least once** — the user store is
  populated on login and the IdP is never enumerated.

## Album upgrades (replacing a lower-quality copy)

When the library holds an album below what a user is entitled to, it can be offered in Discover as
an **Upgrade album** card. It rides along with the "Add missing album" chip — same ask ("go get this
album"), so one filter covers both the gap and the worse copy — and the card keeps its own badge so
it's obvious which one you're looking at. A thumbs-up queues it; a thumbs-down records "keep the copy
we have" and is kept apart from album ratings, so declining an upgrade never shows up as disliking a
record you own.

**`PLEX_PATH_MAP` is required for upgrades to complete.** Plex reports file paths in its own
namespace, which is not this container's. Declare the translation as `plexPrefix:localPrefix` pairs:

```
PLEX_PATH_MAP=/media/music:/music
```

Add a pair per library location you want upgradeable, comma-separated. An album whose files fall
outside every mapped prefix is **refused, not guessed at** — the download is discarded and the
library left untouched, with `Couldn't replace the existing copy` on the row. Without any mapping,
upgrades download and are then refused; gap-filling is unaffected.

**Nothing is deleted.** The superseded copy is moved to a `.mycelium-removed/` folder beside it,
with a `manifest.json` recording where each file came from, so a bad swap can be undone by hand.
Clearing that folder out is a separate, manual decision.

**Two gates stand in front of every swap**, and both leave the library untouched when they refuse:

- **Complete** — a short download never replaces a whole album.
- **Strictly better** — with the fallback ladder on, an album Deezer has no lossless master for comes
  back at 320. Swapping that in would churn files for no gain, so it is refused and the album is
  snoozed for six months (`Deezer has nothing better`) rather than retried on every sync.

Album quality itself comes from Plex, and needs one catch-up: **Dev tools → Audio quality sweep**
reads every track once (~20–30s on a large library) to work out what format each album is in. After
that, ordinary syncs resolve new arrivals a few at a time, whatever their source.

## Notes / troubleshooting

- **Logs:** the app writes rolling logs to the `app_logs` volume (`/app/logs`) and to stdout
  (`podman logs mycelium-app-1`).
- **An album downloaded but no files appeared, or only some tracks did:** Deezer's available formats
  vary **per track**, not per album — one track can have a 320 master while the rest are 128 only.
  The app requests FLAC (`DEEZER_QUALITY=2`) and then keeps downgrading — 320kbps, then 128 — while
  the album is short of Deezer's own track count, merging results per track so lossless is never
  downgraded. The ladder is derived from `DEEZER_QUALITY`, so `DEEZER_FALLBACK_QUALITY` should stay
  unset unless you want to *stop* it downgrading (set it blank to accept incomplete albums instead
  of 128kbps files). Grep the log for `PARTIAL` or `No tracks downloaded`; both now include streamrip's
  output, which names each failing track and why. Each attempt stages under
  `/music/.mycelium-incoming/` and only promotes verified files, so a failed grab leaves the
  library untouched; a leftover directory there means the app was killed mid-download and is safe
  to delete.
- **"Deezer login expired" on the Download page / `AuthenticationError` in the log:** the ARL is
  rejected, not missing — Deezer invalidates it when the session ends, the password changes, or it
  simply ages out. Nothing downloads until it's replaced, so the app raises one banner on the
  Download panel instead of letting each album fail with a generic "couldn't grab this".
  **Fix it from the app:** the banner carries a paste box, with a "Where do I find the ARL?"
  walkthrough. Copy a new `arl` cookie from a logged-in Deezer session in a browser
  (DevTools → Application → Cookies → `deezer.com` → `arl`), paste it, Save. The app checks the token
  against Deezer before writing it, so an unusable one is refused rather than saved; on success it
  rewrites just the `arl` key in `config.toml` (every other setting and comment is left byte-for-byte
  alone), names the account it signed in as, and returns the blocked albums to the queue. `rip` reads
  its config per invocation, so nothing restarts. Editing
  `STREAMRIP_CONFIG_HOST/streamrip/config.toml` by hand still works, and is the fallback if the
  config volume is mounted read-only.
  There is no way around the periodic refresh: streamrip 2.1.0's Deezer client authenticates by ARL
  only, and the underlying `deezer-py` login by email/password needs a reCAPTCHA token, so it can't
  be automated. A failed pass here skips the remaining quality fallbacks — they would reproduce the
  identical failure — so the log carries one traceback per album rather than three.
- **`Cannot connect to host e-cdns-proxy-N.dzcdn.net` in the log:** expected, and handled. Deezer
  retired that CDN, and streamrip 2.1.0 still builds a URL on it whenever the media API returns no
  URL for a track at the requested format. That track can only be recovered by retrying it at a
  lower quality, which is exactly what the fallback chain above does — so these errors are normal
  on the higher-quality passes and harmless as long as the album ends up complete.
- **streamrip is pinned** (`streamrip==2.1.0` in the `Dockerfile`) because the downloader
  compensates for release-specific behaviour: it exits 0 even when every track failed, and has no
  per-track quality downgrade. Re-check `StreamripDownloader` when bumping it.
- **External Mongo:** point `MONGO_URI` at an existing instance and remove the bundled `mongo`
  service.
- **Rebuild after code changes:** redeploy the Stack in Komodo (it rebuilds the image), or
  `podman compose build && podman compose up -d`.
