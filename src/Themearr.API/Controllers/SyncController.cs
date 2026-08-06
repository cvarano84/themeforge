using Microsoft.AspNetCore.Mvc;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/sync")]
public class SyncController(
    SyncService sync, LibrarySourceResolver sources, Database? db = null,
    RadarrLibrarySource? radarr = null, SonarrShowLibrarySource? sonarr = null) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> StartSync()
    {
        // Delegates to the active source rather than assuming Plex, so a Radarr install's
        // sync button works and a misconfigured source gets a source-appropriate message
        // instead of a generic 400 (or, before this, one that only ever mentioned Plex).
        var reason = sources.Active.SyncBlockedReason;
        if (reason is not null)
            return BadRequest(new { detail = reason });

        var started = await sync.StartAsync();
        return Ok(new { started, detail = started ? null : "Sync already in progress" });
    }

    [HttpGet("status")]
    public IActionResult Status() => Ok(sync.GetStatus());

    [HttpPost("arr/{instanceId}")]
    public async Task<IActionResult> SyncArrInstance(string instanceId, CancellationToken ct)
    {
        if (db is null || radarr is null || sonarr is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        var instance = db.GetArrInstance(instanceId);
        if (instance is null) return NotFound(new { detail = "Arr instance not found." });
        if (!instance.Enabled) return BadRequest(new { detail = "Enable this instance before syncing it." });
        try
        {
            var count = instance.ServiceType == "radarr"
                ? await radarr.TrySyncInstanceAsync(instanceId, _ => { }, ct)
                : await sonarr.TrySyncInstanceAsync(instanceId, _ => { }, ct);
            return count is null
                ? Conflict(new { detail = "This instance is already syncing." })
                : Ok(new { synced = count, instanceId });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { detail = ex.Message });
        }
    }
}
