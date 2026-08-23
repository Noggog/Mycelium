using Autofac;
using Microsoft.Extensions.Hosting;
using Mycelium.Backend.Services.Background;
using Mycelium.Backend.Services.Download;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Deezer;
using Mycelium.Interfaces;
using Mycelium.ListenBrainz;
using Mycelium.MongoDB;
using Mycelium.Plex;
using Noggog.Autofac;

namespace Mycelium.Backend;

public class MainModule : Autofac.Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterModule<PlexModule>();
        builder.RegisterModule<MongoDbModule>();
        // Deezer is the live recommendation source (DeezerProvider : IRecommendationProvider),
        // replacing the deprecated Spotify recommendations API.
        builder.RegisterModule<DeezerModule>();
        // ListenBrainz/MusicBrainz is a second, independent similarity source (ISimilaritySource);
        // its edges are merged with Deezer's at read time. Self-disables via LISTENBRAINZ_ENABLED.
        builder.RegisterModule<ListenBrainzModule>();

        // How long a stored similarity-graph edge set stays fresh before re-ingestion (env knob,
        // default 30 days). Read once here so the ingestion service stays env-free and testable.
        var stalenessDays = double.TryParse(
            Environment.GetEnvironmentVariable("RELATED_STALENESS_DAYS"), out var d) ? d : 30;
        builder.RegisterInstance(new RelatedStalenessPolicy(TimeSpan.FromDays(stalenessDays)));

        // Periodic queue replenisher cadence (env knob, default 24h). First run is offset ~5min past
        // boot so it lands after the catalog + album syncs rather than contending with them on Deezer.
        var replenishHours = double.TryParse(
            Environment.GetEnvironmentVariable("QUEUE_REPLENISH_INTERVAL_HOURS"), out var h) ? h : 24;
        builder.RegisterInstance(new ReplenishConfig(
            Interval: TimeSpan.FromHours(replenishHours), StartupDelay: TimeSpan.FromMinutes(5)));

        // When a thumbed artist gets offered back because the user's own Plex song ratings disagree with
        // how they thumbed it (env knobs, defaults: a dislike needs 3+ stars, a like 2 or below, either
        // way across at least a third of the songs), and how often the sweep looks. This re-litigates
        // verdicts made years ago, so weekly is plenty; the first run is offset past the catalog + album
        // syncs so boot isn't three heavy passes at once.
        var reconsiderStars = double.TryParse(
            Environment.GetEnvironmentVariable("RECONSIDER_MIN_AVG_STARS"), out var stars) ? stars : 3;
        var secondThoughtsStars = double.TryParse(
            Environment.GetEnvironmentVariable("RECONSIDER_MAX_AVG_STARS"), out var maxStars) ? maxStars : 2;
        var reconsiderFraction = double.TryParse(
            Environment.GetEnvironmentVariable("RECONSIDER_MIN_RATED_FRACTION"), out var frac) ? frac : 1.0 / 3;
        var reconsiderDays = double.TryParse(
            Environment.GetEnvironmentVariable("RECONSIDER_SWEEP_INTERVAL_DAYS"), out var rd) ? rd : 7;
        builder.RegisterInstance(new ReconsiderPolicy(
            MinAverage: reconsiderStars,
            MaxAverage: secondThoughtsStars,
            MinRatedFraction: reconsiderFraction,
            Interval: TimeSpan.FromDays(reconsiderDays),
            StartupDelay: TimeSpan.FromMinutes(10)));

        // The two daily passes run at a wall-clock hour (env knob, default 6am server-local) rather than
        // 24h after boot: Plex re-files new music on its own nightly pass, and a catalog read that
        // drifted to just before it would leave a finished download undetected for another whole day.
        var syncHour = int.TryParse(
            Environment.GetEnvironmentVariable("DAILY_SYNC_HOUR"), out var sh) && sh is >= 0 and <= 23 ? sh : 6;
        builder.RegisterInstance(new DailySyncSchedule(
            CatalogSync: new TimeOnly(syncHour, 0),
            AlbumSync: new TimeOnly(syncHour, 0).AddMinutes(30)));

        // Every recurring wait in the app is scattered by this much (env knob, default ±30%) instead of
        // firing on an exact cadence — see JitterPolicy.
        var jitterPercent = double.TryParse(
            Environment.GetEnvironmentVariable("TIMER_JITTER_PERCENT"), out var j) ? j : 30;
        builder.RegisterInstance(new JitterPolicy(jitterPercent / 100));

        builder.RegisterInstance(
            new PlexEndpointInfo(Environment.GetEnvironmentVariable("PLEX_ENDPOINT") ?? throw new InvalidOperationException()));
        builder.RegisterInstance(
            new PlexClientInfo(Environment.GetEnvironmentVariable("PLEX_TOKEN") ?? throw new InvalidOperationException()));
        builder.RegisterType<HttpClient>().AsSelf().SingleInstance();

        // Deezer download subsystem (env-driven; the ARL lives in streamrip's own config, which
        // DeezerCredentialService can rewrite in place when the user pastes a new one). streamrip is
        // always the backend. Whether the drainer runs unattended isn't configured here at all — it's
        // the Download page's switch, stored in Mongo (DownloadSettings) — and manual "download now"
        // works regardless. DownloadService is a shared singleton hosted service so the
        // endpoint that enqueues a manual download and the loop that drains it are the same instance.
        builder.RegisterInstance(BuildDownloaderConfig());
        builder.RegisterType<StreamripDownloader>().As<IDownloader>().AsSelf().SingleInstance();
        builder.RegisterType<DownloadService>().AsSelf().As<IHostedService>().SingleInstance();

        // The after-the-click worker: endpoints record a verdict/correction and hand it the graph
        // expansion, queue rebuild and Plex tag write. Same shape as DownloadService — a shared
        // singleton hosted service, so the endpoints that enqueue and the loop that drains are the
        // same instance — and, like it, registered by hand: Services.Background isn't assembly-scanned.
        builder.RegisterType<ArtistFollowUpService>().AsSelf().As<IHostedService>().SingleInstance();
        // Replacing the ARL from the Download page. The Services.Download namespace isn't part of the
        // assembly scan below (which covers Services.Singletons), so these are registered by hand like
        // the two above.
        builder.RegisterType<StreamripArlStore>().AsSelf().SingleInstance();
        builder.RegisterType<DeezerCredentialService>().AsSelf().SingleInstance();

        // Post-download Plex rescan (PlexLibraryScanner auto-registers as ILibraryScanner via the
        // assembly scan below). Off unless PLEX_RESCAN_AFTER_DOWNLOAD is set; debounce defaults to 5m.
        builder.RegisterInstance(BuildLibraryScannerConfig());

        builder.RegisterAssemblyTypes(typeof(LibraryProvider).Assembly)
            .InNamespacesOf(
                typeof(LibraryProvider))
            // JitterPolicy lives in this namespace but is configured above from the environment — the
            // scan would otherwise re-register it and fail, since its constructor takes a plain double.
            .Except<JitterPolicy>()
            .AsImplementedInterfaces()
            .AsSelf()
            .SingleInstance();
    }

    /// <summary>
    /// The quality ladder to walk when the preferred quality doesn't yield every track.
    ///
    /// Unset (the normal case) derives the full ladder from <paramref name="preferred"/> — every step
    /// below it, down to 0 — so "keep downgrading until Deezer will actually serve the track" needs no
    /// configuration and can't be left half-written. A stale hand-listed chain is the failure mode
    /// worth designing out: stopping at 320 on an album whose remaining tracks are 128-only loses them
    /// silently, because streamrip's own retry targets a CDN Deezer retired.
    ///
    /// An explicit <c>DEEZER_FALLBACK_QUALITY</c> still wins (comma-separated, best first); set it to
    /// blank to refuse downgrades entirely and accept the gaps instead.
    /// </summary>
    internal static IReadOnlyList<string> ParseQualities(string? raw, string preferred)
    {
        if (raw is not null)
        {
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        // Non-numeric preferred quality (a typo, or a future streamrip naming things differently):
        // no ladder can be inferred, so don't invent one.
        return int.TryParse(preferred, out var top) && top > 0
            ? Enumerable.Range(0, top).Reverse().Select(q => q.ToString()).ToArray()
            : Array.Empty<string>();
    }

    private static DownloaderConfig BuildDownloaderConfig()
    {
        static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";
        static double EnvDouble(string name, double fallback) =>
            double.TryParse(Environment.GetEnvironmentVariable(name), out var d) ? d : fallback;
        static int EnvInt(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var i) ? i : fallback;

        var quality = Environment.GetEnvironmentVariable("DEEZER_QUALITY") ?? "2"; // streamrip: 2 = FLAC

        return new DownloaderConfig(
            DownloadDir: Env("MUSIC_DOWNLOAD_DIR"),
            RipBinary: Environment.GetEnvironmentVariable("STREAMRIP_BIN") ?? "rip",
            Quality: quality,
            // Unset: every step below the preferred quality, best first (2 -> "1","0"). See
            // ParseQualities for why the ladder is derived rather than listed.
            FallbackQualities: ParseQualities(
                Environment.GetEnvironmentVariable("DEEZER_FALLBACK_QUALITY"), quality),
            Codec: Env("DEEZER_CODEC"), // empty = streamrip default (keep source codec)
            BatchSize: EnvInt("DOWNLOAD_BATCH_SIZE", 3),
            ItemDelay: TimeSpan.FromSeconds(EnvDouble("DOWNLOAD_ITEM_DELAY_SECONDS", 60)),
            BatchInterval: TimeSpan.FromMinutes(EnvDouble("DOWNLOAD_BATCH_INTERVAL_MINUTES", 30)),
            DownloadTimeout: TimeSpan.FromMinutes(EnvDouble("DEEZER_DOWNLOAD_TIMEOUT_MINUTES", 15)),
            SettleInterval: TimeSpan.FromMinutes(EnvDouble("DOWNLOAD_SETTLE_INTERVAL_MINUTES", 15)),
            SettleWindow: TimeSpan.FromHours(EnvDouble("DOWNLOAD_SETTLE_WINDOW_HOURS", 6)));
    }

    private static LibraryScannerConfig BuildLibraryScannerConfig()
    {
        var enabled = Environment.GetEnvironmentVariable("PLEX_RESCAN_AFTER_DOWNLOAD") is var v
                      && (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));
        var debounceMinutes = double.TryParse(
            Environment.GetEnvironmentVariable("PLEX_RESCAN_DEBOUNCE_MINUTES"), out var m) ? m : 5;
        return new LibraryScannerConfig(enabled, TimeSpan.FromMinutes(debounceMinutes));
    }
}