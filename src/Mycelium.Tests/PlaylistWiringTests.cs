using Autofac;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend;
using Mycelium.Backend.Services.Background;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.Plex;
using Mycelium.Plex.Services.Singletons;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// Container wiring for the playlist and collection features. Everything new is picked up by an assembly <em>scan</em>
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
        // ...and the distributed cache, which Aspire wires to Redis. The MusicBrainz resolver takes one,
        // so anything reaching the similarity graph needs it present to resolve at all. An in-memory
        // stand-in: this test only ever builds the graph, it never asks it anything.
        builder.RegisterInstance<IDistributedCache>(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
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

    [Theory]
    // The service behind /api/collections — records no artist's discography can reach.
    [InlineData(typeof(CollectionService))]
    // ...the seam it defers its Plex tag write to, which the background worker implements, and the
    // tagger behind that. The tagger in particular is only ever reached as an interface, so a missing
    // registration would surface as a silently untagged album rather than a failed request.
    [InlineData(typeof(IAlbumTagFollowUp))]
    [InlineData(typeof(IAlbumTagger))]
    // ...and the arrival repair that stamps a verdict once the download lands.
    [InlineData(typeof(AlbumTagBackfill))]
    public void Collection_services_resolve(Type service)
    {
        _container.Invoking(c => c.Resolve(service))
            .Should().NotThrow($"{service.Name} is reached by an assembly scan, not a hand-written registration");
    }

    /// <summary>
    /// The tag write is deferred to the background worker, and the endpoint that enqueues it must reach
    /// the <em>same</em> instance the host started — a second copy would accept work into a channel
    /// nothing is draining, and every collection verdict would quietly never reach Plex.
    /// </summary>
    [Fact]
    public void The_album_tag_seam_is_the_running_worker()
    {
        _container.Resolve<IAlbumTagFollowUp>()
            .Should().BeSameAs(_container.Resolve<ArtistFollowUpService>());
    }

    // Whether ASP.NET can see these as *services* rather than trying to bind them from the request body
    // is settled by precedent: every existing endpoint takes its Autofac-registered service the same way,
    // through the same AutofacServiceProviderFactory. It can't be checked from a bare container here —
    // one built without the host's ServiceCollection has no IServiceProviderIsService at all.
}
