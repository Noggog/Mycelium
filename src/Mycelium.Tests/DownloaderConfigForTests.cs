using Mycelium.Backend.Services.Download;

namespace Mycelium.Tests;

/// <summary>A downloader config for tests that need one but don't care what's in it.</summary>
internal static class DownloaderConfigForTests
{
    public static DownloaderConfig Default { get; } = new(
        DownloadDir: "", RipBinary: "rip", Quality: "2", FallbackQualities: new[] { "1", "0" },
        Codec: "", BatchSize: 1, ItemDelay: TimeSpan.Zero, BatchInterval: TimeSpan.Zero,
        DownloadTimeout: TimeSpan.FromMinutes(1), SettleInterval: TimeSpan.FromMinutes(15),
        SettleWindow: TimeSpan.FromHours(6), FastSettleInterval: TimeSpan.Zero,
        FastSettleWindow: TimeSpan.Zero);
}
