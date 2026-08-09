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
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-venv ffmpeg ca-certificates \
    && python3 -m venv /opt/streamrip \
    && /opt/streamrip/bin/pip install --no-cache-dir streamrip==2.1.0 \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# STREAMRIP_BIN: where the backend finds `rip`. XDG_CONFIG_HOME: where streamrip reads its config
# (the Deezer ARL lives in /config/streamrip/config.toml — a mounted volume, not in the image).
# ASPNETCORE_HTTP_PORTS pins Kestrel to 8080.
ENV STREAMRIP_BIN=/opt/streamrip/bin/rip \
    XDG_CONFIG_HOME=/config \
    ASPNETCORE_HTTP_PORTS=8080
RUN mkdir -p /config /music /app/logs

COPY --from=build /app ./
# The SPA is served as static files from wwwroot (see Program.cs UseStaticFiles + MapFallbackToFile).
COPY --from=web /web/dist ./wwwroot

EXPOSE 8080
ENTRYPOINT ["dotnet", "Mycelium.Backend.dll"]
