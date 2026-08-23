using Mycelium.Interfaces;

namespace Mycelium.Tests;

/// <summary>
/// In-memory <see cref="IAppSettingsRepo"/>. Unset by default (null), mirroring a store that's never
/// been written — which is what makes the environment default apply.
/// </summary>
internal sealed class FakeAppSettingsRepo : IAppSettingsRepo
{
    private bool? _automatic;
    private DateTimeOffset? _fastUntil;

    public Task<bool?> GetDownloadsAutomatic() => Task.FromResult(_automatic);

    public Task SetDownloadsAutomatic(bool automatic)
    {
        _automatic = automatic;
        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> GetDownloadsFastUntil() => Task.FromResult(_fastUntil);

    public Task SetDownloadsFastUntil(DateTimeOffset? until)
    {
        _fastUntil = until;
        return Task.CompletedTask;
    }
}
