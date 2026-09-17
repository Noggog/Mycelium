using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Download;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;
using NSubstitute;

namespace Mycelium.Tests;

/// <summary>
/// An upgrade pre-check that never suppresses anything, for the tests that are about something else.
///
/// Built with no credential configured, which is the production path a deployment without an ARL
/// takes: nothing is probed and every candidate is offered. That makes it the honest default for a
/// test that cares about the diff rather than the pre-check — the rows a sweep produces here are the
/// rows it produced before the pre-check existed. <see cref="UpgradeAvailabilityTests"/> covers the
/// cases where it does something.
/// </summary>
public static class InertUpgradeAvailability
{
    public static UpgradeAvailability Instance()
    {
        var arl = Substitute.For<StreamripArlStore>(NullLogger<StreamripArlStore>.Instance);
        arl.Read().Returns((string?)null);

        var blocks = Substitute.For<IAlbumBlockRepo>();
        blocks.GetAll().Returns(Array.Empty<AlbumBlock>());

        return new UpgradeAvailability(
            Substitute.For<IDeezerQualityProbe>(), arl, blocks,
            NullLogger<UpgradeAvailability>.Instance);
    }
}
