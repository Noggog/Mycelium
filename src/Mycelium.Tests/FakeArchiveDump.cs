using System.Text.Json.Nodes;
using Mycelium.Interfaces;

namespace Mycelium.Tests;

/// <summary>
/// An in-memory stand-in for the Mongo dump, so the archiver can be exercised end to end — real git
/// repository, real files on disk — without a database.
/// </summary>
public class FakeArchiveDump : IArchiveDump
{
    private readonly Dictionary<string, List<JsonObject>> _collections = new(StringComparer.Ordinal);

    public FakeArchiveDump Set(string collection, params JsonObject[] documents)
    {
        _collections[collection] = documents.ToList();
        return this;
    }

    /// <summary>An unknown collection reads as empty, exactly as the real one does.</summary>
    public Task<IReadOnlyList<JsonObject>> Dump(string collection) =>
        Task.FromResult<IReadOnlyList<JsonObject>>(
            _collections.TryGetValue(collection, out var docs) ? docs : []);
}
