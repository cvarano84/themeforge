using Microsoft.AspNetCore.Mvc;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/settings/downloader")]
public sealed class DownloaderSettingsController(
    DownloaderConfiguration configuration,
    IDownloaderDiagnosticsService diagnostics) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        NoStore();
        return Ok(await diagnostics.CheckAsync(false, cancellationToken));
    }

    [HttpPut]
    [Consumes("application/json")]
    public async Task<IActionResult> Save(
        [FromBody] DownloaderSettingsPayload payload, CancellationToken cancellationToken)
    {
        NoStore();
        try
        {
            configuration.Save(payload.AudioQuality, payload.TimeoutSeconds, payload.ConcurrentDownloads);
            return Ok(await diagnostics.CheckAsync(true, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { detail = ex.Message });
        }
    }

    [HttpPost("test")]
    public async Task<IActionResult> Test(CancellationToken cancellationToken)
    {
        NoStore();
        var result = await diagnostics.CheckAsync(true, cancellationToken);
        return Ok(new
        {
            ok = result.Ready,
            status = result.Status,
            detail = result.Summary,
            diagnostics = result,
        });
    }

    private void NoStore()
    {
        if (ControllerContext.HttpContext is { } context)
            context.Response.Headers.CacheControl = "no-store";
    }
}

public sealed record DownloaderSettingsPayload(
    string AudioQuality, int TimeoutSeconds, int ConcurrentDownloads);
