using Microsoft.AspNetCore.Mvc;
using Themearr.API.Services;
using Themearr.API.Services.Health;
using Themearr.API.Services.Sources;
using Themearr.API.Data;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/system")]
public class SystemController(
    HealthCache health, TaskRegistry tasks, Database? db = null,
    RadarrLibrarySource? radarr = null, SonarrShowLibrarySource? sonarr = null) : ControllerBase
{
    [HttpGet("health")]
    public async Task<HealthResponse> Health(CancellationToken ct) => (await health.GetAsync(ct)).Response;

    [HttpGet("tasks")]
    public IReadOnlyList<TaskState> Tasks()
    {
        EnsureArrTasks();
        return tasks.Snapshot();
    }

    [HttpPost("tasks/{id}/run")]
    public IActionResult Run(string id)
    {
        EnsureArrTasks();
        if (!tasks.Exists(id))
            return NotFound(new { detail = "Unknown task" });

        var state = tasks.Snapshot().FirstOrDefault(t => t.Id == id);
        if (state?.IsRunning == true)
            return Conflict(new { detail = "That task is already running" });

        if (id.StartsWith("syncArr:", StringComparison.Ordinal))
        {
            if (db is null || radarr is null || sonarr is null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            tasks.MarkRunning(id, true);
            _ = Task.Run(() => RunArrTaskAsync(id));
            return Accepted(new { started = true });
        }

        // Trigger() returning false means a run is already queued, which is the same
        // outcome the caller wanted — report success either way.
        tasks.Trigger(id);
        return Accepted(new { started = true });
    }

    private void EnsureArrTasks()
    {
        if (db is null) return;
        var interval = TimeSpan.FromMinutes(15);
        foreach (var service in new[] { "radarr", "sonarr" })
        {
            var instances = db.GetArrInstances(service, enabledOnly: true);
            if (instances.Count == 0) continue;
            var allId = $"syncArr:{service}:all";
            if (!tasks.Exists(allId))
                tasks.Register(allId, $"Sync all {(service == "radarr" ? "Radarr" : "Sonarr")} instances", interval);
            foreach (var instance in instances)
            {
                var id = $"syncArr:{service}:{instance.Id}";
                if (!tasks.Exists(id))
                    tasks.Register(id, $"Sync {(service == "radarr" ? "Radarr" : "Sonarr")} — {instance.Name}", interval);
            }
        }
    }

    private async Task RunArrTaskAsync(string taskId)
    {
        var started = DateTime.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var parts = taskId.Split(':', 3);
            var service = parts[1];
            var target = parts[2];
            var selected = target == "all"
                ? db!.GetArrInstances(service, enabledOnly: true)
                : db!.GetArrInstance(target) is { Enabled: true } one ? [one] : [];
            using var concurrency = new SemaphoreSlim(3, 3);
            var counts = await Task.WhenAll(selected.Select(async instance =>
            {
                await concurrency.WaitAsync();
                try
                {
                    return service == "radarr"
                        ? await radarr!.TrySyncInstanceAsync(instance.Id, _ => { }, CancellationToken.None)
                        : await sonarr!.TrySyncInstanceAsync(instance.Id, _ => { }, CancellationToken.None);
                }
                finally { concurrency.Release(); }
            }));
            tasks.RecordRun(taskId, started, stopwatch.Elapsed,
                $"completed: {counts.Where(c => c.HasValue).Sum(c => c!.Value)} items synced");
        }
        catch (Exception ex)
        {
            var message = ex is InvalidOperationException ? ex.Message : "Arr instance sync failed; see application logs.";
            tasks.RecordRun(taskId, started, stopwatch.Elapsed, message);
        }
    }
}
