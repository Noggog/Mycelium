using Mycelium.Interfaces;

namespace Mycelium.Tests;

/// <summary>
/// An in-memory <see cref="IPurchaseRepo"/> mirroring the Mongo upsert semantics (status/requestedAt
/// insert-only; display fields refreshed) so lifecycle transitions are real in tests.
/// </summary>
internal sealed class FakePurchaseRepo : IPurchaseRepo
{
    private readonly Dictionary<string, PurchaseItem> _items = new();

    public IReadOnlyCollection<PurchaseItem> Items => _items.Values;

    public Task<PurchaseItem[]> GetAll() => Task.FromResult(_items.Values.ToArray());

    public Task Upsert(PurchaseItem item)
    {
        _items[item.Id] = _items.TryGetValue(item.Id, out var existing)
            ? item with
            {
                Status = existing.Status,
                RequestedAt = existing.RequestedAt,
                SentAt = existing.SentAt,
                // Album-artist is sticky once learned (mirrors the Mongo repo): don't null it out.
                AlbumArtist = item.AlbumArtist ?? existing.AlbumArtist,
            }
            : item with { Status = PurchaseStatus.Pending };
        return Task.CompletedTask;
    }

    public Task<bool> SetStatus(string id, PurchaseStatus status, DownloadFailure failure = DownloadFailure.None)
    {
        if (!_items.TryGetValue(id, out var item))
        {
            return Task.FromResult(false);
        }
        _items[id] = item with
        {
            Status = status,
            SentAt = status == PurchaseStatus.Sent ? DateTimeOffset.UtcNow : item.SentAt,
            // Mirrors the Mongo repo: the reason is only meaningful on a Failed row, and any other
            // transition clears it so a retry doesn't inherit the last explanation.
            Failure = status == PurchaseStatus.Failed ? failure : DownloadFailure.None,
        };
        return Task.FromResult(true);
    }

    public Task Remove(string id)
    {
        _items.Remove(id);
        return Task.CompletedTask;
    }

    // Test helper: seed a row directly at a given status (e.g. to set up a pending download).
    public void Seed(PurchaseItem item) => _items[item.Id] = item;
}
