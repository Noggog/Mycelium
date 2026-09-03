namespace Mycelium.Backend;

/// <summary>
/// How hard the album-identity backfill leans on MusicBrainz.
///
/// <para>Declared in the root namespace, not in <c>Services</c>, so the Autofac assembly scan can't
/// shadow the registered instance with a reflected one it has no constructor arguments for — the same
/// reason <see cref="MetadataArchiveConfig"/> and <c>LibraryScannerConfig</c> live here.</para>
/// </summary>
/// <param name="BatchSize">
/// Albums to look up per daily pass. MusicBrainz allows roughly one request a second, so this is also
/// the pass's duration in seconds: the default of 2,000 is a little over half an hour of steady
/// traffic, which fills a large library inside a few weeks and then costs nothing. Zero or less turns
/// the backfill off.
/// </param>
public record AlbumIdentityConfig(int BatchSize)
{
    /// <summary>Whether the backfill runs at all.</summary>
    public bool Enabled => BatchSize > 0;
}
