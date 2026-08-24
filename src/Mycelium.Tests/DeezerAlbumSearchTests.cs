using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Deezer.Inputs;
using Mycelium.Deezer.Services;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The album-search walk, against a loopback HTTP server standing in for Deezer. Every other Deezer
/// call is a single request whose parsing is covered by the callers' tests; this one pages, and its
/// paging is easy to get subtly wrong — Deezer answers index 0 of a 230-result search with 87 rows
/// <em>and</em> a next link, so a walk that steps by rows received re-asks for ground it has covered
/// and never reaches the releases the discography listing left out (which is the entire point of the
/// call). <see cref="DeezerEndpointInfo"/>'s base URI is the seam; the client under test is the real
/// one, throttle, error-envelope handling and all.
/// </summary>
public class DeezerAlbumSearchTests : IDisposable
{
    private const long ArtistId = 7;

    private readonly HttpListener _listener = new();
    private readonly DeezerApi _sut;

    // What the server was asked for, in order, and what it should answer with per index.
    private readonly List<int> _indexes = new();
    private readonly List<string?> _queries = new();
    private readonly Dictionary<int, string> _pages = new();
    private readonly HashSet<int> _failures = new();

    public DeezerAlbumSearchTests()
    {
        var baseUri = $"http://127.0.0.1:{FreePort()}";
        _listener.Prefixes.Add($"{baseUri}/");
        _listener.Start();
        _ = Task.Run(Serve);
        _sut = new DeezerApi(new DeezerEndpointInfo(baseUri), NullLogger<DeezerApi>.Instance);
    }

    public void Dispose() => _listener.Close();

    /// <summary>A page of search results: <paramref name="more"/> is Deezer's "there is another page".</summary>
    private static string Page(bool more, params (long Id, string Title)[] albums)
    {
        var rows = albums.Select(a =>
            "{\"id\":" + a.Id + ",\"title\":\"" + a.Title + "\",\"record_type\":\"album\""
            + ",\"artist\":{\"id\":" + ArtistId + "}}");
        var next = more ? ",\"next\":\"http://deezer.invalid/next\"" : string.Empty;
        return "{\"data\":[" + string.Join(",", rows) + "]" + next + "}";
    }

    [Fact]
    public async Task The_walk_steps_by_the_page_size_even_when_a_page_comes_back_short()
    {
        // A short page is not the last page. Deezer thins a page after paginating it, so page one of a
        // three-page search can arrive with a handful of rows and still be followed by two more.
        _pages[0] = Page(more: true, (1, "short page"));
        _pages[100] = Page(more: true, (2, "second page"));
        _pages[200] = Page(more: false, (3, "last page"));

        var found = await _sut.SearchArtistAlbums("Milo");

        _indexes.Should().Equal(0, 100, 200);
        found!.Select(a => a.title).Should().Equal("short page", "second page", "last page");
    }

    [Fact]
    public async Task The_search_is_scoped_to_the_artist_field()
    {
        // artist:"..." keeps the name together as one term. Without it the search answers with every
        // album whose title merely shares a word with the artist's name.
        _pages[0] = Page(more: false, (1, "an album"));

        await _sut.SearchArtistAlbums("Walk Off the Earth");

        _queries.Single().Should().Be("artist:\"Walk Off the Earth\"");
    }

    [Fact]
    public async Task A_page_that_never_answers_makes_the_whole_walk_null()
    {
        // Half a catalog would read to the caller as the whole one — and it persists the difference —
        // so a page that fails takes the whole answer down with it. Deezer's rate-limit refusal (a 200
        // wrapping an error envelope) lands here too, once the client has waited out its retries.
        _pages[0] = Page(more: true, (1, "first page"));
        _failures.Add(100);

        var found = await _sut.SearchArtistAlbums("Milo");

        found.Should().BeNull();
    }

    [Fact]
    public async Task The_walk_is_bounded_when_deezer_keeps_offering_more()
    {
        // Every page claims another follows. The cap is what stops that being an unbounded crawl; the
        // client logs when it bites, and returns what it has rather than nothing.
        for (var index = 0; index <= 500; index += 100)
        {
            _pages[index] = Page(more: true, (index + 1, $"page at {index}"));
        }

        var found = await _sut.SearchArtistAlbums("Milo");

        _indexes.Should().Equal(0, 100, 200, 300, 400);
        found!.Should().HaveCount(5);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                return; // Listener closed — the test is over.
            }

            var index = int.TryParse(context.Request.QueryString["index"], out var i) ? i : 0;
            _indexes.Add(index);
            _queries.Add(context.Request.QueryString["q"]);

            if (_failures.Contains(index))
            {
                context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                context.Response.Close();
                continue;
            }

            var body = Encoding.UTF8.GetBytes(_pages.TryGetValue(index, out var page) ? page : Page(false));
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }
}
