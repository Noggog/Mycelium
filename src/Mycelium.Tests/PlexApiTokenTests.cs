using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Plex;
using Mycelium.Plex.Services.Singletons;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// <see cref="PlexApi.AcceptsToken"/> against a loopback server, because the whole point of the method
/// is a header override and nothing but a real request proves it happened. <see cref="PlexApi"/> stamps
/// the app's own token onto every request that hasn't set one; if the per-request one didn't take
/// precedence, the pasted-token check would silently validate the *admin* token and accept any string
/// at all.
/// </summary>
public class PlexApiTokenTests : IDisposable
{
    private const string AppToken = "app-owner-token";

    private readonly HttpListener _listener = new();
    private readonly string _origin;
    private readonly List<string?> _tokensSeen = new();
    private HttpStatusCode _reply = HttpStatusCode.OK;

    public PlexApiTokenTests()
    {
        // Port 0 asks the OS for a free one, so parallel test runs can't collide.
        var port = FreePort();
        _origin = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"{_origin}/");
        _listener.Start();
        _ = Task.Run(Serve);
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
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
            catch (Exception)
            {
                return; // Listener stopped — the test is over.
            }

            lock (_tokensSeen)
            {
                _tokensSeen.Add(context.Request.Headers["X-Plex-Token"]);
            }

            context.Response.StatusCode = (int)_reply;
            context.Response.ContentType = "application/json";
            var body = "{\"MediaContainer\":{}}"u8.ToArray();
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }

    private PlexApi Api() => new(
        new PlexEndpointInfo(_origin), new StaticPlexTokenSource(AppToken), NullLogger<PlexApi>.Instance);

    private string? LastToken()
    {
        lock (_tokensSeen)
        {
            return _tokensSeen.Single();
        }
    }

    [Fact]
    public async Task AcceptsToken_AsksAsThePastedToken_NotAsTheApp()
    {
        var accepted = await Api().AcceptsToken("pasted-token");

        accepted.Should().BeTrue();
        // One value, and it's the pasted one: the client's default header was suppressed rather than
        // sent alongside it (which Plex would resolve to whichever it read first).
        LastToken().Should().Be("pasted-token");
    }

    [Fact]
    public async Task AcceptsToken_IsFalseWhenTheServerRefusesTheToken()
    {
        _reply = HttpStatusCode.Unauthorized;

        // Every other call turns a 401 into PlexUnauthorizedException; this is the one method whose
        // job is to answer the question, so it catches that and reports a verdict instead.
        (await Api().AcceptsToken("bad-token")).Should().BeFalse();
        LastToken().Should().Be("bad-token");
    }

    [Fact]
    public async Task ARefusedTokenSurfacesAsPlexUnauthorized_NotABareHttpFailure()
    {
        // The reason the dev panel can say "your token expired" instead of returning a naked 500.
        _reply = HttpStatusCode.Unauthorized;

        await Api().Invoking(a => a.GetLibraries())
            .Should().ThrowAsync<PlexUnauthorizedException>()
            .WithMessage("*no longer valid*");
    }

    [Fact]
    public async Task TheServerTokenIsStampedOnCallsThatDontCarryOne()
    {
        // Asked per request rather than fixed on the client, which is what lets a re-link take effect
        // without a restart. GetMachineIdentifier because it tolerates the stub's empty body — the
        // assertion is about the header, not the payload.
        await Api().GetMachineIdentifier();

        LastToken().Should().Be(AppToken);
    }

    [Fact]
    public async Task AcceptsToken_ThrowsOnAServerFault_RatherThanBlamingTheToken()
    {
        // A 500 is the server misbehaving. Reporting that as "your token is wrong" would send the user
        // off to re-copy a token that was fine.
        _reply = HttpStatusCode.InternalServerError;

        await Api().Invoking(a => a.AcceptsToken("good-token"))
            .Should().ThrowAsync<HttpRequestException>();
    }

    public void Dispose()
    {
        _listener.Stop();
        ((IDisposable)_listener).Dispose();
    }
}
