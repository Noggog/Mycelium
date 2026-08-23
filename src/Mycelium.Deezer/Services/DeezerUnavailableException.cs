namespace Mycelium.Deezer.Services;

/// <summary>
/// Deezer never answered a call we needed an answer from — a transport failure, or (far more often)
/// the rate-limit quota, which Deezer serves as a 200 whose body is an error envelope.
///
/// Thrown only where a missing answer would otherwise be recorded as fact: the discography diff,
/// which persists what it sees. Everywhere else an unanswered Deezer call is still degraded
/// gracefully — a card without a photo is fine, a discography silently emptied to nothing is not.
/// It surfaces to the SPA as a 503 so the client can retry and say "Deezer is busy" instead of
/// caching "this artist has no albums" as the answer.
/// </summary>
public class DeezerUnavailableException : Exception
{
    public DeezerUnavailableException(string message) : base(message)
    {
    }
}
