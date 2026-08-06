using Microsoft.AspNetCore.Mvc;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/settings/youtube-cookies")]
public sealed class YoutubeCookiesController(IYoutubeCookieStore cookies) : ControllerBase
{
    // The form envelope needs a little room beyond the file itself; the store independently
    // enforces the exact 1 MiB content limit while streaming.
    private const long MultipartLimit = YoutubeCookieStore.MaximumBytes + 64 * 1024;

    [HttpGet]
    public IActionResult Get()
    {
        NoStore();
        return Ok(cookies.Resolve().Status);
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MultipartLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = MultipartLimit, ValueLengthLimit = 4096)]
    public async Task<IActionResult> Upload([FromForm] IFormFile? file, CancellationToken ct)
    {
        NoStore();
        if (file is null) return BadRequest(new { detail = "Select a cookies.txt file to upload." });
        if (file.Length > YoutubeCookieStore.MaximumBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new { detail = "The cookies file exceeds the 1 MiB limit." });
        try
        {
            await using var stream = file.OpenReadStream();
            return Ok(await cookies.UploadAsync(stream, file.Length, ct));
        }
        catch (YoutubeCookieValidationException ex)
        {
            return BadRequest(new { detail = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { detail = ex.Message });
        }
    }

    [HttpDelete]
    public async Task<IActionResult> Delete(CancellationToken ct)
    {
        NoStore();
        try { return Ok(await cookies.DeleteAsync(ct)); }
        catch (InvalidOperationException ex) { return Conflict(new { detail = ex.Message }); }
    }

    private void NoStore() => Response.Headers.CacheControl = "no-store";
}
