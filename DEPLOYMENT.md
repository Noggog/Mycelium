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

Required: `PUBLIC_ORIGIN`, the three `OIDC_*` values, `PLEX_ENDPOINT`,
`MUSIC_DOWNLOAD_DIR_HOST`, `STREAMRIP_CONFIG_HOST`.

There is no Plex token to set. Once the stack is up, sign in and open **Dev tools → Plex connection →
Link with Plex**: approving in the browser stores a server-scoped token in Mongo. Do this as the
**server owner**, since the same credential writes the library's mood tags. The app runs unlinked
until you do — it just can't read the library.

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

## API tokens (unattended scripts)

Every `/api` route is behind the OIDC session cookie, which is fine for the SPA and useless for a
script: the cookie expires, and the run that inherits the expired one dies with a 401 that only a
human at a browser can clear. An **API token** is the same identity with a lifetime you choose.

A token authenticates **as an existing user**, not as a service account. Everything per-user keeps
working exactly as it does in the browser — the ratings it writes are that person's, the mood tags it
stamps are `<their username>_liked`, and albums it queues come down at their quality tier. There is no
"a robot did this" code path anywhere in the app, and there didn't need to be.

### Mint one

Minting needs a signed-in browser session, so the least fiddly way is the devtools console on the
app's own tab — the session cookie goes along on its own and never has to be copied anywhere:

```js
await (await fetch('/api/tokens', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ name: 'playlist acquisition', expiresInDays: 365 }),
})).json()
```

The same call with `curl`, if you'd rather — this one does need the cookie pasted, the last time
you'll have to:

```bash
curl -sS -X POST "$PUBLIC_ORIGIN/api/tokens" \
  -H 'Content-Type: application/json' \
  -b 'myc.auth=<your session cookie>' \
  -d '{"name":"playlist acquisition","expiresInDays":365}'
```

```json
{
  "id": "9f2c41ab77e0d135",
  "token": "myc_9f2c41ab77e0d135.qP7…",
  "name": "playlist acquisition",
  "subject": "…",
  "devScope": false,
  "expiresAt": "2027-08-27T10:14:03Z"
}
```

**`token` is shown once and never again.** It is not stored — only a SHA-256 of its secret half is —
so it cannot be read back, re-sent, or recovered from the database or the logs. Lose it and you mint
a new one; that is the intended repair, not a limitation to work around. Put it wherever that script
keeps its secrets.

- `name` — a label for the revoke list. Say which script it's for.
- `expiresInDays` — optional. Omit for "until revoked". A schedule nobody watches is exactly where an
  unannounced expiry recreates the problem this feature exists to end, so there is no default expiry.
- `dev` — see **Dev scope** below. Off unless asked for.

Minting requires an interactive browser session. A token **cannot mint another token or revoke one**,
by design: that keeps a leaked token to a fixed blast radius and a fixed lifetime, rather than a
foothold that can reissue itself while you're revoking. Rotation is a thing a person does.

### Use one

```bash
curl -H "Authorization: Bearer $MYCELIUM_TOKEN" "$PUBLIC_ORIGIN/api/artists"
```

`Authorization: Bearer` is the header to use. If your reverse proxy has claimed `Authorization` for
its own handshake — an Authentik forward-auth outpost in front of this app may — the token is also
read from an app-specific header, which nothing else will touch:

```bash
curl -H "X-Mycelium-Token: $MYCELIUM_TOKEN" "$PUBLIC_ORIGIN/api/artists"
```

`GET /auth/me` is the one call to make first: it answers with the subject, username and quality tier
the token resolves to, plus `viaApiToken: true`. If that returns what you expect, everything under
`/api` will act as that user.

An invalid, revoked or expired token is always a plain **401** — never a redirect, never a 500. The
body says nothing about *why*; the app's log names the token's **id** (never its value) and the
reason, so `podman logs mycelium-app-1 | grep 'Rejected an API token'` tells you which of your tokens
stopped working and whether it was revoked, expired or never existed.

### Revoke one

```bash
curl -sS "$PUBLIC_ORIGIN/api/tokens" -b 'myc.auth=<your session cookie>'          # list yours
curl -sS -X DELETE "$PUBLIC_ORIGIN/api/tokens/9f2c41ab77e0d135" -b 'myc.auth=…'  # revoke by id
```

Revocation takes effect on the token's next request — nothing caches a verification. The row is kept
rather than deleted, so an id in an old log line still resolves to something. You only ever see and
revoke **your own** tokens; there is no cross-user revoke, so removing a departed maintainer means
dropping them from `DEV_USERNAMES` and, if you want their tokens dead, deleting their rows from the
`apiTokens` collection in Mongo by hand.

### Dev scope

The dev endpoints include destructive maintenance — `POST /api/dev/plex-tags/clear` strips every
`_liked`/`_disliked` tag off the entire library. A token does **not** reach them just because its
owner is listed in `DEV_USERNAMES`. Dev scope is opt-in at creation:

```bash
curl -sS -X POST "$PUBLIC_ORIGIN/api/tokens" -H 'Content-Type: application/json' -b 'myc.auth=…' \
  -d '{"name":"tag rebuild","dev":true,"expiresInDays":7}'
```

Only a dev user, signed in at a browser, can grant it, and it is a *ceiling* rather than a grant:
dropping a username out of `DEV_USERNAMES` takes their tokens' dev access with it, so revoking a
maintainer doesn't mean hunting down every token they ever minted. Give ordinary automation the
default (no dev scope) and mint a short-lived dev-scoped token for the run that actually needs one.

### Notes

- Tokens live in the `apiTokens` collection in Mongo, hashed. Unlike the Deezer ARL and the Plex
  server token — both of which are stored in the clear because the app has to *replay* them to a
  third party — this one is only ever *checked*, so the plaintext is never kept. A dump of that
  collection hands an attacker nothing they can present.
- A token for an account with no `preferred_username` is refused at creation. The Plex mood tags are
  built from that username and are skipped silently without one, so such a token would rate happily
  and tag nothing for weeks before anyone noticed. Sign in to the app once as that user first.
- Nothing about the browser session changed. The SPA still uses the cookie; the token is a second,
  parallel way in.

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
