using Autofac;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mycelium.Backend;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.ListenBrainz.Services;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The MusicBrainz plumbing resolves from the real container — and the gate is one instance. Two gates
/// would each keep to one request a second, together two, and MusicBrainz would start refusing.
/// </summary>
public class MusicBrainzWiringTests : IDisposable
{
    private readonly IContainer _container;

    public MusicBrainzWiringTests()
    {
        // MainModule reads these at registration time and throws without them. Never dialled.
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
    [InlineData(typeof(IMusicBrainzApi))]
    [InlineData(typeof(SourceCache))]
    [InlineData(typeof(ISourceCacheStore))]
    public void Resolves(Type service)
    {
        _container.Invoking(c => c.Resolve(service)).Should().NotThrow();
    }

    [Fact]
    public void There_is_one_gate()
    {
        _container.Resolve<MusicBrainzGate>().Should().BeSameAs(_container.Resolve<MusicBrainzGate>());
    }
}
