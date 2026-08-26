using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Interfaces;
using Mycelium.Plex;
using Mycelium.Plex.Services.Singletons;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The server's machineIdentifier must be readable with <em>no</em> credential at all.
///
/// <para>It is what scopes a newly minted token to this server during linking, so if reading it needed
/// a working token, a dead one could never be replaced — and a deployment that had never linked could
/// never link a first one. Plex serves <c>/identity</c> unauthenticated while the root endpoint 401s,
/// which is the whole reason <see cref="PlexApi.GetMachineIdentifier"/> asks for the former.</para>
/// </summary>
public class PlexIdentityTests : IDisposable
{
    private const string MachineId = "26476bc2846b24eabc763a1b331d7153e4798968";

    private readonly HttpListener _listener = new();
    private readonly string _origin;
    private readonly List<string> _pathsSeen = new();
    private readonly List<string?> _tokensSeen = new();

    public PlexIdentityTests()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _origin = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"{_origin}/");
        _listener.Start();
        _ = Task.Run(Serve);
    }

    /// <summary>Stands in for Plex: /identity is open, everything else demands a token.</summary>
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
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "";
            lock (_pathsSeen)
            {
                _pathsSeen.Add(path);
                _tokensSeen.Add(context.Request.Headers["X-Plex-Token"]);
            }

            var open = path == "/identity";
            context.Response.StatusCode = (int)(open ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
            context.Response.ContentType = "application/json";
            var body = System.Text.Encoding.UTF8.GetBytes(
                $"{{\"MediaContainer\":{{\"machineIdentifier\":\"{MachineId}\"}}}}");
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }

    /// <summary>Nothing linked — what a deployment looks like before its first link.</summary>
    private sealed class NoTokenSource : IPlexTokenSource
    {
        public Task<string> Current() =>
            throw new PlexUnauthorizedException("No Plex token is configured.");

        public Task<PlexTokenResolution> Resolve() =>
            Task.FromResult(new PlexTokenResolution(null, null));

        public void Invalidate()
        {
        }
    }

    private PlexApi Api(IPlexTokenSource tokens) =>
        new(new PlexEndpointInfo(_origin), tokens, NullLogger<PlexApi>.Instance);

    [Fact]
    public async Task TheMachineIdIsReadableWithNothingLinkedAtAll()
    {
        // The bootstrap case: no credential exists yet, and the link flow still has to work.
        (await Api(new NoTokenSource()).GetMachineIdentifier()).Should().Be(MachineId);

        lock (_pathsSeen)
        {
            _pathsSeen.Should().ContainSingle().Which.Should().Be("/identity");
            _tokensSeen.Should().ContainSingle().Which.Should().BeNull();
        }
    }

    [Fact]
    public async Task TheMachineIdIsReadableWhileTheStoredTokenIsDead()
    {
        // The case that actually bit: re-linking must not depend on the credential being replaced.
        // The stub 401s anything but /identity, so a request to "/" here would throw instead.
        (await Api(new StaticPlexTokenSource("expired")).GetMachineIdentifier()).Should().Be(MachineId);

        lock (_pathsSeen)
        {
            _pathsSeen.Should().ContainSingle().Which.Should().Be("/identity");
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        ((IDisposable)_listener).Dispose();
    }
}
