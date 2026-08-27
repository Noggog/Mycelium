using Mycelium.Interfaces;

namespace Mycelium.Tests;

/// <summary>
/// In-memory <see cref="IApiTokenRepo"/>. A hand-written fake rather than a substitute because the
/// tests here care what was <em>stored</em> — chiefly that it isn't the token — and asserting that
/// against a real dictionary reads better than reconstructing it from captured calls.
/// </summary>
public class FakeApiTokenRepo : IApiTokenRepo
{
    public Dictionary<string, ApiTokenRecord> Rows { get; } = new(StringComparer.Ordinal);

    public Task Add(ApiTokenRecord token)
    {
        Rows[token.Id] = token;
        return Task.CompletedTask;
    }

    public Task<ApiTokenRecord?> Get(string id) =>
        Task.FromResult(Rows.TryGetValue(id, out var row) ? row : null);

    public Task<ApiTokenRecord[]> GetForSubject(string subject) =>
        Task.FromResult(Rows.Values
            .Where(t => t.Subject == subject)
            .OrderByDescending(t => t.CreatedAt)
            .ToArray());

    public Task<bool> Revoke(string id, string subject, DateTimeOffset at)
    {
        if (!Rows.TryGetValue(id, out var row) || row.Subject != subject || row.RevokedAt is not null)
        {
            return Task.FromResult(false);
        }

        Rows[id] = row with { RevokedAt = at };
        return Task.FromResult(true);
    }
}
