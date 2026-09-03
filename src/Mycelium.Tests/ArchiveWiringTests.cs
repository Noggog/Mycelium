using Autofac;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mycelium.Backend;
using Mycelium.Backend.Services.Archive;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// Container wiring for the metadata archive and the two Plex harvests.
///
/// <para>Worth its own test for the reason <see cref="PlaylistWiringTests"/> gives, doubled: the
/// archive services are registered <em>by hand</em> in <c>MainModule</c> because
/// <c>Services.Archive</c> sits outside the assembly scan, while the harvesters and the two new repos
/// are picked up <em>by</em> scans in three different modules. Either kind of mistake compiles
/// perfectly and fails at runtime — and this feature's failures are especially quiet, because nothing
/// reads the archive back. A snapshot that never ran looks exactly like a night where nothing
/// changed.</para>
/// </summary>
public class ArchiveWiringTests : IDisposable
{
    private readonly IContainer _container;

    public ArchiveWiringTests()
    {
        // MainModule reads these at registration time and throws without them. Never dialled: Mongo
        // resolves its connection lazily and resolution alone makes no Plex call.
        Environment.SetEnvironmentVariable("PLEX_ENDPOINT", "http://plex.invalid:32400");
        Environment.SetEnvironmentVariable("MONGO_URI", "mongodb://mongo.invalid:27017");

        var builder = new ContainerBuilder();
        builder.RegisterModule<MainModule>();
        builder.RegisterInstance<ILoggerFactory>(NullLoggerFactory.Instance);
        builder.RegisterGeneric(typeof(Logger<>)).As(typeof(ILogger<>)).SingleInstance();
        builder.RegisterInstance<IDistributedCache>(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        _container = builder.Build();
    }

    public void Dispose() => _container.Dispose();

    [Theory]
    // Registered by hand, because Services.Archive is outside the assembly scan.
    [InlineData(typeof(MetadataArchiver))]
    [InlineData(typeof(ArchiveBuilder))]
    [InlineData(typeof(IGitRepository))]
    [InlineData(typeof(MetadataArchiveConfig))]
    // The takeout, which reuses the same builder. Registered by hand next to them, and notably not
    // dependent on MetadataArchiveConfig — a deployment with no archive repository still owes people
    // their own data.
    [InlineData(typeof(TakeoutBuilder))]
    // Reached by the MongoDB module's scan — the archive's only read path.
    [InlineData(typeof(IArchiveDump))]
    // The two harvesters, reached by the Backend scan, plus the stores they write to, reached by the
    // MongoDB one. A missing registration here would mean stars and playlists silently never mirrored
    // — and those are the two things that exist nowhere but Plex, so the loss would be permanent.
    [InlineData(typeof(StarHarvester))]
    [InlineData(typeof(PlaylistHarvester))]
    [InlineData(typeof(IUserTrackRatingRepo))]
    [InlineData(typeof(IUserPlaylistRepo))]
    // The library's track listing, which is what lets an album file carry a real one.
    [InlineData(typeof(ILibraryTrackRepo))]
    public void Archive_services_resolve(Type service)
    {
        _container.Invoking(c => c.Resolve(service))
            .Should().NotThrow($"{service.Name} must resolve or the archive silently never runs");
    }

    [Fact]
    public void Archiving_is_off_when_no_repository_path_is_configured()
    {
        // The deployment default. A stack that never sets METADATA_REPO_PATH must behave exactly as it
        // did before any of this existed.
        Environment.SetEnvironmentVariable("METADATA_REPO_PATH", null);

        var builder = new ContainerBuilder();
        builder.RegisterModule<MainModule>();
        builder.RegisterInstance<ILoggerFactory>(NullLoggerFactory.Instance);
        builder.RegisterGeneric(typeof(Logger<>)).As(typeof(ILogger<>)).SingleInstance();
        builder.RegisterInstance<IDistributedCache>(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        using var container = builder.Build();

        container.Resolve<MetadataArchiveConfig>().Enabled.Should().BeFalse();
    }

    [Theory]
    // The ordinary way to authenticate a push to a self-hosted forge: the token *is* the URL.
    [InlineData(
        "https://noggog:abc123token@192.168.1.232:3300/noggog/MusicStatTracking.git",
        "https://***@192.168.1.232:3300/noggog/MusicStatTracking.git")]
    // Token-as-username, which some forges accept.
    [InlineData("https://abc123token@git.example.com/x/y.git", "https://***@git.example.com/x/y.git")]
    [InlineData("http://user:pw@host:3300/a/b.git", "http://***@host:3300/a/b.git")]
    // git echoes the URL back inside its own failure text, which is logged verbatim.
    [InlineData(
        "fatal: Authentication failed for 'https://noggog:abc123token@host/x.git'",
        "fatal: Authentication failed for 'https://***@host/x.git'")]
    // Nothing to hide, nothing changed — a local path or a credential-free URL passes through.
    [InlineData("https://git.example.com/x/y.git", "https://git.example.com/x/y.git")]
    [InlineData("/srv/appdata/mycelium/metadata", "/srv/appdata/mycelium/metadata")]
    public void A_credential_in_a_remote_url_is_never_rendered(string input, string expected)
    {
        MetadataArchiveConfig.Redact(input).Should().Be(expected);
    }

    [Fact]
    public void The_remote_shown_in_logs_carries_no_token()
    {
        // This is the line written to the rolling log file on every start, so it is the one that
        // matters most: a token there outlives the session and is read by whoever is debugging.
        var config = new MetadataArchiveConfig(
            RepoPath: "/archive",
            Remote: "https://noggog:abc123token@192.168.1.232:3300/noggog/MusicStatTracking.git",
            Branch: "main",
            SnapshotAt: new TimeOnly(8, 0),
            CommitName: "Mycelium",
            CommitEmail: "m@localhost",
            GitBinary: "git",
            CommandTimeout: TimeSpan.FromMinutes(5));

        config.SafeRemote.Should().NotContain("abc123token");
        config.SafeRemote.Should().Contain("MusicStatTracking");
        // ...while the value actually handed to git is untouched.
        config.Remote.Should().Contain("abc123token");
    }

    [Fact]
    public void The_snapshot_hour_is_anchored_past_the_daily_syncs()
    {
        // It must record a freshly-synced library rather than race one: the catalog read runs at the
        // sync hour and the Deezer album diff 30 minutes later.
        Environment.SetEnvironmentVariable("DAILY_SYNC_HOUR", "6");

        var builder = new ContainerBuilder();
        builder.RegisterModule<MainModule>();
        builder.RegisterInstance<ILoggerFactory>(NullLoggerFactory.Instance);
        builder.RegisterGeneric(typeof(Logger<>)).As(typeof(ILogger<>)).SingleInstance();
        builder.RegisterInstance<IDistributedCache>(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        using var container = builder.Build();

        container.Resolve<MetadataArchiveConfig>().SnapshotAt.Should().Be(new TimeOnly(8, 0));
        Environment.SetEnvironmentVariable("DAILY_SYNC_HOUR", null);
    }
}
