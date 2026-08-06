using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;
using Themearr.API.Services.Sources;

namespace Themearr.API.Services.Health;

public sealed class ShowLibrarySourceCheck(Database db, ShowSourceResolver sources) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!db.IsSetupComplete()) return HealthCheckResult.Healthy("Setup not complete");
        var source = sources.Active;
        if (source.Name == "disabled") return HealthCheckResult.Healthy("TV show source is disabled");
        var reason = await source.CheckAsync(cancellationToken);
        if (reason?.StartsWith("DEGRADED: ", StringComparison.Ordinal) == true)
            return HealthCheckResult.Degraded(reason[10..]);
        return reason is null
            ? HealthCheckResult.Healthy($"{source.Name} is reachable")
            : HealthCheckResult.Unhealthy(reason);
    }
}
