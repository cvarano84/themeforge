using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Themearr.API.Services.Health;

/// <summary>Local-only downloader readiness check. It never contacts YouTube.</summary>
public sealed class YtDlpCheck(IDownloaderDiagnosticsService diagnostics) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = await diagnostics.CheckAsync(false, cancellationToken);
        return result.Status switch
        {
            "healthy" => HealthCheckResult.Healthy(result.Summary),
            "degraded" => HealthCheckResult.Degraded(result.Summary),
            _ => HealthCheckResult.Unhealthy(result.Summary),
        };
    }
}
