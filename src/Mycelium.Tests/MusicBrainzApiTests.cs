using System.Web;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.ListenBrainz.Inputs;
using Mycelium.ListenBrainz.Services;
using Xunit;
using Reply = Mycelium.Tests.LoopbackHttpServer.Reply;

namespace Mycelium.Tests;

/// <summary>
/// The browse and lookup calls, against a loopback server standing in for MusicBrainz. The client is
/// the real one, gate and all (with no spacing, so the tests don't sit out a second per request).
/// </summary>
public class MusicBrainzApiTests : IDisposable
{
    private const string Vulture = "56cda0cf-5d98-4f34-9049-c3e744f65edd";

    private readonly LoopbackHttpServer _server = new();
    private readonly MusicBrainzGate _gate;
    private readonly MusicBrainzApi _sut;

    public MusicBrainzApiTests()
    {
        var endpoint = new ListenBrainzEndpointInfo(
            _server.BaseUri, "https://lb", "contact", "algo", Enabled: true, TimeSpan.Zero);
        _gate = new MusicBrainzGate(endpoint);
        _sut = new MusicBrainzApi(endpoint, _gate, NullLogger<MusicBrainzApi>.Instance);
    }

    public void Dispose()
    {
        _gate.Dispose();
        _server.Dispose();
    }

    private static string ReleaseGroups(int total, int from, int count) =>
        "{\"release-group-count\":" + total + ",\"release-groups\":["
        + string.Join(",", Enumerable.Range(from, count).Select(i =>
            "{\"id\":\"rg-" + i + "\",\"title\":\"Album " + i + "\",\"primary-type\":\"Album\","
            + "\"secondary-types\":[\"Live\"],\"first-release-date\":\"2020-01-0" + (i % 9 + 1) + "\"}"))
        + "]}";

    private static int Offset(System.Net.HttpListenerRequest request) =>
        int.Parse(request.QueryString["offset"] ?? "0");

    [Fact]
    public async Task Browsing_release_groups_walks_every_page()
    {
        _server.Respond = r => new Reply(200, ReleaseGroups(150, Offset(r), Offset(r) == 0 ? 100 : 50));

        var groups = await _sut.BrowseReleaseGroups(Vulture);

        groups.Should().HaveCount(150);
        groups![0].SecondaryTypes.Should().Equal("Live");
        groups[0].FirstReleaseDate.Should().Be("2020-01-01");
        _server.Requests.Select(r => r.PathAndQuery).Should().SatisfyRespectively(
            first => first.Should().Contain($"artist={Vulture}").And.Contain("offset=0").And.Contain("limit=100"),
            second => second.Should().Contain("offset=100"));
    }

    [Fact]
    public async Task A_browse_with_an_unanswered_page_is_null_rather_than_partial()
    {
        _server.Respond = r => Offset(r) == 0
            ? new Reply(200, ReleaseGroups(150, 0, 100))
            : new Reply(500, "{}");

        (await _sut.BrowseReleaseGroups(Vulture)).Should().BeNull();
    }

    [Fact]
    public async Task Browsing_an_artist_MusicBrainz_does_not_have_is_an_empty_answer()
    {
        _server.Respond = _ => new Reply(404, "{\"error\":\"Not Found\"}");

        (await _sut.BrowseReleaseGroups(Vulture)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_rate_limited_request_is_retried_until_it_answers()
    {
        var calls = 0;
        _server.Respond = _ => ++calls == 1
            ? new Reply(503, "{}", RetryAfterSeconds: 0)
            : new Reply(200, ReleaseGroups(1, 0, 1));

        (await _sut.BrowseReleaseGroups(Vulture)).Should().HaveCount(1);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task Releases_are_browsed_official_and_downloadable_types_only_with_links_and_groups()
    {
        _server.Respond = _ => new Reply(200, """
            {"release-count":1,"releases":[{"id":"r1","title":"Sentinels","status":"Official",
              "barcode":"039841607321","date":"2024-04-12",
              "release-group":{"id":"rg1","title":"Sentinels","primary-type":"Album","secondary-types":[]},
              "relations":[{"type":"free streaming","target-type":"url","ended":false,
                "url":{"id":"u1","resource":"https://www.deezer.com/album/557958402"}}]}]}
            """);

        var releases = await _sut.BrowseReleases(Vulture);

        var release = releases.Should().ContainSingle().Subject;
        release.Barcode.Should().Be("039841607321");
        release.ReleaseGroup!.Id.Should().Be("rg1");
        release.Relations.Should().ContainSingle()
            .Which.Url!.Resource.Should().Be("https://www.deezer.com/album/557958402");

        var query = HttpUtility.ParseQueryString(new Uri(_server.BaseUri + _server.Requests.Single().PathAndQuery).Query);
        query["status"].Should().Be("official");
        query["type"].Should().Be("album|ep|single");
        query["inc"].Should().Be("release-groups url-rels");
    }

    [Fact]
    public async Task Looking_up_a_url_finds_the_artist_linked_to_it()
    {
        _server.Respond = _ => new Reply(200, """
            {"id":"u1","resource":"https://www.deezer.com/artist/291446","relations":[
              {"type":"free streaming","target-type":"artist","ended":false,
               "artist":{"id":"56cda0cf-5d98-4f34-9049-c3e744f65edd","name":"Vulture",
                         "disambiguation":"German speed/thrash metal band"}}]}
            """);

        var url = await _sut.LookupUrl("https://www.deezer.com/artist/291446");

        url!.Relations.Should().ContainSingle().Which.Artist!.Id.Should().Be(Vulture);
        _server.Requests.Single().PathAndQuery.Should()
            .Contain("resource=https%3a%2f%2fwww.deezer.com%2fartist%2f291446");
    }

    [Fact]
    public async Task A_url_MusicBrainz_has_never_heard_of_is_an_answer_with_nothing_linked()
    {
        _server.Respond = _ => new Reply(404, "{\"error\":\"Not Found\"}");

        var url = await _sut.LookupUrl("https://www.deezer.com/artist/1");

        url.Should().NotBeNull();
        url!.Relations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_url_lookup_that_goes_unanswered_is_null()
    {
        _server.Respond = _ => new Reply(500, "{}");

        (await _sut.LookupUrl("https://www.deezer.com/artist/1")).Should().BeNull();
    }
}
