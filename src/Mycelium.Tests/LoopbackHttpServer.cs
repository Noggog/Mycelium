using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Mycelium.Tests;

/// <summary>
/// A loopback HTTP server standing in for an outside API, so a test can drive the real client —
/// throttling, retries, error handling and all — through its base-URI setting. Each request is
/// recorded and answered by <see cref="Respond"/>.
/// </summary>
internal sealed class LoopbackHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<(string PathAndQuery, DateTimeOffset At)> _requests = new();

    public LoopbackHttpServer()
    {
        BaseUri = $"http://127.0.0.1:{FreePort()}";
        _listener.Prefixes.Add($"{BaseUri}/");
        _listener.Start();
        _ = Task.Run(Serve);
    }

    public string BaseUri { get; }

    /// <summary>Answers a request: status, JSON body, and an optional Retry-After in seconds.</summary>
    public Func<HttpListenerRequest, Reply> Respond { get; set; } = _ => new Reply(200, "{}");

    public IReadOnlyList<(string PathAndQuery, DateTimeOffset At)> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public void Dispose() => _listener.Close();

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

            lock (_requests)
            {
                _requests.Add((context.Request.Url!.PathAndQuery, DateTimeOffset.UtcNow));
            }

            var reply = Respond(context.Request);
            context.Response.StatusCode = reply.Status;
            if (reply.RetryAfterSeconds is { } seconds)
            {
                context.Response.AddHeader("Retry-After", seconds.ToString());
            }
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(reply.Body));
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public sealed record Reply(int Status, string Body, int? RetryAfterSeconds = null);
}
