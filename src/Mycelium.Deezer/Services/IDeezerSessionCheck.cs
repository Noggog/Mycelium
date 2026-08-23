namespace Mycelium.Deezer.Services;

/// <summary>The verdict on a Deezer ARL, and who it belongs to when it's good.</summary>
/// <param name="Valid">True when Deezer accepted the token and returned a real user.</param>
/// <param name="AccountName">The Deezer account the token authenticates as, when known — echoed back
/// so a paste can be confirmed as the right account rather than merely a well-formed string.</param>
/// <param name="Lossless">Whether that account is entitled to lossless streaming. Not a validity
/// concern, but it explains a later class of "downloads keep falling back to MP3" confusion, so it's
/// worth reporting at the moment the credential is set.</param>
/// <param name="Error">Why the check failed (transport/parse), when it couldn't reach a verdict at
/// all. Distinct from <c>Valid == false</c>, which is Deezer positively rejecting the token.</param>
public record DeezerSessionInfo(bool Valid, string? AccountName, bool Lossless, string? Error = null);

/// <summary>
/// Checks whether a Deezer ARL is still good, without going through streamrip. streamrip only reports
/// a bad token by raising mid-download, so this is what lets the app validate a pasted ARL before
/// saving it — and, in principle, notice an expiry before a queue drains into identical failures.
/// </summary>
public interface IDeezerSessionCheck
{
    /// <summary>
    /// Asks Deezer whether this ARL still authenticates. Never throws: a transport failure comes back
    /// as <see cref="DeezerSessionInfo.Error"/> so a Deezer outage is never mistaken for a dead token
    /// (which would otherwise send the user chasing a credential that was fine).
    /// </summary>
    Task<DeezerSessionInfo> Check(string arl);
}
