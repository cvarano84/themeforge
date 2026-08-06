using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

[Collection("Downloader environment")]
public sealed class YtDlpConcurrencyGateTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string? _old = Environment.GetEnvironmentVariable("YTDLP_CONCURRENT_DOWNLOADS");
    private readonly DownloaderConfiguration _configuration;

    public YtDlpConcurrencyGateTests()
    {
        Environment.SetEnvironmentVariable("YTDLP_CONCURRENT_DOWNLOADS", null);
        var db = new Database(Path.Combine(_dir.Path, "gate.db"));
        db.Init();
        _configuration = new DownloaderConfiguration(db);
        _configuration.Save("192K", 300, 1);
    }

    [Fact]
    public async Task Bounds_active_processes_and_releases_the_next_waiter()
    {
        var gate = new YtDlpConcurrencyGate(_configuration);
        using var first = await gate.AcquireAsync(CancellationToken.None);
        var second = gate.AcquireAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(second.IsCompleted);

        first.Dispose();
        using var acquired = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(acquired);
    }

    [Fact]
    public async Task Raising_the_limit_allows_a_waiter_without_restarting_the_gate()
    {
        var gate = new YtDlpConcurrencyGate(_configuration);
        using var first = await gate.AcquireAsync(CancellationToken.None);
        var second = gate.AcquireAsync(CancellationToken.None);
        _configuration.Save("192K", 300, 2);

        using var acquired = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(acquired);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("YTDLP_CONCURRENT_DOWNLOADS", _old);
        _dir.Dispose();
    }
}
