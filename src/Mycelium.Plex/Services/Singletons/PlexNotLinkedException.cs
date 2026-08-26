namespace Mycelium.Plex.Services.Singletons;

/// <summary>
/// No Plex credential exists at all — the state of a deployment that has never linked one, and of one
/// that has just disconnected.
///
/// <para>A subclass of <see cref="PlexUnauthorizedException"/> because every caller that handles a
/// refused token handles this the same way: the library can't be read, serve what's stored. It is a
/// distinct type only so the few places that talk to a person can say "not connected yet" instead of
/// "your token expired" — advice to re-mint a credential that was never minted reads as a bug.</para>
/// </summary>
public class PlexNotLinkedException : PlexUnauthorizedException
{
    public PlexNotLinkedException(string message) : base(message)
    {
    }
}
