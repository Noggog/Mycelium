using System.Text.Json.Nodes;

namespace Mycelium.Interfaces;

/// <summary>
/// A raw, unfiltered read of one persistence collection, handed back as neutral JSON.
///
/// <para>Deliberately not expressed in terms of the domain records: those are lossy. An
/// <see cref="ArtistRating"/> drops <c>decidedAt</c> and the sticky <c>likeConfirmed</c> /
/// <c>dislikeConfirmed</c> flags; <see cref="AlbumRating"/> drops the same. Archiving through them
/// would quietly discard the hand-made decisions most worth keeping, and the loss would be invisible
/// until a restore. So the archive reads whole documents and decides what to keep itself.</para>
///
/// <para><see cref="JsonObject"/> rather than the driver's document type because this contract lives
/// in the project that must not know about Mongo. The implementation is responsible for flattening
/// storage-specific types into plain JSON (dates to ISO-8601 UTC strings, 64-bit ints to numbers) so
/// that everything downstream is storage-agnostic and unit-testable without a database.</para>
/// </summary>
public interface IArchiveDump
{
    /// <summary>
    /// Every document in <paramref name="collection"/>, in no particular order — the caller sorts,
    /// since sort order is part of the archive format rather than of the read. An unknown collection
    /// yields an empty list rather than throwing: a collection that no code has written to yet simply
    /// doesn't exist, and that is not an error worth failing a nightly snapshot over.
    /// </summary>
    Task<IReadOnlyList<JsonObject>> Dump(string collection);
}
