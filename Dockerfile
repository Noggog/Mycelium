# syntax=docker/dockerfile:1
#
# Mycelium — single image that serves the API *and* the built SPA on one HTTP port (8080).
# The Aspire AppHost is dev-only; here we run Mycelium.Backend.dll directly with settings from
# env vars (see compose.yaml / .env). streamrip is baked in because the download path shells out to
# the `rip` binary locally (there is no remote downloader API).

# ---- build the SPA ----
FROM node:20-alpine AS web
WORKDIR /web
COPY src/Mycelium.Web/package.json src/Mycelium.Web/package-lock.json ./
RUN npm ci
COPY src/Mycelium.Web/ ./
# `npm run build` == `tsc && vite build`; emits static assets to /web/dist.
RUN npm run build

# ---- build the backend ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
# Publish only the backend project graph (Deezer, MongoDB, Plex, Interfaces, ServiceDefaults).
# Building the whole solution would pull the Aspire AppHost, which needs the Aspire workload SDK.
COPY src/ ./src/
RUN dotnet publish src/Mycelium.Backend/Mycelium.Backend.csproj -c Release -o /app

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# streamrip (Deezer downloader, https://github.com/nathom/streamrip) lives in an isolated venv so
# it doesn't collide with Debian's externally-managed system Python (PEP 668). ffmpeg is used by
# streamrip for codec conversion/tagging.
# Pinned: StreamripDownloader compensates for behaviour specific to this release (it exits 0 even
# when every track failed, and has no per-track quality downgrade — that landed on streamrip's dev
# branch after 2.1.0). An unpinned install would change the download path on any image rebuild, so
# bump this deliberately and re-check StreamripDownloader's verification when you do.
# git is here for the metadata archive: the app commits a nightly snapshot of the data we own
# (taste, acquisitions, identity pins) into a repository on a mounted volume. A real git rather than
# an embedded library, so the archive stays an ordinary repo you can walk into and operate by hand.
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-venv ffmpeg ca-certificates git \
    && python3 -m venv /opt/streamrip \
    && /opt/streamrip/bin/pip install --no-cache-dir streamrip==2.1.0 \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# STREAMRIP_BIN: where the backend finds `rip`. XDG_CONFIG_HOME: where streamrip reads its config
# (the Deezer ARL lives in /config/streamrip/config.toml — a mounted volume, not in the image).
# ASPNETCORE_HTTP_PORTS pins Kestrel to 8080.
# The paths below are the container's own and are fixed here rather than asked for in compose: what
# gets mounted at each is the deployment's business, and the app has no reason to care. A deployment
# only has to bind a directory to the mount point.
#   METADATA_REPO_PATH  the metadata archive's git repository. Declaring it here also keeps local dev
#                       (which doesn't use this image) archive-free, since the backend reads an unset
#                       path as "archiving off".
#   MUSIC_DOWNLOAD_DIR  where downloads land — must be the storage Plex scans.
ENV STREAMRIP_BIN=/opt/streamrip/bin/rip \
    XDG_CONFIG_HOME=/config \
    METADATA_REPO_PATH=/archive \
    MUSIC_DOWNLOAD_DIR=/music \
    ASPNETCORE_HTTP_PORTS=8080
RUN mkdir -p /config /music /app/logs /archive

COPY --from=build /app ./
# The SPA is served as static files from wwwroot (see Program.cs UseStaticFiles + MapFallbackToFile).
COPY --from=web /web/dist ./wwwroot

EXPOSE 8080
ENTRYPOINT ["dotnet", "Mycelium.Backend.dll"]
