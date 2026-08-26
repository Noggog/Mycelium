using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithRedisInsight();

var database = builder.AddMongoDB("db")
    .WithLifetime(ContainerLifetime.Persistent);

var backend = builder.AddProject<Mycelium_Backend>("backend")
    .WithReference(cache)
    .WaitFor(cache)
    .WithReference(database)
    .WaitFor(database)
    // BFF auth against the existing Authentik instance. The issuer/client id/secret are secrets,
    // so they're forwarded from host env vars (set them in local.secrets.env) rather than committed.
    // PUBLIC_ORIGIN is the browser-facing SPA origin the OIDC callback returns through.
    .WithEnvironment("OIDC_AUTHORITY", Environment.GetEnvironmentVariable("OIDC_AUTHORITY"))
    .WithEnvironment("OIDC_CLIENT_ID", Environment.GetEnvironmentVariable("OIDC_CLIENT_ID"))
    .WithEnvironment("OIDC_CLIENT_SECRET", Environment.GetEnvironmentVariable("OIDC_CLIENT_SECRET"))
    .WithEnvironment("PUBLIC_ORIGIN",
        Environment.GetEnvironmentVariable("PUBLIC_ORIGIN") ?? "http://localhost:5173")
    // MongoDbProvider reads the connection string from the "MONGO_URI" env var; feed it
    // the Aspire-provisioned MongoDB connection string (otherwise it's null -> 500s).
    .WithEnvironment("MONGO_URI", database)
    // Plex settings are forwarded from same-named env vars at run time (override as needed).
    // Aspire does not auto-propagate AppHost env vars to children, so these are explicit.
    .WithEnvironment("PLEX_ENDPOINT", Environment.GetEnvironmentVariable("PLEX_ENDPOINT"))
    .WithEnvironment("PLEX_LIBRARY", Environment.GetEnvironmentVariable("PLEX_LIBRARY"))
    // Deezer similarity-graph knobs (both optional; the backend defaults them when unset):
    //   DEEZER_BASE_URI       override the public Deezer API endpoint
    //   RELATED_STALENESS_DAYS how long a stored edge set is considered fresh (default 30)
    .WithEnvironment("DEEZER_BASE_URI", Environment.GetEnvironmentVariable("DEEZER_BASE_URI"))
    .WithEnvironment("RELATED_STALENESS_DAYS", Environment.GetEnvironmentVariable("RELATED_STALENESS_DAYS"))
    // QUEUE_REPLENISH_INTERVAL_HOURS how often the per-user queue top-up runs (default 24)
    .WithEnvironment("QUEUE_REPLENISH_INTERVAL_HOURS", Environment.GetEnvironmentVariable("QUEUE_REPLENISH_INTERVAL_HOURS"))
    // Deezer download subsystem (streamrip). MUSIC_DOWNLOAD_DIR is the library root downloads land in;
    // the rest are optional throttle/quality knobs (backend defaults them). The ARL and the
    // artist/album folder_format live in streamrip's own config, not here.
    .WithEnvironment("MUSIC_DOWNLOAD_DIR", Environment.GetEnvironmentVariable("MUSIC_DOWNLOAD_DIR"))
    .WithEnvironment("STREAMRIP_BIN", Environment.GetEnvironmentVariable("STREAMRIP_BIN"))
    .WithEnvironment("DEEZER_QUALITY", Environment.GetEnvironmentVariable("DEEZER_QUALITY"))
    .WithEnvironment("DEEZER_FALLBACK_QUALITY", Environment.GetEnvironmentVariable("DEEZER_FALLBACK_QUALITY"))
    .WithEnvironment("DEEZER_CODEC", Environment.GetEnvironmentVariable("DEEZER_CODEC"))
    .WithEnvironment("DOWNLOAD_BATCH_SIZE", Environment.GetEnvironmentVariable("DOWNLOAD_BATCH_SIZE"))
    .WithEnvironment("DOWNLOAD_ITEM_DELAY_SECONDS", Environment.GetEnvironmentVariable("DOWNLOAD_ITEM_DELAY_SECONDS"))
    .WithEnvironment("DOWNLOAD_BATCH_INTERVAL_MINUTES", Environment.GetEnvironmentVariable("DOWNLOAD_BATCH_INTERVAL_MINUTES"))
    .WithEnvironment("DEEZER_DOWNLOAD_TIMEOUT_MINUTES", Environment.GetEnvironmentVariable("DEEZER_DOWNLOAD_TIMEOUT_MINUTES"));

builder.AddNpmApp("web", "../Mycelium.Web", "dev")
    .WithReference(backend)
    .WaitFor(backend)
    .WithEnvironment("VITE_BACKEND_URL", backend.GetEndpoint("http"))
    // Pin the web app to a fixed port (Vite reads PORT) so the URL is stable across runs
    // instead of Aspire allocating a random one.
    .WithHttpEndpoint(port: 5173, env: "PORT")
    .WithExternalHttpEndpoints();

builder.Build().Run();
