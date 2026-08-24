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
                // Album-artist and Deezer id are sticky once learned (mirrors the Mongo repo): don't
                // null them out. A row that lost its id would be permanently un-downloadable.
                AlbumArtist = item.AlbumArtist ?? existing.AlbumArtist,
                DeezerAlbumId = item.DeezerAlbumId ?? existing.DeezerAlbumId,
                Manual = existing.Manual,
                // What a previous download produced is history, not a display field — a reconcile
                // must not erase it. (The Mongo repo gets this for free: its upsert never names this
                // field, so the stored value is left alone. Here the whole record is replaced, so it
                // has to be carried across explicitly.) TargetQuality is deliberately NOT carried:
                // it is recomputed from who currently wants the album, and must be free to rise.
                AcquiredQuality = existing.AcquiredQuality,
            }
            : item with { Status = PurchaseStatus.Pending };
        return Task.CompletedTask;
    }

    public Task<bool> SetStatus(
        string id,
        PurchaseStatus status,
        DownloadFailure failure = DownloadFailure.None,
        AudioQuality? acquired = null)
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
            // Also mirrors the Mongo repo: only ever written, never cleared, so a backend that
            // couldn't say what it got doesn't erase what an earlier attempt reported.
            AcquiredQuality = acquired ?? item.AcquiredQuality,
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
