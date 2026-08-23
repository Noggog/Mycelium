using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Mycelium.Backend;
using Mycelium.Backend.Services.Auth;
using Mycelium.Backend.Services.Background;
using Mycelium.Backend.Services.Download;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Write logs to a rolling file under the backend's content root (logs/backend-<date>.log,
// gitignored) so failures are inspectable without watching the live console. writeToProviders
// keeps the existing OpenTelemetry logging from ServiceDefaults intact.
builder.Services.AddSerilog((_, lc) => lc
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(builder.Environment.ContentRootPath, "logs", "backend-.log"),
        rollingInterval: RollingInterval.Day,
        shared: true),
    writeToProviders: true);

// Use Redis when a "cache" connection string is provided (the Aspire AppHost injects one);
// otherwise fall back to an in-memory cache so the backend can run standalone in local dev.
if (builder.Configuration.GetConnectionString("cache") is not null)
{
    builder.AddRedisDistributedCache("cache");
}
else
{
    builder.Services.AddDistributedMemoryCache();
}

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Allow the React SPA to call the API directly (the Vite dev proxy keeps things same-origin in
// dev, so this is mainly a fallback / for running the SPA outside the proxy).
const string spaCorsPolicy = "spa";
builder.Services.AddCors(options =>
{
    options.AddPolicy(spaCorsPolicy, policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

// Syncs the artist catalog (artists + their albums) from Plex on startup, then daily.
builder.Services.AddHostedService<CatalogSyncService>();

// Diffs each owned artist's Deezer discography against the library to find missing albums; runs
// shortly after startup (so the catalog is populated first), then daily.
builder.Services.AddHostedService<AlbumSyncService>();

// Periodically tops up each user's recommendation queue (additive — grows the frontier and refreshes
// stale similarity edges without clearing pending). Cadence via QUEUE_REPLENISH_INTERVAL_HOURS.
builder.Services.AddHostedService<QueueReplenishService>();

// Re-reads the Plex song ratings for every thumbed artist and flags the ones the ratings contradict,
// feeding the "second chance" (well-rated dislikes) and "second thoughts" (poorly-rated likes)
// discovery categories. Slow by design (weekly, via RECONSIDER_SWEEP_INTERVAL_DAYS) — it exists to
// re-litigate verdicts made years ago.
builder.Services.AddHostedService<ReconsiderSweepService>();

// The Deezer download engine (DownloadService) is registered in MainModule as a shared singleton
// hosted service, so the "download now" endpoint and the drainer loop are the same instance.

// BFF auth: cookie session + OIDC (Keycloak) code flow. See BffAuthentication.
builder.AddBffAuthentication();

builder.Host.RegisterAutofacModule<MainModule>();

var app = builder.Build();

app.UseExceptionHandler();

// One log line per HTTP request (method, path, status, duration) — the fastest way to
// spot which endpoint failed and with what status.
app.UseSerilogRequestLogging();

// Serve the built SPA (production: the Vite build is copied to wwwroot in the image). No-op in
// local dev, where Vite serves the SPA itself and proxies /api + /auth to this backend.
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(spaCorsPolicy);

app.UseAuthentication();
app.UseAuthorization();

// --- BFF auth endpoints (reached by the browser through the Vite proxy, verbatim) ---
// Start login: triggers the OIDC challenge, returning to a local returnUrl afterward.
app.MapGet("/auth/login", (string? returnUrl) =>
    {
        // Only allow local return paths (no open redirect).
        var target = returnUrl is not null && returnUrl.StartsWith('/') ? returnUrl : "/";
        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = target },
            new[] { OpenIdConnectDefaults.AuthenticationScheme });
    })
    .WithName("Login");

// Sign out of both the local cookie and the IdP session.
app.MapGet("/auth/logout", () =>
        Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            new[] { CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme }))
    .WithName("Logout");

// Current user (200 with profile, or 401 if not signed in) — the SPA polls this to know auth state.
app.MapGet("/auth/me", (HttpContext http, DevUsers devUsers, ILoggerFactory loggerFactory) =>
    {
        var user = http.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }

        var isDev = devUsers.Includes(user);

        // Diagnostic: dump every claim and the dev-match decision so we can see exactly what the IdP
        // sends (e.g. whether preferred_username is present, and under what value). Remove once the
        // DEV_USERNAMES match is confirmed.
        /*var log = loggerFactory.CreateLogger("AuthMe");
        log.LogInformation(
            "auth/me claims: [{Claims}]; preferred_username={Username}; DEV_USERNAMES=[{DevUsers}]; isDev={IsDev}",
            string.Join(", ", user.Claims.Select(c => $"{c.Type}={c.Value}")),
            user.FindFirst("preferred_username")?.Value ?? "(none)",
            string.Join(", ", devUsers.Configured),
            isDev);*/

        return Results.Ok(new
        {
            subject = user.GetSubject(),
            username = user.FindFirst("preferred_username")?.Value,
            email = user.FindFirst("email")?.Value,
            displayName = user.FindFirst("name")?.Value,
            // Drives the in-app dev panel's visibility (DEV_USERNAMES). The dev endpoints enforce the
            // same check server-side, so this only governs what the UI bothers to show.
            isDev,
        });
    })
    .WithName("Me");

// Application API, grouped under /api so it shares the origin with the SPA without the SPA's
// client routes (/artists, /related, /purchases, ...) colliding with same-named API paths.
var api = app.MapGroup("/api");

api.MapGet("/artists", (ILibraryProvider libraryProvider) =>
    {
        return libraryProvider.GetArtistList();
    })
    .WithName("GetArtists");

// Pin a library artist to a specific Deezer artist id — the fix for a misassociation (e.g. a
// common name like "Alex" resolving to the wrong, more popular act). Stores a sticky override and
// force-refreshes that artist's similarity edges so the graph re-derives from the correct id, then
// rebuilds the caller's recommendation queue so candidates from the old (wrong) edges drop off
// immediately rather than lingering until the next manual rebuild.
// Auth-gated: this is a maintainer correction. artist is a query param so '/' in names works.
api.MapPost("/artists/deezer-id", async (HttpContext http, string artist, long id,
        DeezerArtistResolver resolver, RelatedArtistInteractor interactor, DiscoveryEngine engine) =>
    {
        var identity = await resolver.SetOverride(artist, id);
        if (identity is null)
        {
            return Results.NotFound();
        }

        await interactor.GetRelated(new ArtistKey(artist), forceRefresh: true);
        await engine.Rebuild(http.User.GetSubject()!);
        return Results.Ok(identity);
    })
    .RequireAuthorization()
    .WithName("SetArtistDeezerId");

// Clear a Deezer override so the artist re-resolves from a name search next time. Auth-gated.
api.MapDelete("/artists/deezer-id", async (string artist, DeezerArtistResolver resolver) =>
    {
        await resolver.ClearOverride(artist);
        return Results.NoContent();
    })
    .RequireAuthorization()
    .WithName("ClearArtistDeezerId");

// Free-text Deezer artist search powering the "Correct association" picker: candidate artists
// (id, name, fans, link, photo) in relevance order. Public Deezer metadata, so no auth.
api.MapGet("/deezer/search", async (string q, int? limit, DeezerArtistResolver resolver) =>
        Results.Ok(await resolver.SearchArtists(q, Math.Clamp(limit ?? 10, 1, 25))))
    .WithName("SearchDeezerArtists");

// ---- Cross-source identity ("Sources" tab): one set of generic routes over every registered
// ISourceIdentityCorrector (deezer, musicbrainz, …), dispatched by the {source} path segment. ----

// Every source's resolved identity (id + link-out + override flag) for one artist, for the tab.
api.MapGet("/artists/sources", async (string artist, ArtistSourcesService sources) =>
        Results.Ok(await sources.Get(new ArtistKey(artist))))
    .RequireAuthorization()
    .WithName("GetArtistSources");

// Every library source's presence + open-in deep links for one artist, for the "Library" tab.
api.MapGet("/artists/libraries", async (string artist, LibrarySourcesService libraries) =>
        Results.Ok(await libraries.Get(new ArtistKey(artist))))
    .RequireAuthorization()
    .WithName("GetArtistLibraries");

// The user's per-song Plex rating summary (highest / lowest / average, 0–5 stars) for one artist,
// shown in the discovery readout. Present=false for artists not in Plex (nothing to show).
api.MapGet("/artists/ratings", async (string artist, ArtistRatingStatsService ratings) =>
        Results.Ok(await ratings.Get(new ArtistKey(artist))))
    .RequireAuthorization()
    .WithName("GetArtistRatings");

// The editable Plex descriptor tags (genres / styles / moods) for one artist, for the Browse page's
// "Tags" tab. Reads live from Plex — those fields are where smart collections look — and hides the
// app's own like/dislike verdict moods. present=false for artists that aren't in the library.
api.MapGet("/artists/tags", async (string artist, ArtistTagsService tags) =>
        Results.Ok(await tags.Get(new ArtistKey(artist))))
    .RequireAuthorization()
    .WithName("GetArtistTags");

// Add and/or remove one tag on one field (genre|style|mood) of an artist, returning the artist's tags
// as they now stand. The write is a delta, so the field's other tags — including the verdict moods the
// tab never shows — are untouched. Auth-gated: this edits the shared Plex library.
api.MapPost("/artists/tags", async (string artist, string field, string? add, string? remove,
        ArtistTagsService tags) =>
    {
        try
        {
            return Results.Ok(await tags.Update(
                new ArtistKey(artist), field,
                add is null ? Array.Empty<string>() : new[] { add },
                remove is null ? Array.Empty<string>() : new[] { remove }));
        }
        catch (ArgumentException ex)
        {
            // Unknown field, or an attempt to touch a "<user>_liked"/"_disliked" verdict tag.
            return Results.BadRequest(new { error = ex.Message });
        }
    })
    .RequireAuthorization()
    .WithName("EditArtistTags");

// Free-text candidate search within one source, powering that source's "Correct association" picker.
api.MapGet("/sources/{source}/search",
        async (string source, string q, int? limit, IEnumerable<ISourceIdentityCorrector> correctors) =>
        {
            var corrector = correctors.FirstOrDefault(c => c.Source == source);
            return corrector is null
                ? Results.NotFound()
                : Results.Ok(await corrector.Search(q, Math.Clamp(limit ?? 10, 1, 25)));
        })
    .RequireAuthorization()
    .WithName("SearchSource");

// Pin an artist to a specific id on one source (sticky override), then re-derive that artist's
// similarity edges from the corrected ids and rebuild the caller's queue so the old (wrong) edges
// drop off immediately — mirrors the original Deezer-pin behaviour, now source-generic.
api.MapPost("/artists/sources/{source}",
        async (HttpContext http, string source, string artist, string id,
            IEnumerable<ISourceIdentityCorrector> correctors,
            RelatedArtistInteractor interactor, DiscoveryEngine engine) =>
        {
            var corrector = correctors.FirstOrDefault(c => c.Source == source);
            if (corrector is null) return Results.NotFound();

            var identity = await corrector.Pin(new ArtistKey(artist), id);
            if (identity is null) return Results.NotFound();

            await interactor.GetRelated(new ArtistKey(artist), forceRefresh: true);
            await engine.Rebuild(http.User.GetSubject()!);
            return Results.Ok(identity);
        })
    .RequireAuthorization()
    .WithName("PinArtistSource");

// Clear a source's pin (or unlinked flag) so the artist re-resolves from a name search next time,
// then re-derive its similarity edges and rebuild the queue — the same refresh a pin does, so a
// reset doesn't leave stale edges from the old (pinned/detached) identity behind.
api.MapDelete("/artists/sources/{source}",
        async (HttpContext http, string source, string artist,
            IEnumerable<ISourceIdentityCorrector> correctors,
            RelatedArtistInteractor interactor, DiscoveryEngine engine) =>
        {
            var corrector = correctors.FirstOrDefault(c => c.Source == source);
            if (corrector is null) return Results.NotFound();

            await corrector.Clear(new ArtistKey(artist));
            await interactor.GetRelated(new ArtistKey(artist), forceRefresh: true);
            await engine.Rebuild(http.User.GetSubject()!);
            return Results.NoContent();
        })
    .RequireAuthorization()
    .WithName("ClearArtistSource");

// Stickily detach an artist from a source (it has no match there). Wipes the artist's stored
// similarity edges first — a detached source resolves to null and so won't overwrite the old (wrong)
// edges on its own — then re-derives from whatever sources remain linked and rebuilds the queue.
api.MapPost("/artists/sources/{source}/unlink",
        async (HttpContext http, string source, string artist,
            IEnumerable<ISourceIdentityCorrector> correctors,
            IRelatedArtistRepo relatedRepo, RelatedArtistInteractor interactor, DiscoveryEngine engine) =>
        {
            var corrector = correctors.FirstOrDefault(c => c.Source == source);
            if (corrector is null) return Results.NotFound();

            await corrector.Unlink(new ArtistKey(artist));
            await relatedRepo.DeleteAllSources(new ArtistKey(artist));
            await interactor.GetRelated(new ArtistKey(artist), forceRefresh: true);
            await engine.Rebuild(http.User.GetSubject()!);
            return Results.NoContent();
        })
    .RequireAuthorization()
    .WithName("UnlinkArtistSource");

// Backfill the Deezer identity for every present artist (id/name/fans/link/photo) into the catalog
// so the Artists page can flag misassociations. Heavy (one lookup per artist), so it's a one-shot
// maintenance trigger; afterwards ids are captured opportunistically as artists are sampled/rated.
api.MapPost("/artists/deezer/resolve-all", async (ILibraryProvider library, DeezerArtistResolver resolver) =>
    {
        var artists = await library.GetAllArtistMetadata();
        var resolved = 0;
        foreach (var a in artists)
        {
            if (await resolver.ResolveIdentity(a.ArtistKey.ArtistName) != null) resolved++;
        }
        return Results.Ok(new { total = artists.Length, resolved });
    })
    .RequireAuthorization()
    .WithName("ResolveAllDeezer");

// The Library Catalog sync job: pull the artist list from Plex into the local catalog.
// Daily reads (GET /artists) serve from that catalog, so this is the only Plex-touching path.
api.MapPost("/catalog/refresh", (CatalogRefresher refresher) =>
    {
        return refresher.Refresh();
    })
    .WithName("RefreshCatalog");

// Maintenance: clean up Plex's ';'-joined multi-artist names (e.g. "Nina Simone;Hot Chip") that
// leaked into the catalog and user ratings before ingestion-time splitting. GET previews the work;
// POST resolves it (splits catalog docs, re-attributes ratings). Auth-gated — the maintainer's tool.
api.MapGet("/maintenance/combined-artists", async (LibraryCleanupService cleanup) =>
        Results.Ok(await cleanup.Scan()))
    .RequireAuthorization()
    .WithName("ScanCombinedArtists");

api.MapPost("/maintenance/combined-artists/resolve", async (LibraryCleanupService cleanup) =>
        Results.Ok(await cleanup.Resolve()))
    .RequireAuthorization()
    .WithName("ResolveCombinedArtists");

// Related artists, unified across every similarity source. Ingests from Deezer on a cache
// miss/stale entry (persisting into the graph); pass ?refresh=true to force a re-fetch.
// The artist is a query param (not a path segment) so names with '/' (e.g. "AC/DC") work —
// an encoded slash in a path segment is rejected by ASP.NET routing by default.
api.MapGet("/related", async (string artist, bool? refresh,
        RelatedArtistInteractor interactor, ArtistMetaEnricher meta) =>
    {
        var relations = await interactor.GetRelated(new ArtistKey(artist), forceRefresh: refresh ?? false);
        // Resolve images (and future cross-source meta) for every recommended artist, not just those
        // a source that carries images happened to recommend — so ListenBrainz-only picks aren't blank.
        return Results.Ok(await meta.EnrichImages(relations));
    })
    .WithName("GetRelated");

// Deezer play info for an artist: a 30-second preview MP3 to sample plus the deezer.com artist
// link. The SPA plays the preview in a plain <audio> (no login/cookies, unlike the embed widget).
// Public Deezer metadata, so no auth; cached server-side.
// `fresh=true` bypasses the server cache to re-mint preview urls — the client sends it to retry a
// preview whose signed url expired while the readout sat open (Deezer's tokens live ~15 minutes).
api.MapGet("/deezer/artist", async (string artist, DeezerArtistResolver resolver, bool? fresh) =>
    {
        var info = await resolver.ResolvePlayInfo(artist, fresh ?? false);
        return info is null ? Results.NotFound() : Results.Ok(info);
    })
    .WithName("ResolveDeezerArtist");

// Deezer play info for a specific album id: its previewable tracks plus the deezer.com album link.
// Used to sample "missing album" cards. Public Deezer metadata, so no auth; cached server-side.
api.MapGet("/deezer/album", async (long id, DeezerArtistResolver resolver, bool? fresh) =>
        Results.Ok(await resolver.ResolveAlbumPlayInfo(id, fresh ?? false)))
    .WithName("ResolveDeezerAlbum");

// The missing-album sync job (Deezer discography diff per owned artist). Heavy, so it's a dev-only
// manual trigger; in production it runs on the daily AlbumSyncService schedule.
api.MapPost("/albums/missing/refresh", (MissingAlbumRefresher refresher) =>
    {
        return refresher.Refresh();
    })
    .WithName("RefreshMissingAlbums");

// --- Discovery: the per-user feed + ratings over the similarity graph (DiscoveryEngine) ---
// All require an authenticated user. artist/album are query params (handles '/' in names),
// pageSize is clamped to keep paging sane.

// A paged feed section for one category: RecommendedArtist | LibraryArtist | MissingAlbum.
api.MapGet("/discovery", async (HttpContext http, DiscoveryEngine engine, string? kind, int? page, int? pageSize) =>
    {
        var feedKind = Enum.TryParse<FeedKind>(kind, ignoreCase: true, out var parsed)
            ? parsed
            : FeedKind.RecommendedArtist;
        return Results.Ok(await engine.GetFeed(
            http.User.GetSubject()!, feedKind, Math.Max(page ?? 0, 0), Math.Clamp(pageSize ?? 20, 1, 100)));
    })
    .RequireAuthorization()
    .WithName("GetDiscoveryFeed");

// A single mixed feed across the selected categories (comma-separated `kinds`), round-robin
// interleaved + shuffled by `seed` so the order is stable across pages. This is what the Discover
// page uses; the per-kind endpoint above remains for any single-category view.
api.MapGet("/discovery/mixed", async (
        HttpContext http, DiscoveryEngine engine, ArtistMetaEnricher meta,
        string? kinds, int? page, int? pageSize, int? seed) =>
    {
        var requested = (kinds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => Enum.TryParse<FeedKind>(k, ignoreCase: true, out var fk) ? (FeedKind?)fk : null)
            .Where(k => k.HasValue)
            .Select(k => k!.Value)
            .Distinct()
            .ToArray();
        if (requested.Length == 0)
        {
            requested = new[]
            {
                FeedKind.RecommendedArtist, FeedKind.MissingAlbum,
                FeedKind.RecommendedLibraryArtist, FeedKind.SeedLibraryArtist,
                FeedKind.ReconsiderArtist, FeedKind.SecondThoughtsArtist,
            };
        }
        var feed = await engine.GetMixedFeed(
            http.User.GetSubject()!, requested,
            Math.Max(page ?? 0, 0), Math.Clamp(pageSize ?? 20, 1, 100), seed ?? 0);
        // Same cross-source meta pipeline as /related: fill images for the page's artist cards
        // (bounded to the page size) regardless of which source recommended them.
        return Results.Ok(feed with { Items = await meta.EnrichImages(feed.Items) });
    })
    .RequireAuthorization()
    .WithName("GetMixedDiscoveryFeed");

// Dev-panel "secret" op: rebuild the pending recommendations for every user from their liked
// artists (keeps ratings). Gated to dev accounts since it sweeps the whole user base.
api.MapPost("/discovery/refresh", async (DiscoveryEngine engine) =>
    {
        var rebuilt = await engine.RebuildAll();
        return Results.Ok(new { rebuilt });
    })
    .RequireAuthorization("DevUser")
    .WithName("RefreshDiscoveryQueue");

// Rate an artist or (when album is supplied) a missing album. verdict = "up" (Liked) | "down" (Disliked).
api.MapPost("/discovery/rate", async (
        string artist, string? album, string? albumArt, string verdict,
        HttpContext http, DiscoveryEngine engine, IArtistTagger tagger) =>
    {
        var status = verdict.Equals("up", StringComparison.OrdinalIgnoreCase)
            ? DiscoveryStatus.Liked
            : DiscoveryStatus.Disliked;
        var userId = http.User.GetSubject()!;
        if (string.IsNullOrEmpty(album))
        {
            await engine.RateArtist(userId, artist, status);
            // Mirror the verdict into Plex as a per-user mood tag ("<username>_liked"/"_disliked"), which
            // a music smart playlist can filter on via "Artist Mood". Stamp the new verdict and strip the
            // opposite so the latest rating is the only tag left (a like→dislike flip drops "_liked").
            // Best-effort (never throws), so a Plex hiccup can't fail the rating.
            var username = http.User.FindFirst("preferred_username")?.Value;
            var tag = ArtistTag.For(username, status);
            if (tag != null)
            {
                var opposite = status == DiscoveryStatus.Liked ? DiscoveryStatus.Disliked : DiscoveryStatus.Liked;
                var oppositeTag = ArtistTag.For(username, opposite);
                var remove = oppositeTag != null ? new[] { oppositeTag } : Array.Empty<string>();
                await tagger.SetTags(artist, tag, remove);
            }
        }
        else
        {
            await engine.RateAlbum(userId, artist, album, albumArt, status);
        }
        return Results.NoContent();
    })
    .RequireAuthorization()
    .WithName("RateCandidate");

// Ad-hoc seed: add an artist that no one in the library recommends yet (so it never surfaces in the
// feed) straight from a source search. Pins the user's *chosen* candidate by id (honouring their
// disambiguation rather than re-guessing the name), which captures the identity into the catalog, then
// likes it — growing the frontier from it and queuing it to buy, exactly as thumbing a recommendation
// does. The like is what seeds it into discovery; the pin just makes the right Deezer artist drive that
// expansion. Mirrors the Plex tagging of /discovery/rate (best-effort; the artist may not be in Plex).
api.MapPost("/discovery/seed", async (
        string source, string artist, string id,
        HttpContext http, IEnumerable<ISourceIdentityCorrector> correctors,
        DiscoveryEngine engine, IArtistTagger tagger) =>
    {
        var corrector = correctors.FirstOrDefault(c => c.Source == source);
        if (corrector is null) return Results.NotFound();

        var identity = await corrector.Pin(new ArtistKey(artist), id);
        if (identity is null) return Results.NotFound();

        var userId = http.User.GetSubject()!;
        await engine.RateArtist(userId, artist, DiscoveryStatus.Liked);

        var username = http.User.FindFirst("preferred_username")?.Value;
        var tag = ArtistTag.For(username, DiscoveryStatus.Liked);
        if (tag != null)
        {
            var dislikeTag = ArtistTag.For(username, DiscoveryStatus.Disliked);
            var remove = dislikeTag != null ? new[] { dislikeTag } : Array.Empty<string>();
            await tagger.SetTags(artist, tag, remove);
        }

        return Results.Ok(new { artist });
    })
    .RequireAuthorization()
    .WithName("SeedArtist");

// A liked non-owned artist's acquirable albums (their Deezer discography minus anything already
// owned), surfaced inline under the just-rated card so a fresh discovery can be acted on. Fetched
// on demand (one Deezer call) only when an artist is liked — not precomputed per feed card.
api.MapGet("/discovery/artist-albums", async (string artist, HttpContext http, DiscoveryEngine engine) =>
        Results.Ok(await engine.ArtistAlbums(http.User.GetSubject()!, artist)))
    .RequireAuthorization()
    .WithName("GetArtistAlbums");

// An owned artist's full Deezer discography for the Artists-page drill-down: every LP flagged owned
// vs. missing, missing ones overlaid with the user's verdict. One Deezer call per expand.
api.MapGet("/discovery/artist-discography", async (string artist, HttpContext http, DiscoveryEngine engine) =>
        Results.Ok(await engine.ArtistDiscography(http.User.GetSubject()!, artist)))
    .RequireAuthorization()
    .WithName("GetArtistDiscography");

// Snooze a recommendation: hide it for the chosen duration; it resurfaces when the window lapses.
// Snoozes an artist, or — when album is supplied — a missing album. duration = week | month | year
// (mapped server-side to 7 / 30 / 365 days).
api.MapPost("/discovery/snooze", async (
        string artist, string? album, string? albumArt, string duration, HttpContext http, DiscoveryEngine engine) =>
    {
        var window = duration.ToLowerInvariant() switch
        {
            "week" => TimeSpan.FromDays(7),
            "month" => TimeSpan.FromDays(30),
            "year" => TimeSpan.FromDays(365),
            _ => (TimeSpan?)null,
        };
        if (window is null)
        {
            return Results.Problem("duration must be week, month, or year.", statusCode: 400);
        }
        var userId = http.User.GetSubject()!;
        if (string.IsNullOrEmpty(album))
        {
            await engine.SnoozeArtist(userId, artist, window.Value);
        }
        else
        {
            await engine.SnoozeAlbum(userId, artist, album, albumArt, window.Value);
        }
        return Results.NoContent();
    })
    .RequireAuthorization()
    .WithName("SnoozeCandidate");

// Clear a rating, returning the artist/album to the feed.
api.MapDelete("/discovery/rate", async (
        string artist, string? album, HttpContext http, DiscoveryEngine engine, IArtistTagger tagger) =>
    {
        var userId = http.User.GetSubject()!;
        if (string.IsNullOrEmpty(album))
        {
            await engine.ClearArtistRating(userId, artist);
            // Undo the Plex tag too — a cleared verdict shouldn't leave its "<username>_liked"/
            // "_disliked" tag behind. We don't know which verdict it was, so strip both (the user holds
            // at most one); best-effort, so a Plex hiccup can't fail the clear.
            var username = http.User.FindFirst("preferred_username")?.Value;
            var tags = new[] { DiscoveryStatus.Liked, DiscoveryStatus.Disliked }
                .Select(s => ArtistTag.For(username, s))
                .OfType<string>()
                .ToArray();
            if (tags.Length > 0)
            {
                await tagger.SetTags(artist, add: null, remove: tags);
            }
        }
        else
        {
            await engine.ClearAlbumRating(userId, artist, album);
        }
        return Results.NoContent();
    })
    .RequireAuthorization()
    .WithName("ClearRating");

// Every rating the user has made, for the review page (albums that now exist are filtered out).
api.MapGet("/discovery/ratings", async (HttpContext http, DiscoveryEngine engine) =>
        Results.Ok(await engine.GetRatings(http.User.GetSubject()!)))
    .RequireAuthorization()
    .WithName("GetRatings");

// --- Dev panel: Plex tag maintenance ---
// Wipe and/or rebuild the per-user like/dislike mood tags so we can iterate on the tagging logic
// without leaving orphaned tags scattered across the library. Gated by the "DevUser" policy
// (DEV_USERNAMES) — the clear is destructive (it nukes every "_liked"/"_disliked" tag), so these
// stay restricted to dev users rather than any signed-in user.
var dev = api.MapGroup("/dev/plex-tags").RequireAuthorization("DevUser");

// Strip every managed tag from every artist (clean slate).
dev.MapPost("/clear", async (PlexTagMaintenance maint) =>
        Results.Ok(new { cleared = await maint.ClearManagedTags() }))
    .WithName("DevClearPlexTags");

// Reapply tags from every user's stored ratings (additive; doesn't remove stale ones).
dev.MapPost("/reapply", async (PlexTagMaintenance maint) =>
        Results.Ok(new { applied = await maint.ReapplyFromRatings() }))
    .WithName("DevReapplyPlexTags");

// Nuke then reapply — the full reset.
dev.MapPost("/rebuild", async (PlexTagMaintenance maint) =>
    {
        var result = await maint.Rebuild();
        return Results.Ok(new { cleared = result.Cleared, applied = result.Applied });
    })
    .WithName("DevRebuildPlexTags");

// Whole-library similarity warm: force-populate every source's edges (Deezer + ListenBrainz) across
// the entire catalog, instead of waiting for the lazy, usage-driven path. Long-running (bounded by
// MusicBrainz's ~1 req/s), so it runs as a single-flight background job: POST kicks it off and
// returns the live status; GET polls progress. Same DevUser gate as the tag tools.
var devSim = api.MapGroup("/dev/similarity").RequireAuthorization("DevUser");

devSim.MapPost("/warm", (SimilarityGraphWarmer warmer, bool? force) =>
        Results.Ok(warmer.Start(force ?? false)))
    .WithName("DevWarmSimilarity");

devSim.MapGet("/warm", (SimilarityGraphWarmer warmer) =>
        Results.Ok(warmer.GetStatus()))
    .WithName("DevSimilarityWarmStatus");

// The shared "to buy" list: every user's liked non-owned artists + liked albums not yet acquired,
// persisted with a status (pending → sent → in-library). Reconciles on read so it's always current.
// Auth-gated, but not scoped to the caller — this is the library maintainer's unified queue.
api.MapGet("/purchases", async (PurchaseService purchases) =>
        Results.Ok(await purchases.GetActive()))
    .RequireAuthorization()
    .WithName("GetPurchases");

// A live snapshot of the download subsystem for the monitoring panel (backend, throttle, counts,
// what's downloading now). Cheap; polled by the UI.
api.MapGet("/purchases/status", async (PurchaseService purchases) =>
        Results.Ok(await purchases.GetDownloadSnapshot()))
    .RequireAuthorization()
    .WithName("DownloadStatus");

// The automatic/manual switch for the background drainer. Persisted in Mongo so it survives a
// redeploy — this switch is the only place the mode is set, no env var — and re-read by the drainer
// each tick, so it takes effect without a restart.
api.MapPost("/purchases/automatic", async (bool automatic, DownloadSettings settings) =>
    {
        await settings.SetAutomatic(automatic);
        return Results.NoContent();
    })
    .RequireAuthorization()
    .WithName("SetDownloadsAutomatic");

// Manually queue an item for download now (the "Download now"/"Retry" button). Non-blocking — the
// drainer does the fetch; returns immediately. Works whether or not automatic downloads are on.
api.MapPost("/purchases/download", async (string id, DownloadService downloads) =>
        await downloads.RequestDownload(id)
            ? Results.NoContent()
            : Results.Problem("Item isn't a downloadable Deezer album.", statusCode: 409))
    .RequireAuthorization()
    .WithName("DownloadPurchase");

// Replace the Deezer ARL that streamrip authenticates with. The token expires on its own and is the
// only credential streamrip's Deezer client accepts, so this turns a recurring SSH-and-edit-TOML
// chore into a paste on the page that reported the problem. POST-only and never echoed back: the
// value goes in, a yes/no comes out. Validated against Deezer before anything is written, so a
// mistyped token can't be saved and then fail every download exactly as the expired one did.
api.MapPost("/purchases/deezer-arl", async (ArlUpdateRequest body, DeezerCredentialService credentials) =>
    {
        var result = await credentials.Update(body.Arl ?? "");
        // 400, not 500: a rejected or unsaveable ARL is the user's input to correct, and the message
        // is written for them to act on.
        return result.Saved ? Results.Ok(result) : Results.BadRequest(result);
    })
    .RequireAuthorization()
    .WithName("SetDeezerArl");

// Undo — move a downloaded/queued item back to "pending".
api.MapPost("/purchases/unsend", async (string id, PurchaseService purchases) =>
        await purchases.Unsend(id) ? Results.NoContent() : Results.NotFound())
    .RequireAuthorization()
    .WithName("UnsendPurchase");

// The library albums a (near-miss titled) album can be merged into: the suggestions for this album
// by default, or a whole-library search when `q` is supplied. Feeds the "Already in library?" pane,
// which is offered wherever a missing album shows — the Download queue, the Browse discography and
// the Discover feed. Each option carries an "open in Plex" link (best-effort) so the copy being
// merged into can be checked first. Query params so names with '/' survive.
api.MapGet("/albums/merge-candidates", async (
            string artist, string album, string? q, PurchaseService purchases, PlexAlbumLinker links) =>
        Results.Ok(await links.WithLinks(await purchases.MergeCandidates(artist, album, q))))
    .RequireAuthorization()
    .WithName("AlbumMergeCandidates");

// Merge a missing album into one already in the library under a different title (e.g. Deezer's "DOOM
// (Original Game Soundtrack)" vs. Plex's "Doom: Original Game Soundtrack", or a copy filed under a
// different act entirely): records a durable match override honoured by both the reconcile and the
// missing-album diff, and closes out any queued download.
api.MapPost("/albums/merge", async (string artist, string album, string libraryAlbum, PurchaseService purchases) =>
        await purchases.MergeAlbum(artist, album, libraryAlbum)
            ? Results.NoContent()
            : Results.BadRequest("Artist, album and library album are all required."))
    .RequireAuthorization()
    .WithName("MergeAlbum");

// Block an album for everyone — the escalation from a personal "meh" (a thumbs-down, which only ever
// hides an album from the user who gave it). Use for releases nobody should be offered: a junk Deezer
// entry, a reissue that duplicates something owned. Existing verdicts and queued downloads are left
// alone; the block stops the album being offered from here on.
api.MapPost("/albums/block", async (string artist, string album, HttpContext http, DiscoveryEngine engine) =>
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
        {
            return Results.BadRequest("Artist and album are both required.");
        }
        await engine.BlockAlbum(http.User.GetSubject()!, artist, album);
        return Results.NoContent();
    })
    .RequireAuthorization()
    .WithName("BlockAlbum");

// Lift a global block, returning the album to everyone's feeds. Anyone may lift a block — the same
// trust model as recording a merge.
api.MapDelete("/albums/block", async (string artist, string album, DiscoveryEngine engine) =>
    {
        await engine.UnblockAlbum(artist, album);
        return Results.NoContent();
    })
    .RequireAuthorization()
    .WithName("UnblockAlbum");

app.MapDefaultEndpoints();

// Any unmatched, non-API route serves the SPA shell so client-side deep links work.
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>
/// Body of the ARL update. A POST body rather than a query parameter so the credential never lands in
/// a URL — where it would be logged by the request logger, proxies, and browser history alike.
/// </summary>
internal record ArlUpdateRequest(string? Arl);
