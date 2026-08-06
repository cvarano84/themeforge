using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Services;
using Themearr.API.Services.Health;

namespace Themearr.API.Tests;

public sealed class YtDlpHealthCheckTests
{
    private sealed class FakeDiagnostics(DownloaderDiagnostics result) : IDownloaderDiagnosticsService
    {
        public int Calls { get; private set; }
        public Task<DownloaderDiagnostics> CheckAsync(bool forceRefresh = false, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private static DownloaderDiagnostics Result(bool ready, bool degraded, string status, string summary) =>
        new(ready, degraded, status, summary,
            new(ready, ready ? "available" : "missing", ready ? "2026.07.04" : null),
            new(ready, ready ? "available" : "missing", ready ? "7.1" : null),
            new(ready, ready ? "available" : "missing", ready ? "7.1" : null),
            new(!degraded, degraded ? "missing" : "available", degraded ? null : "2.9.4"),
            new(false, "none", false, true, false, false, 0, 0, null),
            new("auto", "notConfigured", true, false, null),
            "192K", 300, 1, false, false, false);

    [Theory]
    [InlineData(true, false, "healthy", HealthStatus.Healthy)]
    [InlineData(true, true, "degraded", HealthStatus.Degraded)]
    [InlineData(false, false, "unhealthy", HealthStatus.Unhealthy)]
    public async Task Maps_local_diagnostics_without_downloading(
        bool ready, bool degraded, string status, HealthStatus expected)
    {
        var diagnostics = new FakeDiagnostics(Result(ready, degraded, status, "local check"));
        var check = new YtDlpCheck(diagnostics);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(expected, result.Status);
        Assert.Equal("local check", result.Description);
        Assert.Equal(1, diagnostics.Calls);
    }
}
