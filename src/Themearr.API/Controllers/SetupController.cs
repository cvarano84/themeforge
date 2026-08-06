using Microsoft.AspNetCore.Mvc;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/setup")]
public class SetupController(
    Database db, PlexService plex, LocalFolderResolver? folderResolver = null, TaskRegistry? tasks = null) : ControllerBase
{
    private readonly LocalFolderResolver _folders = folderResolver ?? new LocalFolderResolver(db);
    // ── Status ────────────────────────────────────────────────────────────────

    [HttpGet("status")]
    public IActionResult Status() => Ok(SetupPayload());

    // ── Plex PIN login ────────────────────────────────────────────────────────

    [HttpPost("plex/login")]
    [Consumes("application/json")]
    public async Task<IActionResult> StartPlexLogin([FromBody] PlexLoginRequest req)
    {
        var result = await plex.CreateLoginPinAsync(req.ForwardUrl?.Trim() ?? "");
        return Ok(result);
    }

    [HttpGet("plex/login/status")]
    public async Task<IActionResult> PlexLoginStatus([FromQuery] int pinId, [FromQuery] string code)
    {
        Dictionary<string, object> pinState;
        try { pinState = await plex.CheckLoginPinAsync(pinId, code); }
        catch (InvalidOperationException ex) { return BadRequest(new { detail = ex.Message }); }

        var claimed = (bool)pinState["claimed"];
        if (!claimed)
            return Ok(new
            {
                claimed = false,
                connected = false,
                accountName = db.GetSetting("plex_account_name"),
            });

        var authToken = pinState["authToken"]?.ToString() ?? "";
        db.SetSetting("plex_access_token", authToken);

        string accountName;
        try { accountName = await plex.GetAccountNameAsync(authToken); }
        catch { accountName = "Plex user"; }
        db.SetSetting("plex_account_name", accountName);

        return Ok(new
        {
            claimed = true,
            connected = true,
            needsSelection = true,
            accountName,
        });
    }

    // ── Server / library discovery ────────────────────────────────────────────

    [HttpGet("plex/servers")]
    public async Task<IActionResult> PlexServers()
    {
        var token = db.GetSetting("plex_access_token").Trim();
        if (string.IsNullOrEmpty(token))
            return BadRequest(new { detail = "Plex sign-in is required first" });

        try
        {
            var servers = await plex.DiscoverServersAsync(token);
            return Ok(new { servers });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = $"Plex server discovery failed: {ex.Message}" });
        }
    }

    [HttpPost("plex/libraries")]
    [Consumes("application/json")]
    public async Task<IActionResult> PlexLibraries([FromBody] PlexLibrariesRequest req)
    {
        var libraryType = string.IsNullOrWhiteSpace(req.LibraryType)
            ? "movie"
            : req.LibraryType.Trim().ToLowerInvariant();
        if (libraryType is not ("movie" or "show"))
            return BadRequest(new { detail = "Library type must be 'movie' or 'show'." });

        var payload = new Dictionary<string, object>();
        foreach (var server in req.Servers)
        {
            var serverId = server.GetValueOrDefault("id", "")?.ToString()?.Trim() ?? "";
            var serverUrl = server.GetValueOrDefault("url", "")?.ToString()?.Trim() ?? "";
            var urls = server.GetValueOrDefault("urls") is System.Text.Json.JsonElement je
                ? je.EnumerateArray().Select(u => u.GetString() ?? "").Where(u => !string.IsNullOrEmpty(u)).ToList()
                : new List<string>();
            var token = server.GetValueOrDefault("token", "")?.ToString()?.Trim() ?? "";

            if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(token))
                continue;

            var candidates = urls.Prepend(serverUrl).Distinct().ToList();
            try
            {
                payload[serverId] = await plex.ListLibrariesAsync(candidates, token, libraryType);
            }
            catch (Exception)
            {
                return StatusCode(502, new { detail = $"Plex library discovery failed for server {serverId}." });
            }
        }
        return Ok(new { libraries = payload });
    }

    // ── Save selection ────────────────────────────────────────────────────────

    [HttpPost("plex/selection")]
    [Consumes("application/json")]
    public IActionResult SaveSelection([FromBody] PlexSelectionRequest req)
    {
        if (req.Servers == null || req.Servers.Count == 0)
            return BadRequest(new { detail = "Select at least one Plex server" });

        var movieCount = req.SelectedLibraries?.Values.Sum(v => v.Count) ?? 0;
        var effectiveShowLibraries = req.SelectedShowLibraries ?? db.GetSelectedShowLibraries();
        var showCount = effectiveShowLibraries.Values.Sum(v => v.Count);
        if (movieCount + showCount == 0)
            return BadRequest(new { detail = "Select at least one movie or TV show library" });

        var paths = _folders.ValidateConfiguration(req.PathMappings ?? [], req.LibraryPaths ?? []);
        if (!paths.IsValid)
            return BadRequest(new { detail = paths.Errors[0], errors = paths.Errors });
        if (paths.LibraryRoots.Count == 0)
            return BadRequest(new { detail = $"Configure at least one local library root visible inside {ProductBrand.Name}." });

        // Merge so a re-save that omits the redacted token keeps the stored one.
        db.SetPlexServersMergingTokens(req.Servers);
        db.SetSelectedLibraries(req.SelectedLibraries ?? []);
        if (req.SelectedShowLibraries is not null)
            db.SetSelectedShowLibraries(req.SelectedShowLibraries);
        db.SetSetting("movie_library_source", movieCount > 0 ? "plex" : "disabled");
        // Old clients only understand the movie-centric legacy key.
        db.SetSetting("library_source", "plex");
        if (req.SelectedShowLibraries is not null)
            db.SetSetting("show_library_source", showCount > 0 ? "plex" : "disabled");
        db.SetPathMappings(paths.Mappings.ToList());
        db.SetLibraryPaths(paths.LibraryRoots.ToList());

        db.MarkSetupComplete();
        ScheduleConfiguredSyncs();
        return Ok(SetupPayload());
    }

    // ── Non-Plex completion ──────────────────────────────────────────────────

    /// <summary>
    /// Marks setup complete for an install that is not using Plex. The Plex branch
    /// finishes via plex/selection; a Radarr user never touches those endpoints.
    /// </summary>
    [HttpPost("complete")]
    public IActionResult Complete()
    {
        var movieSource = db.GetMovieLibrarySource();
        var showSource = db.GetShowLibrarySource();
        if (movieSource == "plex" || showSource == "plex")
            return BadRequest(new { detail = "Complete Plex setup by selecting its movie or TV libraries; only non-Plex sources use this endpoint." });
        if (movieSource == "disabled" && showSource == "disabled")
            return BadRequest(new { detail = "Enable at least one movie or TV show source." });
        if (movieSource == "radarr" && db.GetArrInstances("radarr", enabledOnly: true).Count == 0 &&
            (string.IsNullOrWhiteSpace(db.GetSetting("radarr_url", "")) ||
             string.IsNullOrWhiteSpace(db.GetSetting("radarr_api_key", ""))))
            return BadRequest(new { detail = "Configure Radarr before completing setup." });
        if (showSource == "sonarr" && db.GetArrInstances("sonarr", enabledOnly: true).Count == 0 &&
            (string.IsNullOrWhiteSpace(db.GetSetting("sonarr_url", "")) ||
             string.IsNullOrWhiteSpace(db.GetSetting("sonarr_api_key", ""))))
            return BadRequest(new { detail = "Configure Sonarr before completing setup." });
        var paths = _folders.ValidateConfiguration(db.GetPathMappings(), db.GetLibraryPaths());
        if (!paths.IsValid)
            return BadRequest(new { detail = paths.Errors[0], errors = paths.Errors });
        if (paths.LibraryRoots.Count == 0)
            return BadRequest(new { detail = $"Configure at least one local library root visible inside {ProductBrand.Name}." });

        db.MarkSetupComplete();
        ScheduleConfiguredSyncs();
        return Ok(new { setupComplete = true });
    }

    // ── Logout ───────────────────────────────────────────────────────────────

    [HttpPost("plex/logout")]
    public IActionResult PlexLogout()
    {
        db.SetSetting("plex_access_token", "");
        db.SetSetting("plex_account_name", "");
        return Ok(new { success = true });
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    [HttpPost("reset")]
    public IActionResult Reset()
    {
        // Wiping all app state is a destructive, master-token-only operation — the
        // externally-held API key must not be able to factory-reset the install.
        if (!HttpContext.AuthenticatedWithBearerToken())
            return StatusCode(StatusCodes.Status403Forbidden,
                new { detail = "Resetting requires the access token, not the API key." });

        db.ResetAppState();
        return Ok(SetupPayload());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private object SetupPayload()
    {
        var plexConnected = !string.IsNullOrEmpty(db.GetSetting("plex_access_token").Trim());
        // Plex token is write-only — never echo it back in a GET response.
        var selectedServers = db.GetPlexServersRedacted();
        var selectedLibraries = db.GetSelectedLibraries();
        var selectedShowLibraries = db.GetSelectedShowLibraries();
        var movieSource = db.GetMovieLibrarySource();
        var showSource = db.GetShowLibrarySource();

        // A Radarr install never selects Plex libraries, so the library-count
        // requirement only makes sense for the Plex source — otherwise setup can
        // never be reported complete and /setup/complete becomes unobservable.
        return new
        {
            setupComplete = db.IsSetupComplete() && ConfigurationIsComplete(movieSource, showSource),
            plexConnected,
            plexAccountName = db.GetSetting("plex_account_name"),
            selectedServers,
            selectedLibraries,
            selectedShowLibraries,
            movieLibrarySource = movieSource,
            showLibrarySource = showSource,
            pathMappings = db.GetPathMappings(),
            libraryPaths = db.GetLibraryPaths(),
        };
    }

    private bool ConfigurationIsComplete(string movieSource, string showSource)
    {
        if (movieSource == "disabled" && showSource == "disabled") return false;
        var moviesReady = movieSource switch
        {
            "disabled" => true,
            "plex" => db.GetSelectedLibraries().Values.Any(v => v.Count > 0),
            "radarr" => true,
            _ => false,
        };
        var showsReady = showSource switch
        {
            "disabled" => true,
            "plex" => db.GetSelectedShowLibraries().Values.Any(v => v.Count > 0),
            "sonarr" => true,
            _ => false,
        };
        return moviesReady && showsReady;
    }

    private void ScheduleConfiguredSyncs()
    {
        if (tasks is null) return;
        if (db.GetMovieLibrarySource() != "disabled" && tasks.Exists(AutoSyncService.SyncTaskId))
            tasks.Trigger(AutoSyncService.SyncTaskId);
        if (db.GetShowLibrarySource() != "disabled" && tasks.Exists(ShowAutoSyncService.SyncTaskId))
            tasks.Trigger(ShowAutoSyncService.SyncTaskId);
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

public record PlexLoginRequest(string? ForwardUrl);

public class PlexLibrariesRequest
{
    public List<Dictionary<string, object?>> Servers { get; set; } = [];
    public string? LibraryType { get; set; }
}

public class PlexSelectionRequest
{
    public List<Dictionary<string, object?>> Servers { get; set; } = [];
    public Dictionary<string, List<string>> SelectedLibraries { get; set; } = [];
    public Dictionary<string, List<string>>? SelectedShowLibraries { get; set; }
    public List<Dictionary<string, string>> PathMappings { get; set; } = [];
    public List<string> LibraryPaths { get; set; } = [];
}
