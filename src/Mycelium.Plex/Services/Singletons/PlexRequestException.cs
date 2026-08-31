using System.Net;

namespace Mycelium.Plex.Services.Singletons;

/// <summary>
/// A Plex request that didn't succeed, carrying <em>what</em> Plex said rather than only that
/// something went wrong.
///
/// <para>Left as a bare <see cref="HttpRequestException"/> these surface to the browser as a 500 with
/// no detail — "Couldn't create the playlist: 500" — which is indistinguishable from a bug in this app
/// and sends you into the server logs to learn that Plex returned a 400, or that the request timed out
/// while the server was still evaluating a filter across the whole library. The status and a snippet of
/// the body are the two things that tell those apart, so they travel with the exception.</para>
///
/// <para><see cref="Status"/> is null for a request that never got an answer at all — a timeout or a
/// dropped connection — which is a materially different failure: Plex may well have carried out the
/// write and simply not said so in time.</para>
/// </summary>
public class PlexRequestException : Exception
{
    public PlexRequestException(
        HttpMethod method, string path, HttpStatusCode? status, string? detail, Exception? inner = null)
        : base(Describe(method, path, status, detail), inner)
    {
        Method = method.Method;
        Path = path;
        Status = status;
        Detail = detail;
    }

    public string Method { get; }

    /// <summary>The request path, which never carries the token — that travels as a header.</summary>
    public string Path { get; }

    /// <summary>What Plex answered, or null when it never answered.</summary>
    public HttpStatusCode? Status { get; }

    /// <summary>A short excerpt of Plex's response body, when it sent one worth repeating.</summary>
    public string? Detail { get; }

    /// <summary>Whether Plex may have carried out the write anyway — true when it never answered.</summary>
    public bool Unanswered => Status is null;

    private static string Describe(
        HttpMethod method, string path, HttpStatusCode? status, string? detail)
    {
        // The path can carry a whole serialised filter; the leading segment is the part that names
        // which call failed, and the rest is noise in a message a user reads.
        var route = path.Split('?')[0];
        var what = status is null
            ? $"Plex didn't answer {method.Method} {route} in time"
            : $"Plex refused {method.Method} {route} with {(int)status} {status}";

        return string.IsNullOrWhiteSpace(detail) ? $"{what}." : $"{what}: {detail}";
    }
}
