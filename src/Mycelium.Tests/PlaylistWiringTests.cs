using Autofac;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.Plex;
using Mycelium.Plex.Services.Singletons;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// Container wiring for the playlist feature. Everything new is picked up by an assembly <em>scan</em>
/// rather than registered by hand, which fails silently at compile time and loudly at first request —
/// on someone else's deployment, after the code is already pushed. Resolving each new service here moves
/// that failure to the build.
/// </summary>
public class PlaylistWiringTests : IDisposable
{
    private readonly IContainer _container;

    public PlaylistWiringTests()
    {
        // MainModule reads these at registration time and throws without them. Values are never dialled:
        // MongoClient resolves its connection lazily and no Plex call is made by resolution alone.
        Environment.SetEnvironmentVariable("PLEX_ENDPOINT", "http://plex.invalid:32400");
        Environment.SetEnvironmentVariable("PLEX_TOKEN", "test-token");
        Environment.SetEnvironmentVariable("MONGO_URI", "mongodb://mongo.invalid:27017");

        var builder = new ContainerBuilder();
        builder.RegisterModule<MainModule>();
        // The host normally supplies logging; a bare container has none.
        builder.RegisterInstance<ILoggerFactory>(NullLoggerFactory.Instance);
        builder.RegisterGeneric(typeof(Logger<>)).As(typeof(ILogger<>)).SingleInstance();
        _container = builder.Build();
    }

    public void Dispose() => _container.Dispose();

    [Theory]
    // The services the two new endpoint groups take as parameters — if these don't resolve, every
    // request to /api/playlists and /api/plex/link fails.
    [InlineData(typeof(SmartPlaylistService))]
    [InlineData(typeof(PlexLinkService))]
    // ...and the seams they depend on, each of which is registered by a different module's scan.
    [InlineData(typeof(IPlexPlaylistApi))]
    [InlineData(typeof(IPlexAccountApi))]
    [InlineData(typeof(IPlexLinkRepo))]
    [InlineData(typeof(PlexAppIdentity))]
    public void Playlist_services_resolve(Type service)
    {
        _container.Invoking(c => c.Resolve(service))
            .Should().NotThrow($"{service.Name} is reached by an assembly scan, not a hand-written registration");
    }

    // Whether ASP.NET can see these as *services* rather than trying to bind them from the request body
    // is settled by precedent: every existing endpoint takes its Autofac-registered service the same way,
    // through the same AutofacServiceProviderFactory. It can't be checked from a bare container here —
    // one built without the host's ServiceCollection has no IServiceProviderIsService at all.
}
