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

## Notes / troubleshooting

- **Logs:** the app writes rolling logs to the `app_logs` volume (`/app/logs`) and to stdout
  (`podman logs mycelium-app-1`).
- **An album downloaded but no files appeared:** Deezer has no lossless for it. The app requests
  FLAC (`DEEZER_QUALITY=2`), re-runs anything short at `DEEZER_FALLBACK_QUALITY=1` (320kbps MP3),
  and merges the two per track, so this now resolves itself — grep the log for `PARTIAL` or
  `No tracks downloaded` to see what a grab actually landed. Each attempt stages under
  `/music/.mycelium-incoming/` and only promotes verified files, so a failed grab leaves the
  library untouched; a leftover directory there means the app was killed mid-download and is safe
  to delete.
- **streamrip is pinned** (`streamrip==2.1.0` in the `Dockerfile`) because the downloader
  compensates for release-specific behaviour: it exits 0 even when every track failed, and has no
  per-track quality downgrade. Re-check `StreamripDownloader` when bumping it.
- **External Mongo:** point `MONGO_URI` at an existing instance and remove the bundled `mongo`
  service.
- **Rebuild after code changes:** redeploy the Stack in Komodo (it rebuilds the image), or
  `podman compose build && podman compose up -d`.
