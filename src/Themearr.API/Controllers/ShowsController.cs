using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

/// <summary>
/// The shows API. A deliberate parallel of <see cref="MoviesController"/> rather than a
/// media-type-generic controller: branching every movie route by media type risks changing
/// movie behaviour, and the logic genuinely worth sharing already lives in
/// <see cref="ThemeFiles"/> and <see cref="DownloadService"/>.
///
/// Unlike the movie routes' legacy shape, everything here is namespaced under
/// <c>/api/shows</c> — except posters, which must sit under the public <c>/api/poster</c>
/// prefix (see <see cref="PosterController.GetShow"/>).
/// </summary>
[ApiController]
[Route("api/shows")]
public class ShowsController(
    Database db, YoutubeService youtube, DownloadService download, PosterUrlSigner posterSigner,
    ILogger<ShowsController> log) : ControllerBase
{
    private readonly LocalFolderResolver _folders = new(db);

    [HttpGet]
    public IActionResult ListShows()
    {
        var shows = MediaGrouping.Group(db.GetAllShows(), db.GetArrInstances(), shows: true);
        var posterExpiry = DateTimeOffset.UtcNow.AddHours(12);
        foreach (var show in shows)
        {
            var id = show.GetValueOrDefault("id")?.ToString() ?? "";

            // Shows come only from Plex, so unlike movies there is no active-source check
            // here — a show with a source_ref always has a Plex poster to sign a URL for.
            var hasSourceRef = !string.IsNullOrEmpty(show.GetValueOrDefault("sourceRef")?.ToString());
            var hasPoster = hasSourceRef && (show.GetValueOrDefault("hasPoster") as bool? ?? true);

            show["posterUrl"] = (!string.IsNullOrEmpty(id) && hasPoster)
                ? posterSigner.ShowPosterPath(id, posterExpiry)
                : null;
        }
        return Ok(shows);
    }

    [HttpGet("{showId}/search")]
    public async Task<IActionResult> SearchYoutube(string showId, [FromQuery] string? q = null)
    {
        var show = db.GetShow(showId);
        if (show == null) return NotFound(new { detail = "Show not found" });

        var title = show["title"]?.ToString() ?? "";

        // Year-free by default: a show spans years, so including one biases the search
        // toward a single season's upload. Same query the auto-download worker uses, so a
        // manual search and an automatic one agree on what they are looking for.
        var query = !string.IsNullOrWhiteSpace(q) ? q : ShowAutoDownloadService.BuildQuery(title);

        try
        {
            var results = await youtube.SearchAsync(query, maxResults: 8, title: title);
            return Ok(new { show, results });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = $"YouTube search error: {ex.Message}" });
        }
    }

    // A show whose status is 'plexTheme' is NOT blocked here. That status is informational
    // — it tells the UI why the show is being skipped by default — and the UI is expected
    // to require an explicit "download anyway". The API accepting it is what makes the
    // override possible at all.
    [HttpPost("{showId}/download")]
    [Consumes("application/json")]
    public async Task<IActionResult> Download(string showId, [FromBody] ShowDownloadRequest req)
    {
        if (db.GetShow(showId) == null) return NotFound(new { detail = "Show not found" });
        if (download.DestinationBlockedReason(showId, "show") is { } pathBlocked)
            return UnprocessableEntity(new { detail = pathBlocked });

        if (!YtDlpThemeAudioProvider.IsValidVideoId(req.VideoId))
            return BadRequest(new { detail = "Invalid YouTube video ID." });

        if (await download.DownloadBlockedReasonAsync(isProviderUrl: true, HttpContext.RequestAborted) is { } notReady)
        {
            log.LogWarning("Show download for {ShowId} blocked: {Reason}", LogSanitizer.Clean(showId), notReady);
            return UnprocessableEntity(new { detail = notReady });
        }

        if (!download.Start(showId, $"https://www.youtube.com/watch?v={req.VideoId}", "show"))
            return Conflict(new { detail = "A download is already running for this destination." });
        return Accepted(new { started = true, showId });
    }

    [HttpPost("{showId}/download-url")]
    [Consumes("application/json")]
    public async Task<IActionResult> DownloadUrl(string showId, [FromBody] ShowDownloadUrlRequest req)
    {
        if (string.IsNullOrEmpty(req.Url) || !Uri.TryCreate(req.Url, UriKind.Absolute, out var uri))
            return BadRequest(new { detail = "Invalid URL" });

        if (uri.Scheme is not ("http" or "https"))
            return BadRequest(new { detail = "Only http and https URLs are supported." });

        if (HostGuard.IsPrivateOrLoopback(uri.Host))
            return BadRequest(new { detail = "Refusing to download from a private or loopback address." });

        if (db.GetShow(showId) == null) return NotFound(new { detail = "Show not found" });
        if (download.DestinationBlockedReason(showId, "show") is { } pathBlocked)
            return UnprocessableEntity(new { detail = pathBlocked });

        if (await download.DownloadBlockedReasonAsync(
                DownloadService.IsProviderUrl(req.Url), HttpContext.RequestAborted) is { } notReady)
        {
            log.LogWarning("Show download-url for {ShowId} blocked: {Reason}", LogSanitizer.Clean(showId), notReady);
            return UnprocessableEntity(new { detail = notReady });
        }

        if (!download.Start(showId, req.Url, "show"))
            return Conflict(new { detail = "A download is already running for this destination." });
        return Accepted(new { started = true, showId });
    }

    [HttpGet("{showId}/download/status")]
    public IActionResult DownloadStatus(string showId) => Ok(download.GetStatus(showId, "show"));

    [HttpPost("{showId}/ignore")]
    public IActionResult IgnoreShow(string showId)
    {
        if (db.GetShow(showId) == null) return NotFound(new { detail = "Show not found" });
        db.SetShowIgnored(showId, true);
        return Ok(new { ignored = true });
    }

    [HttpPost("{showId}/unignore")]
    public IActionResult UnignoreShow(string showId)
    {
        if (db.GetShow(showId) == null) return NotFound(new { detail = "Show not found" });
        db.SetShowIgnored(showId, false);
        return Ok(new { ignored = false });
    }

    [HttpDelete("{showId}/theme")]
    public IActionResult DeleteTheme(string showId, [FromQuery] string scope = "location")
    {
        var show = db.GetShow(showId);
        if (show == null) return NotFound(new { detail = "Show not found" });

        var folder = show["folderName"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(folder)) return BadRequest(new { detail = "Show has no folder" });
        if (!IsCurrentResolution(show, folder))
            return BadRequest(new { detail = "The show folder is stale or unresolved. Run a full sync or path repair." });

        // Confine deletes to the configured library roots (see DownloadService).
        var roots = db.GetTrustedLibraryRoots();
        if (roots.Count == 0 || !Directory.Exists(folder) || !ThemeFiles.IsWithinRoots(folder, roots))
            return BadRequest(new { detail = "Refusing to delete outside the configured library roots." });
        if (!ThemeFiles.IsDirectoryWritable(folder))
            return BadRequest(new { detail = "The resolved show folder is not writable." });

        if (scope == "all")
        {
            var groupKey = MediaGrouping.GroupKey(show, shows: true);
            var results = new List<object>();
            foreach (var location in db.GetAllShows().Where(row =>
                         MediaGrouping.GroupKey(row, shows: true) == groupKey))
            {
                var locationId = location["id"]?.ToString() ?? "";
                var locationFolder = location["folderName"]?.ToString() ?? "";
                try
                {
                    if (!ThemeFiles.IsWithinRoots(locationFolder, roots) || !Directory.Exists(locationFolder))
                        throw new UnauthorizedAccessException("location unavailable");
                    var locationDeleted = ThemeFiles.DeleteThemes(locationFolder);
                    if (locationDeleted) db.SetShowStatus(locationId, "pending");
                    results.Add(new { id = locationId, deleted = locationDeleted, error = (string?)null });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    results.Add(new { id = locationId, deleted = false, error = ex.Message });
                }
            }
            return Ok(new { deleted = results.Any(), scope = "all", locations = results });
        }
        if (scope != "location") return BadRequest(new { detail = "scope must be 'location' or 'all'." });

        var deleted = ThemeFiles.DeleteThemes(folder);

        // Reset the stored status so the column stays honest and the auto-download worker's
        // stored-status pre-filter re-adopts this show — same contract as the movie endpoint.
        if (deleted) db.SetShowStatus(showId, "pending");

        return Ok(new { deleted });
    }

    [HttpGet("{showId}/theme/audio")]
    public IActionResult GetThemeAudio(string showId)
    {
        var show = db.GetShow(showId);
        if (show == null) return NotFound(new { detail = "Show not found" });

        var folder = show["folderName"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(folder)) return NotFound(new { detail = "No folder" });
        if (!IsCurrentResolution(show, folder))
            return NotFound(new { detail = "No safely resolved theme file" });

        var roots = db.GetTrustedLibraryRoots();
        if (roots.Count == 0 || !Directory.Exists(folder) || !ThemeFiles.IsWithinRoots(folder, roots))
            return NotFound(new { detail = "No safely resolved theme file" });

        var themeFile = ThemeFiles.FindThemeFile(folder);
        if (themeFile == null) return NotFound(new { detail = "No theme file" });

        // ETag + Last-Modified so repeated visits don't re-download the same theme file.
        // Framework honours If-None-Match / If-Modified-Since and returns 304 automatically.
        var info = new FileInfo(themeFile);
        var etag = new EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"");
        Response.Headers.CacheControl = "private, max-age=300";
        return PhysicalFile(themeFile, ThemeFiles.ContentTypeFor(themeFile),
            info.LastWriteTimeUtc, etag, enableRangeProcessing: true);
    }

    private bool IsCurrentResolution(Dictionary<string, object?> show, string folder)
    {
        var sourcePath = show["sourcePath"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(sourcePath)) return true;
        var resolved = _folders.ResolveStoredSource(
            sourcePath, show["source"]?.ToString(), isShow: true,
            show.GetValueOrDefault("instanceId")?.ToString()).ResolvedFolderPath;
        return resolved is not null && SamePath(folder, resolved);
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }
    }
}

public record ShowDownloadRequest(string VideoId);
public record ShowDownloadUrlRequest(string Url);
