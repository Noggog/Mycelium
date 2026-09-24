using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Deezer.Inputs;
using Mycelium.Deezer.Services;
using Xunit;
using Reply = Mycelium.Tests.LoopbackHttpServer.Reply;

namespace Mycelium.Tests;

/// <summary>
/// <c>GET /album/upc:{barcode}</c>, against a loopback server standing in for Deezer. What matters is
/// the three-way answer: an album, "no such barcode" (Deezer's code-800 error inside a 200), and no
/// answer at all — a matcher records the second and must not record the third.
/// </summary>
public class DeezerUpcLookupTests : IDisposable
{
    private readonly LoopbackHttpServer _server = new();
    private readonly DeezerApi _sut;

    public DeezerUpcLookupTests()
    {
        _sut = new DeezerApi(new DeezerEndpointInfo(_server.BaseUri), NullLogger<DeezerApi>.Instance);
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task A_known_barcode_answers_with_the_album_and_its_details()
    {
        _server.Respond = _ => new Reply(200, """
            {"id":904930042,"title":"Somewhere in Time (2015 Remaster)","upc":"190295524241",
             "label":"Parlophone UK","available":true,"record_type":"album",
             "contributors":[{"id":931,"name":"Iron Maiden","role":"Main"}]}
            """);

        var lookup = await _sut.GetAlbumByUpc("4050538463965");

        lookup!.Album!.id.Should().Be(904930042);
        lookup.Album.upc.Should().Be("190295524241");
        lookup.Album.available.Should().BeTrue();
        lookup.Album.contributors.Should().ContainSingle().Which.role.Should().Be("Main");
        // The barcode is sent exactly as given — Deezer won't find it with a leading zero dropped.
        _server.Requests.Single().PathAndQuery.Should().Be("/album/upc:4050538463965");
    }

    [Fact]
    public async Task An_unknown_barcode_is_an_answer_with_no_album()
    {
        _server.Respond = _ => new Reply(200,
            """{"error":{"type":"DataException","message":"no data","code":800}}""");

        var lookup = await _sut.GetAlbumByUpc("039841607321");

        lookup.Should().NotBeNull();
        lookup!.Album.Should().BeNull();
    }

    [Fact]
    public async Task Any_other_error_is_no_answer()
    {
        _server.Respond = _ => new Reply(200,
            """{"error":{"type":"Exception","message":"An error has occurred","code":100}}""");

        (await _sut.GetAlbumByUpc("039841607321")).Should().BeNull();
    }
}
