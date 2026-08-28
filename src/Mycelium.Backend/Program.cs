using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Mycelium.Backend;
using Mycelium.Backend.Services.Auth;
using Mycelium.Backend.Services.Background;
using Mycelium.Backend.Services.Download;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;
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

// One-time migration for the arrival of per-user quality tiers. The deployment default is the
// *lower* tier on purpose — a new account shouldn't quietly cost 3x the disk before anyone decides
// it should — but applying that to people already using the app would demote them without warning:
// their queued albums would start landing as 320, and the upgrade feed would go blank (it only
// surfaces rows some user out-ranks, which nobody would). Lifting existing users to lossless makes
// the default apply to accounts created afterwards, which is what it is for. Idempotent: it only
// touches docs with no tier, so a user later set to lossy by hand is never lifted back.
{
    using var scope = app.Services.CreateScope();
    var backfilled = await scope.ServiceProvider.GetRequiredService<IUserRepo>()
        .BackfillMissingQuality(AudioQuality.Lossless);
    if (backfilled > 0)
    {
        app.Logger.LogInformation(
            "Quality tiers: backfilled {Count} existing user(s) to {Quality} (new accounts default to {Default})",
            backfilled, AudioQuality.Lossless, MainModule.DefaultAudioQuality());
    }
}

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
app.MapGet("/auth/me", async (
        HttpContext http, DevUsers devUsers, UserQualityService qualities, ILoggerFactory loggerFactory) =>
    {
        var user = http.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }

        // The credential matters as well as the person: an API token minted without dev scope is not
        // a dev session even when its owner is in DEV_USERNAMES. Same call the DevUser policy makes,
        // so the panel is never offered where the server would refuse it.
        var isDev = devUsers.AllowsDevTools(user);
        var quality = await qualities.For(user.GetSubject()!);

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
            // How this request authenticated. Cheap, and it makes /auth/me the one call a script can
            // make to confirm its token works and resolves to the identity it expected.
            viaApiToken = ApiTokenClaims.IsApiToken(user),
            // What quality this account's requests download at, resolved (stored tier else the
            // deployment default) so the UI never has to know the default itself. Lets an album card
            // say "you'll get FLAC" rather than leaving the user to guess.
            maxQuality = quality.ToString(),
        });
    })
    .WithName("Me");

// Application API, grouped under /api so it shares the origin with the SPA without the SPA's
// client routes (/artists, /related, /purchases, ...) colliding with same-named API paths.
var api = app.MapGroup("/api");

// Deezer never answered — its rate-limit quota, usually. A retryable 503, never an empty 200: the
// SPA caches what an endpoint returns, so answering a rate-limited discography call with "no albums"
// pins that answer on screen until a hard reload.
static IResult DeezerBusy() => Results.Problem(
    "Deezer didn't answer — it rate-limits bursts. Try again in a moment.",
    statusCode: StatusCodes.Status503ServiceUnavailable);

// Plex refused the server's token. A 502 rather than a 401: nothing is wrong with the caller's
// session — the upstream this app depends on turned *it* away — and answering 401 would have the
// SPA bounce the user through a sign-in that fixes nothing. The message names the fix, because the
// alternative is a bare 500 that sends whoever pressed the button into the container logs.
// Nothing is linked. Distinct from a rejection: telling someone to re-mint a credential they have
// never minted sends them looking for a token that doesn't exist.
static IResult PlexNotLinked() => Results.Problem(
    "Plex isn't connected. Link it from the dev panel (Dev tools \u2192 Plex connection).",
    statusCode: StatusCodes.Status502BadGateway);

static IResult PlexTokenRejected() => Results.Problem(
    "Plex rejected this server's token — it has expired or been revoked. Re-link Plex in the dev "
    + "panel to mint a new one; it takes effect immediately, with no restart.",
    statusCode: StatusCodes.Status502BadGateway);

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

// The *calling user's* per-song Plex rating summary (highest / lowest / average, 0–5 stars) for one
// artist, shown in the discovery readout. Read as their own linked Plex account, so two users see
// their own stars rather than the server owner's; Present=false for artists not in Plex, and for a
// user who hasn't connected Plex at all (nothing to show either way).
api.MapGet("/artists/ratings", async (HttpContext http, string artist, ArtistRatingStatsService ratings) =>
        Results.Ok(await ratings.ForUser(http.User.GetSubject()!, new ArtistKey(artist))))
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

// Pin an artist to a specific id on one source (sticky override). The pin lands before the response;
// re-deriving that artist's similarity edges from the corrected id and rebuilding the caller's queue —
// so the old (wrong) edges drop off — is queued, since a rebuild re-walks every liked artist.
api.MapPost("/artists/sources/{source}",
        async (HttpContext http, string source, string artist, string id,
            IEnumerable<ISourceIdentityCorrector> correctors, ArtistFollowUpService followUps) =>
        {
            var corrector = correctors.FirstOrDefault(c => c.Source == source);
            if (corrector is null) return Results.NotFound();

            var identity = await corrector.Pin(new ArtistKey(artist), id);
            if (identity is null) return Results.NotFound();

            followUps.QueueIdentityRefresh(http.User.GetSubject()!, artist);
            return Results.Ok(identity);
        })
    .RequireAuthorization()
    .WithName("PinArtistSource");

// Clear a source's pin (or unlinked flag) so the artist re-resolves from a name search next time, and
// queue the same re-derive + rebuild a pin does, so a reset doesn't leave stale edges from the old
// (pinned/detached) identity behind.
api.MapDelete("/artists/sources/{source}",
        async (HttpContext http, string source, string artist,
            IEnumerable<ISourceIdentityCorrector> correctors, ArtistFollowUpService followUps) =>
        {
            var corrector = correctors.FirstOrDefault(c => c.Source == source);
            if (corrector is null) return Results.NotFound();

            await corrector.Clear(new ArtistKey(artist));
            followUps.QueueIdentityRefresh(http.User.GetSubject()!, artist);
            return Results.NoContent();
        })
    .RequireAuthorization()
    .WithName("ClearArtistSource");

// Stickily detach an artist from a source (it has no match there). Wipes the artist's stored
// similarity edges first — a detached source resolves to null and so won't overwrite the old (wrong)
// edges on its own — then queues the re-derive from whatever sources remain linked, and the rebuild.
api.MapPost("/artists/sources/{source}/unlink",
        async (HttpContext http, string source, string artist,
            IEnumerable<ISourceIdentityCorrector> correctors,
            IRelatedArtistRepo relatedRepo, ArtistFollowUpService followUps) =>
        {
            var corrector = correctors.FirstOrDefault(c => c.Source == source);
            if (corrector is null) return Results.NotFound();

            await corrector.Unlink(new ArtistKey(artist));
            await relatedRepo.DeleteAllSources(new ArtistKey(artist));
            followUps.QueueIdentityRefresh(http.User.GetSubject()!, artist);
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
api.MapPost("/catalog/refresh", async (CatalogRefresher refresher) =>
    {
        try
        {
            // Gap-fill like the scheduled syncs. Re-deriving every album's quality is a separate,
            // explicitly-named dev action (POST /api/dev/catalog/quality-sweep) rather than a side
            // effect of pressing "refresh".
            return Results.Ok(await refresher.Refresh(CatalogRefresher.QualityRead.GapFill));
        }
        catch (PlexNotLinkedException)
        {
            return PlexNotLinked();
        }
        catch (PlexUnauthorizedException)
        {
            return PlexTokenRejected();
        }
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
        var lookup = await resolver.ResolvePlayInfo(artist, fresh ?? false);
        // 404 means Deezer answered and has no such artist ("Not on Deezer" — a fact the client can
        // cache). 503 means Deezer never answered, so the client must retry rather than record a
        // rate-limit blip as the artist's permanent verdict.
        if (lookup.Unavailable) return DeezerBusy();
        return lookup.Value is null ? Results.NotFound() : Results.Ok(lookup.Value);
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
// The body of this used to live here; it moved to DiscoveryRatingService when the batch route below
// arrived, so that both spell a verdict — and, more to the point, the Plex mood tag it adds and the
// opposite one it strips — exactly the same way. See that type for why a second copy would have been
// the dangerous kind of duplication: a divergence in the *stripping* fails nothing and shows up months
// later as a smart playlist matching music the user rejected.
api.MapPost("/discovery/rate", async (
        string artist, string? album, string? albumArt, string verdict, bool? upgrade,
        HttpContext http, DiscoveryRatingService ratings) =>
    {
        await ratings.RateOne(
            http.User.GetSubject()!,
            http.User.FindFirst("preferred_username")?.Value,
            new DiscoveryRateItem(artist, album, albumArt, verdict, upgrade));
        return Results.NoContent();
    })
    .RequireAuthorization()
    .WithName("RateCandidate");

// The same verdict, a playlist at a time. Exists for the migration client, which queues a whole
// playlist's worth of albums at once — 15–40 of them across as many artists — and until now paid a
// request per item plus its own client-side throttle for the privilege.
//
// A JSON body rather than query parameters, because a batch cannot go in a query string; the item
// shape is field-for-field the single route's parameters, so nothing has to be re-derived to move
// between the two. Answers 200 with a *per-item* verdict rather than one pass/fail: partial failure is
// the expected outcome, not an exception, and a caller told only "some of that didn't work" would have
// to re-read its ratings to find out which. Over the cap is a 400, never a silent truncation — a client
// told "OK" about 50 of the 60 albums it sent would wait forever on the other ten. Same auth as the
// single route: this is the same act, in bulk.
api.MapPost("/discovery/rate/batch", async (
        DiscoveryRateBatchRequest body, HttpContext http, DiscoveryRatingService ratings) =>
    {
        try
        {
            return Results.Ok(await ratings.RateMany(
                http.User.GetSubject()!,
                http.User.FindFirst("preferred_username")?.Value,
                body.Items ?? Array.Empty<DiscoveryRateItem>()));
        }
        catch (ArgumentException ex)
        {
            // Over the cap. Same shape the tag-edit and playlist routes answer a bad request with.
            return Results.BadRequest(new { error = ex.Message });
        }
    })
    .RequireAuthorization()
    .WithName("RateCandidateBatch");

// Ad-hoc seed: add an artist that no one in the library recommends yet (so it never surfaces in the
// feed) straight from a source search. Pins the user's *chosen* candidate by id (honouring their
// disambiguation rather than re-guessing the name), which captures the identity into the catalog, then
// likes it — growing the frontier from it and queuing it to buy, exactly as thumbing a recommendation
// does. The like is what seeds it into discovery; the pin just makes the right Deezer artist drive that
// expansion. Mirrors the Plex tagging of /discovery/rate (best-effort; the artist may not be in Plex).
api.MapPost("/discovery/seed", async (
        string source, string artist, string id,
        HttpContext http, IEnumerable<ISourceIdentityCorrector> correctors,
        DiscoveryEngine engine, ArtistFollowUpService followUps) =>
    {
        var corrector = correctors.FirstOrDefault(c => c.Source == source);
        if (corrector is null) return Results.NotFound();

        var identity = await corrector.Pin(new ArtistKey(artist), id);
        if (identity is null) return Results.NotFound();

        // Pin + verdict, then answer. The expansion and the Plex tag run on the follow-up worker: this
        // artist is new to the app, so every graph fetch it implies is a cold, rate-limited round trip
        // and the Plex tag write can't hit a stored rating key — seconds of work the "Added" tick in
        // the UI has no reason to wait for.
        var userId = http.User.GetSubject()!;
        var depth = await engine.RecordArtistVerdict(userId, artist, DiscoveryStatus.Liked);

        var username = http.User.FindFirst("preferred_username")?.Value;
        var tag = ArtistTag.For(username, DiscoveryStatus.Liked);
        var dislikeTag = tag != null ? ArtistTag.For(username, DiscoveryStatus.Disliked) : null;
        followUps.QueueVerdictFollowUp(
            userId, artist, DiscoveryStatus.Liked, depth,
            addTag: tag,
            removeTags: dislikeTag != null ? new[] { dislikeTag } : Array.Empty<string>());

        return Results.Ok(new { artist });
    })
    .RequireAuthorization()
    .WithName("SeedArtist");

// A liked non-owned artist's acquirable albums (their Deezer discography minus anything already
// owned), surfaced inline under the just-rated card so a fresh discovery can be acted on. Fetched
// on demand (one Deezer call) only when an artist is liked — not precomputed per feed card.
api.MapGet("/discovery/artist-albums", async (string artist, HttpContext http, DiscoveryEngine engine) =>
    {
        try
        {
            return Results.Ok(await engine.ArtistAlbums(http.User.GetSubject()!, artist));
        }
        catch (DeezerUnavailableException)
        {
            // An empty list here would read as "this artist has nothing to acquire" and be cached as
            // such by the client. Fail loudly instead so it retries.
            return DeezerBusy();
        }
    })
    .RequireAuthorization()
    .WithName("GetArtistAlbums");

// An owned artist's full Deezer discography for the Artists-page drill-down: every LP flagged owned
// vs. missing, missing ones overlaid with the user's verdict. One Deezer call per expand. The owned
// rows are deep linked into Plex here rather than in the engine, which knows only the abstract
// library — same split as the merge picker's suggestions below.
api.MapGet("/discovery/artist-discography", async (
        string artist, HttpContext http, DiscoveryEngine engine, PlexAlbumLinker links) =>
    {
        try
        {
            var albums = await engine.ArtistDiscography(http.User.GetSubject()!, artist);
            return Results.Ok(await links.WithLinks(albums));
        }
        catch (DeezerUnavailableException)
        {
            return DeezerBusy();
        }
    })
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
        string artist, string? album, HttpContext http, DiscoveryEngine engine,
        ArtistFollowUpService followUps, CollectionService collections) =>
    {
        var userId = http.User.GetSubject()!;
        if (string.IsNullOrEmpty(album))
        {
            await engine.ClearArtistVerdict(userId, artist);
            // The queued follow-up prunes what the artist seeded and undoes the Plex tag — a cleared
            // verdict shouldn't leave its "<username>_liked"/"_disliked" tag behind. We don't know which
            // verdict it was, so strip both (the user holds at most one). Queued rather than awaited so
            // it can't reorder against the rate that preceded it: one worker, submission order.
            var username = http.User.FindFirst("preferred_username")?.Value;
            var tags = new[] { DiscoveryStatus.Liked, DiscoveryStatus.Disliked }
                .Select(s => ArtistTag.For(username, s))
                .OfType<string>()
                .ToArray();
            followUps.QueueVerdictFollowUp(userId, artist, status: null, depth: 0, addTag: null, removeTags: tags);
        }
        else
        {
            await engine.ClearAlbumRating(userId, artist, album);
            // Undo the album mood a collection's verdict wrote. We don't know which of the two it was,
            // so strip both — the user holds at most one. No-op for a non-umbrella album, whose artist
            // mood is the user's own verdict on the act and isn't this endpoint's to clear.
            collections.QueueTagWrite(
                http.User.FindFirst("preferred_username")?.Value, artist, album, status: null);
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

// --- Collections: records no artist's discography can reach ---
// A various-artists compilation, a soundtrack, a cast recording. Deezer credits them to an umbrella
// act whose discography is empty (asking for "Various Artists"' albums returns nothing at all), so the
// artist-rooted walk behind every other feed in this app can never surface one. These endpoints are
// the way in: name the record, or paste its link. Deliberately absent from the Discover feed — see
// CollectionService.

// Everything the user can act on: umbrella-credited albums the library already holds, plus every one
// they have thumbed. The owned-but-unrated rows are the point — without them there'd be no way to say
// you like a compilation you already own, and it could never reach a "My Library" playlist.
api.MapGet("/collections", async (HttpContext http, CollectionService collections) =>
        Results.Ok(await collections.List(http.User.GetSubject()!)))
    .RequireAuthorization()
    .WithName("GetCollections");

// Deezer album search, umbrella-credited hits first. Non-umbrella albums are kept rather than filtered
// out: a search that claimed "no results" for a record Deezer plainly has would read as broken.
api.MapGet("/collections/search", async (string q, HttpContext http, CollectionService collections) =>
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Results.Ok(Array.Empty<CollectionItem>());
        }

        try
        {
            return Results.Ok(await collections.Search(http.User.GetSubject()!, q));
        }
        catch (DeezerUnavailableException)
        {
            return DeezerBusy();
        }
    })
    .RequireAuthorization()
    .WithName("SearchCollections");

// Resolve a pasted Deezer album link (or bare id) into a rateable row — the path for a record search
// won't surface. Body rather than query string: a pasted URL carries '/' and Deezer's share tracking
// params. 404 when the paste holds no album id, or Deezer doesn't know it.
api.MapPost("/collections/resolve", async (
        ManualAddRequest body, HttpContext http, CollectionService collections) =>
    {
        var item = await collections.Resolve(http.User.GetSubject()!, body.Url);
        return item is null
            ? Results.NotFound("That doesn't look like a Deezer album link.")
            : Results.Ok(item);
    })
    .RequireAuthorization()
    .WithName("ResolveCollection");

// Thumb a collection by its Deezer album id. Writes the global missing-album row (which is what
// carries the id through the purchase reconcile to the downloader), the per-user verdict, and queues
// the mood tag — on the album for an umbrella-credited record, on the artist for anything else. This
// is the only id-keyed way to queue an album, so it is also how an ordinary release gets rated by an
// API client; the artist tag is what keeps that from writing nothing to Plex at all.
api.MapPost("/collections/rate", async (
        long id, string verdict, HttpContext http, CollectionService collections) =>
    {
        var status = verdict.Equals("up", StringComparison.OrdinalIgnoreCase)
            ? DiscoveryStatus.Liked
            : DiscoveryStatus.Disliked;
        var username = http.User.FindFirst("preferred_username")?.Value;
        var item = await collections.Rate(http.User.GetSubject()!, username, id, status);
        return item is null ? Results.NotFound($"Deezer has no album {id}.") : Results.Ok(item);
    })
    .RequireAuthorization()
    .WithName("RateCollection");

// The same thumb, a playlist at a time — the collections twin of /discovery/rate/batch, and there for
// the same client: a migration script naming 15–40 records at once instead of one request per album.
//
// A JSON body because a batch can't go in a query string. The per-item answer matters more here than on
// the discovery batch: every item costs a Deezer /album/{id} lookup, and an id Deezer won't resolve
// right now is an ordinary outcome — so an item that failed says so and names why, beside the ones that
// went in, each carrying the same row the single route returns. The 404 the single route answers an
// unknown id with becomes a per-item error, since one unresolvable album must not decide the status
// code for the other twenty-nine. Over the cap is a 400 rather than a truncation, as above.
//
// CollectionService.RateMany explains the pacing: sequential, on top of the rolling rate-limit window
// DeezerApi already puts every call in the process through.
api.MapPost("/collections/rate/batch", async (
        CollectionRateBatchRequest body, HttpContext http, CollectionService collections) =>
    {
        try
        {
            return Results.Ok(await collections.RateMany(
                http.User.GetSubject()!,
                http.User.FindFirst("preferred_username")?.Value,
                body.Items ?? Array.Empty<CollectionRateItem>()));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    })
    .RequireAuthorization()
    .WithName("RateCollectionBatch");

// --- API tokens for unattended automation ---
// Long-lived credentials that authenticate as an existing user, so the seeding and playlist-
// acquisition scripts stop needing a session cookie copied out of devtools every time the old one
// lapses. See ApiTokenService for what a token is and why it carries the user's own identity.
//
// Gated on InteractiveUser — the cookie scheme specifically — rather than plain RequireAuthorization:
// a token can drive the whole API as its user, but cannot mint another or revoke one. Issuing
// credentials stays something a person does at a browser, so a leaked token can't quietly reissue
// itself once the operator starts revoking.
var apiTokens = api.MapGroup("/tokens").RequireAuthorization(BffAuthentication.InteractiveUserPolicy);

// The caller's own tokens, live and dead. No secrets: the app doesn't have them to return.
apiTokens.MapGet("", async (HttpContext http, ApiTokenService tokens) =>
        Results.Ok(await tokens.List(http.User.GetSubject()!)))
    .WithName("ListApiTokens");

// Mint one. The response is the only time the token value exists anywhere but the caller's hands —
// it is not stored, not logged, and not retrievable afterwards, so a lost token is re-minted rather
// than recovered.
apiTokens.MapPost("", async (
        HttpContext http, ApiTokenCreateRequest body, ApiTokenService tokens, DevUsers devUsers) =>
    {
        var wantsDev = body.Dev == true;
        if (wantsDev && !devUsers.AllowsDevTools(http.User))
        {
            // Refused rather than quietly downgraded to a non-dev token: a script handed a token it
            // was told is dev-scoped would otherwise fail later, on the one endpoint it needed.
            return Results.Problem(
                "Only a dev user, signed in at a browser, can grant a token dev scope.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        var lifetime = body.ExpiresInDays is { } days ? TimeSpan.FromDays(days) : (TimeSpan?)null;
        var result = await tokens.Mint(http.User.GetSubject()!, body.Name, wantsDev, lifetime);
        return result.Minted is { } minted
            ? Results.Ok(minted)
            : Results.BadRequest(new { error = result.Error });
    })
    .WithName("CreateApiToken");

// Revoke one, by its public id. Takes effect on the next request the token makes — nothing caches a
// verification. Scoped to the caller's own tokens, so a 404 covers both "no such token" and
// "somebody else's".
apiTokens.MapDelete("/{id}", async (string id, HttpContext http, ApiTokenService tokens) =>
        await tokens.Revoke(http.User.GetSubject()!, id)
            ? Results.NoContent()
            : Results.NotFound(new { error = "No live token of yours with that id." }))
    .WithName("RevokeApiToken");

// --- Dev panel: Plex tag maintenance ---
// Wipe and/or rebuild the per-user like/dislike mood tags so we can iterate on the tagging logic
// without leaving orphaned tags scattered across the library. Gated by the "DevUser" policy
// (DEV_USERNAMES) — the clear is destructive (it nukes every "_liked"/"_disliked" tag), so these
// stay restricted to dev users rather than any signed-in user.
var dev = api.MapGroup("/dev/plex-tags").RequireAuthorization("DevUser");

// Strip every verdict tag from every artist (clean slate). The "_added" credits survive — see
// PlexTagMaintenance.
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

// Reconcile the "<username>_recommended" markers out of band, instead of waiting for the nightly
// catalog sync to do it. Not destructive in the way /clear is — it both adds and removes, and only
// within its own tag namespace — but it reads the whole library and writes across it, so it sits with
// the rest of the tag maintenance rather than being offered to every signed-in user.
dev.MapPost("/recommended", async (RecommendedArtistTagger tagger) =>
    {
        var result = await tagger.Sync();
        return Results.Ok(new { added = result.Added, removed = result.Removed });
    })
    .WithName("DevSyncRecommendedTags");

// --- Dev panel: the server's own Plex credential ---
// The token every library read is made with. These endpoints mint it in place: the same plex.tv PIN
// flow the per-user link uses, pointed at the server credential, with the result stored in Mongo and
// picked up by the next Plex call. It is the only way the app gets a Plex credential — DevUser-gated,
// since this is what the whole app reads the library with.
var plexToken = api.MapGroup("/dev/plex/server-token").RequireAuthorization("DevUser");

// Cheap and pollable: reports the last verdict rather than asking Plex again.
plexToken.MapGet("", async (PlexServerTokenService tokens) =>
        Results.Ok(await tokens.Status()))
    .WithName("GetPlexServerToken");

// Asks Plex now. The panel's "check again" button, and what the daily sync calls on its own.
plexToken.MapPost("/verify", async (PlexServerTokenService tokens) =>
        Results.Ok(await tokens.Verify()))
    .WithName("VerifyPlexServerToken");

plexToken.MapPost("/start", async (HttpContext http, PlexServerTokenService tokens, string? forwardUrl) =>
        Results.Ok(new { authUrl = await tokens.Start(http.User.GetSubject()!, forwardUrl) }))
    .WithName("StartPlexServerTokenLink");

// Polled while the operator approves in their browser; "pending" until they finish.
plexToken.MapPost("/complete", async (HttpContext http, PlexServerTokenService tokens) =>
    {
        var (outcome, status) = await tokens.Complete(http.User.GetSubject()!);
        return Results.Ok(new { outcome = outcome.ToString().ToLowerInvariant(), status });
    })
    .WithName("CompletePlexServerTokenLink");

// Disconnect Plex entirely. The app keeps serving the stored catalog; nothing can refresh it until
// something links again.
plexToken.MapDelete("", async (PlexServerTokenService tokens) =>
        Results.Ok(await tokens.Clear()))
    .WithName("ClearPlexServerToken");

// --- Dev panel: audio-quality catch-up sweep ---
// Re-derives every owned album's format from a paged read of the whole library (~82k tracks, ~22s).
// Needed once, to fill in a library that predates quality tracking; after that the ordinary syncs
// gap-fill new arrivals one small read at a time and this is only for recomputing from scratch.
api.MapPost("/dev/catalog/quality-sweep", async (CatalogRefresher refresher) =>
    {
        try
        {
            var result = await refresher.Refresh(CatalogRefresher.QualityRead.Full);
            return Results.Ok(new { artists = result.TotalPresent });
        }
        catch (PlexNotLinkedException)
        {
            return PlexNotLinked();
        }
        catch (PlexUnauthorizedException)
        {
            // The whole-library sweep is the other button that reads Plex directly, so it hits an
            // expired token exactly as the plain refresh does.
            return PlexTokenRejected();
        }
    })
    .RequireAuthorization("DevUser")
    .WithName("DevAudioQualitySweep");

// --- Dev panel: per-user download quality ---
// Who is allowed to pull down lossless. The list is the app's own user store, which is populated on
// login — so a user who has never signed in cannot appear here or be given a tier until they do
// (the IdP is the source of truth for identity and we never enumerate it). Same DevUser gate as the
// tools above: this decides what other people's requests cost on the shared library volume.
var devUsersApi = api.MapGroup("/dev/users").RequireAuthorization("DevUser");

devUsersApi.MapGet("/", async (IUserRepo users, UserQualityService qualities) =>
    {
        var all = await users.GetAll();
        return Results.Ok(new
        {
            defaultQuality = qualities.Default.ToString(),
            users = all.Select(u => new
            {
                subject = u.Subject,
                username = u.Username,
                displayName = u.DisplayName,
                email = u.Email,
                lastLoginAt = u.LastLoginAt,
                // Null when this user has never been given an explicit tier; `effectiveQuality` is
                // what they actually download at, so the panel can show "Lossy (default)" without
                // duplicating the fallback rule client-side.
                maxQuality = u.MaxQuality?.ToString(),
                effectiveQuality = (u.MaxQuality ?? qualities.Default).ToString(),
            }),
        });
    })
    .WithName("DevListUserQuality");

devUsersApi.MapPost("/{subject}/quality", async (
        string subject, UserQualityRequest body, IUserRepo users) =>
    {
        // An absent/blank quality clears the override, returning the user to the deployment default.
        // A value we can't parse is a client bug, not a request to clear — say so rather than
        // silently resetting someone's entitlement.
        var wanted = AudioQualityTier.Parse(body.Quality);
        if (wanted is null && !string.IsNullOrWhiteSpace(body.Quality))
        {
            return Results.BadRequest(new { error = $"Unknown quality '{body.Quality}'" });
        }

        if (await users.Get(subject) is null)
        {
            return Results.NotFound(new { error = "No such user" });
        }

        await users.SetMaxQuality(subject, wanted);
        return Results.Ok(new { subject, maxQuality = wanted?.ToString() });
    })
    .WithName("DevSetUserQuality");

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
//
// `ids` (comma-separated Deezer album ids) narrows the answer to those albums. Optional: without it
// this is the whole active queue, which is what the Download page reads. It exists because "shared" is
// the problem for anything that isn't that page — a migration client that queued thirty albums wants
// to know where those thirty stand, and pulling several hundred rows to keep thirty of them costs more
// the healthier the queue is. The ids are also the only handle such a client has: it queued by Deezer
// album id and got no purchase id back. Pushed down to a Mongo query rather than filtered here, the
// way the catalog resolves a set of artists.
//
// Capped at the same number as a rating batch, deliberately — a client that just submitted a batch has
// to be able to ask about all of it in one request, or it is back in the per-item loop the batch
// replaced. Unparseable ids are dropped rather than failing the request; an `ids` that parses to
// nothing at all is answered as the empty list it asked for, not as the whole queue.
//
// `includeCompleted=true` keeps the rows that have landed in the library instead of dropping them, so
// each carries the `inLibraryAt` stamp that says the acquisition finished and when. This is the half of
// the answer a polling client is actually waiting for: without it, success and "removed from the queue
// because nobody wants it any more" are the same observation — the row is simply gone. Meant to be
// paired with `ids`, where the result stays bounded by what the client asked about; the Download page
// leaves it off, because that list must not fill up with every record ever acquired.
api.MapGet("/purchases", async (PurchaseService purchases, string? ids, bool? includeCompleted) =>
    {
        var completed = includeCompleted == true;
        if (ids is null)
        {
            return Results.Ok(await purchases.GetActive(includeCompleted: completed));
        }

        var wanted = ids
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => long.TryParse(id, out var parsed) ? (long?)parsed : null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();

        if (wanted.Length > BatchLimits.MaxItems)
        {
            return Results.BadRequest(new
            {
                error = $"An ids filter is capped at {BatchLimits.MaxItems} ids; got {wanted.Length}.",
            });
        }

        return Results.Ok(await purchases.GetActive(wanted, completed));
    })
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

// Fast mode: a time-boxed burst that lifts the batch cap so every pending album is queued at once,
// then lapses on its own an hour later (the deadline is stored, so it survives a redeploy and nothing
// has to switch it back off). Turning it on runs an enqueue pass right here rather than waiting for
// the next batch tick — "queue everything" is the point, and the pass only reads Mongo. The reply
// carries the deadline so the page can start counting down without a second round trip.
// The drainer is then woken as well, because that inline pass only covers the backlog as it stands
// right now: the loop itself may be twenty minutes into a half-hour sleep, and until it wakes it is
// still running at the batch pace — so an album added a minute into the burst would sit Pending for
// the rest of that interval. Waking it makes it re-read the deadline and drop to the fast cadence at
// once; the pass it runs on waking merely repeats one that just happened, which is idempotent.
api.MapPost("/purchases/fast", async (bool fast, DownloadSettings settings, DownloadService downloads) =>
    {
        var until = await settings.SetFast(fast);
        if (fast)
        {
            await downloads.EnqueuePendingBatch();
            downloads.WakeEnqueue();
        }
        return Results.Ok(new FastModeResponse(until));
    })
    .RequireAuthorization()
    .WithName("SetDownloadsFast");

// Manually queue an item for download now (the "Download now"/"Retry" button). Non-blocking — the
// drainer does the fetch; returns immediately. Works whether or not automatic downloads are on.
// The presser is recorded on the row and becomes the album's permanent "<user>_added" mood once the
// download lands in the library (see PurchaseService.Reconcile).
api.MapPost("/purchases/download", async (string id, HttpContext http, DownloadService downloads) =>
        await downloads.RequestDownload(id, http.User.FindFirst("preferred_username")?.Value)
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

// Queue an album by hand from a pasted Deezer link (or bare album id) — the escape hatch for
// releases no owned artist's discography lists, chiefly various-artists compilations. Body rather
// than query string: a pasted URL carries '/' and '?' and Deezer's share tracking params. Answers
// 200 with the created/existing row, or 400 carrying the reason so the paste box can explain itself.
api.MapPost("/purchases/add", async (ManualAddRequest body, HttpContext http, PurchaseService purchases) =>
    {
        // Whoever pasted the link gets the "<user>_added" credit when the album lands — a hand-added
        // compilation has nothing but a person behind it.
        var outcome = await purchases.AddManual(
            body.Url, http.User.FindFirst("preferred_username")?.Value);
        return outcome.Result is ManualAddResult.Added or ManualAddResult.AlreadyQueued
            ? Results.Ok(outcome)
            : Results.BadRequest(outcome);
    })
    .RequireAuthorization()
    .WithName("AddManualPurchase");

// Drop a hand-added row. Only manual rows: everything else leaves by clearing the rating behind it,
// and deleting one directly would just have it return on the next reconcile.
api.MapDelete("/purchases/manual", async (string id, PurchaseService purchases) =>
        await purchases.RemoveManual(id) ? Results.NoContent() : Results.NotFound())
    .RequireAuthorization()
    .WithName("RemoveManualPurchase");

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

// --- Plex account linking -------------------------------------------------------------------
// Playlists, star ratings and play history are all per-Plex-account. Creating playlists with the
// server's own token would file every user's playlists in the owner's sidebar and filter them by the
// owner's ratings, so the playlist features act as the user's own linked account instead. Library
// metadata (the mood tags a thumb writes) keeps using the server token — that's shared state.
var plexLink = api.MapGroup("/plex/link").RequireAuthorization();

plexLink.MapGet("", async (HttpContext http, PlexLinkService links) =>
        Results.Ok(await links.Status(http.User.GetSubject()!)))
    .WithName("GetPlexLink");

// Starts the plex.tv PIN flow and hands back the URL to send the user to. The PIN is held server-side
// against this user, so the poll below needs no arguments and the code never round-trips the browser.
plexLink.MapPost("/start", async (HttpContext http, PlexLinkService links, string? forwardUrl) =>
        Results.Ok(new { authUrl = await links.Start(http.User.GetSubject()!, forwardUrl) }))
    .WithName("StartPlexLink");

// Polled while the user approves in their browser. "pending" is the normal answer until they finish.
plexLink.MapPost("/complete", async (HttpContext http, PlexLinkService links) =>
    {
        var completion = await links.Complete(http.User.GetSubject()!);
        return Results.Ok(new
        {
            outcome = completion.Outcome.ToString().ToLowerInvariant(),
            status = completion.Status,
        });
    })
    .WithName("CompletePlexLink");

// Link from a token the user pasted instead of running the PIN flow — the way to act as a Plex Home
// or managed user, who has no browser session at app.plex.tv of their own to approve with. A POST
// body, never a query parameter: the credential must not reach the request log, proxies or history.
// 400 (like the ARL paste) when the token is rejected — that's input for the user to correct, and the
// body carries the outcome either way so the paste box can say which of the two things went wrong.
plexLink.MapPost("/token", async (HttpContext http, PlexTokenLinkRequest body, PlexLinkService links) =>
    {
        var completion = await links.LinkWithToken(http.User.GetSubject()!, body.Token, body.Label);
        var payload = new
        {
            outcome = completion.Outcome.ToString().ToLowerInvariant(),
            status = completion.Status,
        };
        return completion.Outcome == PlexLinkOutcome.Linked
            ? Results.Ok(payload)
            : Results.BadRequest(payload);
    })
    .WithName("LinkPlexWithToken");

// Forgets the account and its stored token. Playlists already created stay put — they're the user's.
plexLink.MapDelete("", async (HttpContext http, PlexLinkService links) =>
    {
        await links.Unlink(http.User.GetSubject()!);
        return Results.NoContent();
    })
    .WithName("UnlinkPlex");

// --- Stock smart playlists ------------------------------------------------------------------
// A page of ready-made smart playlists, so someone can get a working set without learning Plex's
// filter editor. Whether one already exists is decided by comparing *rules*, never names.
var playlists = api.MapGroup("/playlists").RequireAuthorization();

static int FreshWindow(int? months) =>
    months is not null && SmartPlaylistCatalog.FreshWindows.Contains(months.Value)
        ? months.Value
        : SmartPlaylistService.DefaultFreshMonths;

playlists.MapGet("/stock", async (HttpContext http, SmartPlaylistService service, int? freshMonths) =>
        Results.Ok(await service.Survey(
            http.User.GetSubject()!,
            http.User.FindFirst("preferred_username")?.Value,
            FreshWindow(freshMonths))))
    .WithName("SurveyStockPlaylists");

playlists.MapPost("/stock/{id}", async (
        string id, HttpContext http, SmartPlaylistService service, int? freshMonths) =>
    {
        try
        {
            return Results.Ok(await service.Create(
                http.User.GetSubject()!,
                http.User.FindFirst("preferred_username")?.Value,
                id,
                FreshWindow(freshMonths)));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // No linked Plex account, or the server is unreachable — both are "can't act yet", not bugs.
            return Results.BadRequest(new { error = ex.Message });
        }
    })
    .WithName("CreateStockPlaylist");

// How this user rates in Plex. Plex itself can't be asked — half-star support is a per-client
// capability (Plexamp has it, Plex Web doesn't), exposed by no server or account setting — so the
// user tells us, and the generated rules put their "never play again" floor in the right place.
// A null clears the answer, returning them to the catalog default.
playlists.MapPut("/rating-scale", async (
        RatingScaleRequest body, HttpContext http, IUserRepo users) =>
    {
        var subject = http.User.GetSubject()!;
        if (await users.Get(subject) is null)
        {
            return Results.BadRequest(new { error = "No such user" });
        }

        await users.SetHalfStarRatings(subject, body.HalfStars);
        return Results.Ok(new
        {
            halfStars = body.HalfStars ?? SmartPlaylistCatalog.DefaultHalfStars,
        });
    })
    .WithName("SetRatingScale");

// Rewrites the rules of a playlist that holds one of our names but selects something else.
playlists.MapPut("/stock/{id}", async (
        string id, HttpContext http, SmartPlaylistService service, int? freshMonths) =>
    {
        try
        {
            return Results.Ok(await service.UpdateRules(
                http.User.GetSubject()!,
                http.User.FindFirst("preferred_username")?.Value,
                id,
                FreshWindow(freshMonths)));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    })
    .WithName("UpdateStockPlaylist");

app.MapDefaultEndpoints();

// Any unmatched, non-API route serves the SPA shell so client-side deep links work.
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>
/// Body of the ARL update. A POST body rather than a query parameter so the credential never lands in
/// a URL — where it would be logged by the request logger, proxies, and browser history alike.
/// </summary>
internal record ArlUpdateRequest(string? Arl);

/// <summary>
/// Body of the rating-scale update: whether the user rates in half stars. Null means "I never said",
/// which restores the catalog default rather than asserting whole stars.
/// </summary>
internal record RatingScaleRequest(bool? HalfStars);

/// <summary>
/// Body of a Plex token paste. Same reasoning as <see cref="ArlUpdateRequest"/>: a credential in a
/// query string would be written to the request log verbatim.
/// </summary>
/// <param name="Label">
/// What to call the account. Used only when plex.tv can't identify the token — a Plex server access
/// token verifies against the server but can't be attributed to anyone, so this is a display label
/// rather than a confirmed identity.
/// </param>
internal record PlexTokenLinkRequest(string? Token, string? Label);

/// <summary>
/// Body of a manual album add: whatever the user pasted. A POST body rather than a query parameter
/// because a Deezer album URL carries path separators and a share query string of its own, which
/// would have to be escaped into a query param and unescaped back out for no gain.
/// </summary>
internal record ManualAddRequest(string? Url);

/// <summary>
/// Body of a batch of discovery verdicts. A POST body rather than query parameters for the reason the
/// pasted-link routes have one: a batch has no query-string spelling at all. Wrapped in an object with
/// an <c>items</c> field rather than being a bare array, so the request can grow a sibling field later
/// without breaking every client — a top-level array has nowhere to put one.
/// </summary>
internal record DiscoveryRateBatchRequest(DiscoveryRateItem[]? Items);

/// <summary>
/// Body of a batch of collection verdicts. Same shape and same reasoning as
/// <see cref="DiscoveryRateBatchRequest"/>, over Deezer album ids.
/// </summary>
internal record CollectionRateBatchRequest(CollectionRateItem[]? Items);

/// <summary>
/// Body of a token mint. A POST body rather than query parameters to match the other mutating
/// endpoints, and because <paramref name="Dev"/> is a privilege decision that belongs somewhere less
/// easily copy-pasted than a URL.
/// </summary>
/// <param name="Name">Label for the revoke list — which script this is for.</param>
/// <param name="ExpiresInDays">Null for "until revoked". An expiry is optional because the workflow
/// this exists for runs on a schedule nobody watches; a token that lapses unannounced would recreate
/// the exact failure it was built to end.</param>
/// <param name="Dev">Whether the token may reach the dev endpoints. Off unless asked for, and only
/// grantable by a dev user at a browser — see the endpoint.</param>
internal record ApiTokenCreateRequest(string? Name, int? ExpiresInDays, bool? Dev);

/// <summary>
/// Body of a per-user quality change. A POST body rather than a query parameter to match the other
/// mutating dev endpoints; a null/blank <paramref name="Quality"/> clears the user's override.
/// </summary>
internal record UserQualityRequest(string? Quality);

/// <summary>
/// Reply to the fast-mode toggle: when the burst lapses, or null when it was switched off. Returned so
/// the page starts its countdown from the server's own deadline rather than assuming an hour from the
/// click — the two differ by however long the enqueue pass took.
/// </summary>
internal record FastModeResponse(DateTimeOffset? FastUntil);
