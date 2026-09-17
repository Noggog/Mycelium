using Autofac;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mycelium.Backend;
using Mycelium.Backend.Services.Download;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Deezer.Services;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// Container wiring for the upgrade pre-check, which reaches across a registration boundary that
/// nothing else in <c>Services.Singletons</c> does.
///
/// <para><see cref="UpgradeAvailability"/> is picked up by the assembly scan, but it depends on
/// <see cref="StreamripArlStore"/>, which sits in <c>Services.Download</c> — outside that scan and
/// registered by hand. So the sweep's ability to resolve at all now rests on a line in
/// <c>MainModule</c> written for an unrelated feature (pasting an ARL from the Download page). Remove
/// or move that line and this compiles perfectly, then takes the whole missing-album sync down at
/// startup.</para>
/// </summary>
public class UpgradeWiringTests : IDisposable
{
    private readonly IContainer _container;

    public UpgradeWiringTests()
    {
        // MainModule reads these at registration time and throws without them. Never dialled:
        // resolution alone makes no call out.
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
    // Reached by the Deezer module's scan.
    [InlineData(typeof(IDeezerQualityProbe))]
    // Reached by the backend's Services.Singletons scan.
    [InlineData(typeof(UpgradeAvailability))]
    // Registered by hand, and the dependency this test exists for.
    [InlineData(typeof(StreamripArlStore))]
    // The consumer, which is what actually breaks if any of the above is missing.
    [InlineData(typeof(MissingAlbumRefresher))]
    public void Upgrade_pre_check_services_resolve(Type service) =>
        _container.Resolve(service).Should().NotBeNull();

    [Fact]
    public void The_probe_is_a_singleton()
    {
        // It holds a minted gateway session and paces calls against a shared timestamp. Per-dependency
        // instances would each mint their own session and none of them would see the others' pacing,
        // which is precisely the volume signature worth not producing on a private endpoint.
        _container.Resolve<IDeezerQualityProbe>()
            .Should().BeSameAs(_container.Resolve<IDeezerQualityProbe>());
    }
}
