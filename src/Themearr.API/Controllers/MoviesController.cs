using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api")]
public class MoviesController(
    Database db, YoutubeService youtube, DownloadService download, PosterUrlSigner posterSigner,
    LibrarySourceResolver sources, ILogger<MoviesController> log) : ControllerBase
{
    private readonly LocalFolderResolver _folders = new(db);

    [HttpGet("movies")]
    public IActionResult ListMovies()
    {
        var movies = MediaGrouping.Group(db.GetAllMovies(), db.GetArrInstances(), shows: false);
        var posterExpiry = DateTimeOffset.UtcNow.AddHours(12);
        var activeSource = sources.Active.Name;
        foreach (var movie in movies)
        {
            var id = movie.GetValueOrDefault("id")?.ToString() ?? "";

            // source_ref is opaque outside its own source (see PosterController); only a
            // movie whose source matches the active one has a poster to sign a URL for.
            var hasPoster = movie.GetValueOrDefault("source")?.ToString() == activeSource
                         && !string.IsNullOrEmpty(movie.GetValueOrDefault("sourceRef")?.ToString());

            // Signed, token-free poster URL — the source's credentials stay server-side
            // (see PosterController).
            movie["posterUrl"] = (!string.IsNullOrEmpty(id) && hasPoster)
                ? posterSigner.PosterPath(id, posterExpiry)
                : null;
        }
        return Ok(movies);
    }

    [HttpGet("search/{movieId}")]
    public async Task<IActionResult> SearchYoutube(string movieId, [FromQuery] string? q = null)
    {
        var movie = db.GetMovie(movieId);
        if (movie == null) return NotFound(new { detail = "Movie not found" });

        var title = movie["title"]?.ToString() ?? "";
        var yearObj = movie["year"];
        var year = yearObj?.ToString() ?? "";
        var yearInt = yearObj is int y ? y : (int?)null;
        var query = !string.IsNullOrWhiteSpace(q) ? q : $"{title} {year} theme".Trim();

        try
        {
            var results = await youtube.SearchAsync(query, maxResults: 8, title: title, year: yearInt);
            return Ok(new { movie, results });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = $"YouTube search error: {ex.Message}" });
        }
    }

    [HttpDelete("movies/{movieId}/theme")]
    public IActionResult DeleteTheme(string movieId, [FromQuery] string scope = "location")
    {
        var movie = db.GetMovie(movieId);
        if (movie == null) return NotFound(new { detail = "Movie not found" });

        var folder = movie["folderName"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(folder))
            return BadRequest(new { detail = "Movie has no folder" });
        if (!IsCurrentResolution(movie, folder))
            return BadRequest(new { detail = "The movie folder is stale or unresolved. Run a full sync or path repair." });

        // Confine deletes to the configured library roots (see DownloadService).
        var roots = db.GetTrustedLibraryRoots();
        if (roots.Count == 0 || !Directory.Exists(folder) || !ThemeFiles.IsWithinRoots(folder, roots))
            return BadRequest(new { detail = "Refusing to delete outside the configured library roots." });
        if (!ThemeFiles.IsDirectoryWritable(folder))
            return BadRequest(new { detail = "The resolved movie folder is not writable." });

        if (scope == "all")
        {
            var groupKey = MediaGrouping.GroupKey(movie, shows: false);
            var results = new List<object>();
            foreach (var location in db.GetAllMovies().Where(row =>
                         MediaGrouping.GroupKey(row, shows: false) == groupKey))
            {
                var locationId = location["id"]?.ToString() ?? "";
                var locationFolder = location["folderName"]?.ToString() ?? "";
                try
                {
                    if (!ThemeFiles.IsWithinRoots(locationFolder, roots) || !Directory.Exists(locationFolder))
                        throw new UnauthorizedAccessException("location unavailable");
                    var locationDeleted = ThemeFiles.DeleteThemes(locationFolder);
                    if (locationDeleted) db.SetMovieStatus(locationId, "pending");
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

        // Reset the stored status so the column stays honest (this movie no longer has a
        // theme) and the auto-download worker's cheap stored-status pre-filter re-adopts
        // it. Without this, a movie deleted while auto-download is on would keep its stale
        // 'downloaded' status and never be re-fetched once the worker stops disk-scanning
        // every movie every tick.
        if (deleted) db.SetMovieStatus(movieId, "pending");

        return Ok(new { deleted });
    }

    [HttpPost("movies/{movieId}/ignore")]
    public IActionResult IgnoreMovie(string movieId)
    {
        if (db.GetMovie(movieId) == null) return NotFound(new { detail = "Movie not found" });
        db.SetMovieIgnored(movieId, true);
        return Ok(new { ignored = true });
    }

    [HttpPost("movies/{movieId}/unignore")]
    public IActionResult UnignoreMovie(string movieId)
    {
        if (db.GetMovie(movieId) == null) return NotFound(new { detail = "Movie not found" });
        db.SetMovieIgnored(movieId, false);
        return Ok(new { ignored = false });
    }

    [HttpGet("movies/{movieId}/theme/audio")]
    public IActionResult GetThemeAudio(string movieId)
    {
        var movie = db.GetMovie(movieId);
        if (movie == null) return NotFound(new { detail = "Movie not found" });

        var folder = movie["folderName"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(folder)) return NotFound(new { detail = "No folder" });
        if (!IsCurrentResolution(movie, folder))
            return NotFound(new { detail = "No safely resolved theme file" });

        var roots = db.GetTrustedLibraryRoots();
        if (roots.Count == 0 || !Directory.Exists(folder) || !ThemeFiles.IsWithinRoots(folder, roots))
            return NotFound(new { detail = "No safely resolved theme file" });

        var themeFile = ThemeFiles.FindThemeFile(folder);
        if (themeFile == null) return NotFound(new { detail = "No theme file" });

        var contentType = ThemeFiles.ContentTypeFor(themeFile);

        // ETag + Last-Modified so repeated visits don't re-download the same theme file.
        // Framework honours If-None-Match / If-Modified-Since and returns 304 automatically.
        var info = new FileInfo(themeFile);
        var etag = new EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"");
        Response.Headers.CacheControl = "private, max-age=300";
        return PhysicalFile(themeFile, contentType, info.LastWriteTimeUtc, etag, enableRangeProcessing: true);
    }

    private bool IsCurrentResolution(Dictionary<string, object?> movie, string folder)
    {
        var sourcePath = movie["sourcePath"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(sourcePath)) return true;
        var resolved = _folders.ResolveStoredSource(
            sourcePath, movie["source"]?.ToString(), isShow: false,
            movie.GetValueOrDefault("instanceId")?.ToString()).ResolvedFolderPath;
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

    [HttpPost("auto-download/{movieId}")]
    public async Task<IActionResult> AutoDownload(string movieId)
    {
        var movie = db.GetMovie(movieId);
        if (movie == null) return NotFound(new { detail = "Movie not found" });
        if (download.DestinationBlockedReason(movieId) is { } pathBlocked)
            return UnprocessableEntity(new { detail = pathBlocked });

        var title = movie["title"]?.ToString() ?? "";
        var yearObj = movie["year"];
        var year = yearObj?.ToString() ?? "";
        var yearInt = yearObj is int y ? y : (int?)null;
        var query = $"{title} {year} theme".Trim();

        List<Dictionary<string, object?>> results;
        try
        {
            results = await youtube.SearchAsync(query, maxResults: 8, title: title, year: yearInt);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = $"YouTube search error: {ex.Message}" });
        }

        var best = results.FirstOrDefault(r => r.GetValueOrDefault("bestMatch") is true);
        if (best == null)
            return UnprocessableEntity(new { detail = "No suitable match found — please select manually." });

        if (await download.DownloadBlockedReasonAsync(isProviderUrl: true, HttpContext.RequestAborted) is { } notReady)
        {
            log.LogWarning("Auto-download for {MovieId} blocked: {Reason}", LogSanitizer.Clean(movieId), notReady);
            return UnprocessableEntity(new { detail = notReady });
        }

        var videoId = best["videoId"]?.ToString() ?? "";
        var url = $"https://www.youtube.com/watch?v={videoId}";
        if (!download.Start(movieId, url))
            return Conflict(new { detail = "A download is already running for this destination." });

        return Accepted(new { started = true, movieId, videoId, videoTitle = best["title"] });
    }

    [HttpPost("download")]
    [Consumes("application/json")]
    public async Task<IActionResult> Download([FromBody] DownloadRequest req)
    {
        if (db.GetMovie(req.MovieId) == null)
            return NotFound(new { detail = "Movie not found" });
        if (download.DestinationBlockedReason(req.MovieId) is { } pathBlocked)
            return UnprocessableEntity(new { detail = pathBlocked });

        if (!YtDlpThemeAudioProvider.IsValidVideoId(req.VideoId))
            return BadRequest(new { detail = "Invalid YouTube video ID." });

        if (await download.DownloadBlockedReasonAsync(isProviderUrl: true, HttpContext.RequestAborted) is { } notReady)
        {
            log.LogWarning("Download for {MovieId} blocked: {Reason}", LogSanitizer.Clean(req.MovieId), notReady);
            return UnprocessableEntity(new { detail = notReady });
        }

        var url = $"https://www.youtube.com/watch?v={req.VideoId}";
        if (!download.Start(req.MovieId, url))
            return Conflict(new { detail = "A download is already running for this destination." });
        return Accepted(new { started = true, movieId = req.MovieId });
    }

    [HttpPost("download-url")]
    [Consumes("application/json")]
    public async Task<IActionResult> DownloadUrl([FromBody] DownloadUrlRequest req)
    {
        if (string.IsNullOrEmpty(req.Url) ||
            !Uri.TryCreate(req.Url, UriKind.Absolute, out var uri))
            return BadRequest(new { detail = "Invalid URL" });

        if (uri.Scheme is not ("http" or "https"))
            return BadRequest(new { detail = "Only http and https URLs are supported." });

        if (HostGuard.IsPrivateOrLoopback(uri.Host))
            return BadRequest(new { detail = "Refusing to download from a private or loopback address." });

        if (db.GetMovie(req.MovieId) == null)
            return NotFound(new { detail = "Movie not found" });
        if (download.DestinationBlockedReason(req.MovieId) is { } pathBlocked)
            return UnprocessableEntity(new { detail = pathBlocked });

        if (await download.DownloadBlockedReasonAsync(
                DownloadService.IsProviderUrl(req.Url), HttpContext.RequestAborted) is { } notReady)
        {
            log.LogWarning("Download-url for {MovieId} blocked: {Reason}", LogSanitizer.Clean(req.MovieId), notReady);
            return UnprocessableEntity(new { detail = notReady });
        }

        if (!download.Start(req.MovieId, req.Url))
            return Conflict(new { detail = "A download is already running for this destination." });
        return Accepted(new { started = true, movieId = req.MovieId });
    }

    [HttpGet("download/status/{movieId}")]
    public IActionResult DownloadStatus(string movieId)
    {
        return Ok(download.GetStatus(movieId));
    }

    // Diagnostic view of the server-side auto-download loop. Use this to verify
    // the background service is actually running when "set and forget" seems broken.
    [HttpGet("auto-download/debug")]
    public IActionResult AutoDownloadDebug([FromServices] AutoDownloadService auto)
        => Ok(auto.GetDiagnostics());
}

public record DownloadRequest(string MovieId, string VideoId);
public record DownloadUrlRequest(string MovieId, string Url);
