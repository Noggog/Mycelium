using System.Text.Json;
using FluentAssertions;
using Mycelium.Interfaces;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The Reconcile page groups rows by these names. Serialised as numbers, every row falls through every
/// group and the page shows a count with nothing under it — which is how this test came to exist.
/// </summary>
public class ArtistResolutionJsonTests
{
    [Fact]
    public void Status_and_confidence_go_over_the_wire_as_names()
    {
        var resolution = new ArtistResolution(
            "Vulture", ArtistResolutionStatus.Resolved, ResolutionConfidence.Low, "mbid", "Vulture", null,
            null, 2, [], "Only the name matches.", DateTimeOffset.UnixEpoch);

        var json = JsonSerializer.Serialize(resolution, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.Should().Contain("\"status\":\"Resolved\"").And.Contain("\"confidence\":\"Low\"");
    }
}
