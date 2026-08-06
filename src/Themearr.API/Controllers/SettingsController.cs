using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController(
    Database db, RadarrLibrarySource radarr, SonarrShowLibrarySource sonarr,
    PlexLibrarySource plex, IApiKeyStore keys,
    LocalFolderResolver? folderResolver = null, LibraryPathRepairService? pathRepairService = null) : ControllerBase
{
    private readonly LocalFolderResolver _folders = folderResolver ?? new LocalFolderResolver(db);
    private readonly LibraryPathRepairService _pathRepair = pathRepairService
        ?? new LibraryPathRepairService(db, folderResolver ?? new LocalFolderResolver(db));
    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        // Plex token is write-only — never echo it back in a GET response.
        selectedServers = db.GetPlexServersRedacted(),
        selectedLibraries = db.GetSelectedLibraries(),
        selectedShowLibraries = db.GetSelectedShowLibraries(),
        movieLibrarySource = db.GetMovieLibrarySource(),
        showLibrarySource = db.GetShowLibrarySource(),
        pathMappings = db.GetPathMappings(),
        libraryPaths = db.GetLibraryPaths(),
        advanced = new
        {
            maxSearchDirs = int.Parse(db.GetSetting("max_search_dirs", "20000")),
            searchDepth = int.Parse(db.GetSetting("search_depth", "4")),
        },
        autoDownload = db.GetSetting("auto_download", "false") == "true",
        autoSync = db.GetSetting("auto_sync", "false") == "true",
        lastAutoSyncAt = db.GetSetting("last_auto_sync_at", ""),
    });

    // [Consumes] forces a JSON content-type (and thus a CORS preflight), which — on top
    // of the header-only bearer auth — blocks simple cross-site POSTs from forging this.
    [HttpPost]
    [Consumes("application/json")]
    public IActionResult Save([FromBody] SettingsPayload req)
    {
        var paths = _folders.ValidateConfiguration(req.PathMappings, req.LibraryPaths);
        if (!paths.IsValid)
            return BadRequest(new { detail = paths.Errors[0], errors = paths.Errors });

        // Merge so a save that omits the redacted token keeps the stored one.
        db.SetPlexServersMergingTokens(req.SelectedServers);
        db.SetSelectedLibraries(req.SelectedLibraries);
        // Only when present — see SettingsPayload.SelectedShowLibraries for why absent
        // must mean "leave unchanged" rather than "select nothing".
        if (req.SelectedShowLibraries is not null)
            db.SetSelectedShowLibraries(req.SelectedShowLibraries);
        db.SetPathMappings(paths.Mappings.ToList());
        db.SetLibraryPaths(paths.LibraryRoots.ToList());

        var maxDirs = Math.Clamp(req.Advanced.GetValueOrDefault("maxSearchDirs", 20000), 500, 100000);
        var depth = Math.Clamp(req.Advanced.GetValueOrDefault("searchDepth", 4), 1, 10);
        db.SetSetting("max_search_dirs", maxDirs.ToString());
        db.SetSetting("search_depth", depth.ToString());
        db.SetSetting("auto_download", req.AutoDownload ? "true" : "false");
        db.SetSetting("auto_sync", req.AutoSync ? "true" : "false");

        if (req.SelectedServers.Count > 0 && req.SelectedLibraries.Values.Sum(v => v.Count) > 0)
            db.MarkSetupComplete();

        return Get();
    }

    [HttpPost("paths/test")]
    [Consumes("application/json")]
    public IActionResult TestPathMapping([FromBody] TestPathMappingPayload payload)
    {
        var mappings = payload.PathMappings ?? db.GetPathMappings();
        var roots = payload.LibraryPaths ?? db.GetLibraryPaths();
        var validation = _folders.ValidateConfiguration(mappings, roots);
        if (!validation.IsValid)
            return BadRequest(new { detail = validation.Errors[0], errors = validation.Errors });

        var result = _folders.ResolveDetailed(payload.SourcePath ?? "",
            validation.Mappings, validation.LibraryRoots, payload.SourceIsFolder);
        return Ok(result);
    }

    [HttpPost("paths/repair")]
    public IActionResult RepairPaths() => Ok(_pathRepair.RepairAll());

    // ── Radarr library source ────────────────────────────────────────────────

    [HttpGet("radarr")]
    public IActionResult GetRadarr() => Ok(new
    {
        source = db.GetMovieLibrarySource(),
        url = PreferredArr("radarr")?.Url ?? db.GetSetting("radarr_url", ""),
        // The key itself is never returned — credentials remain write-only.
        configured = PreferredArr("radarr")?.ApiKey.Length > 0
            || !string.IsNullOrWhiteSpace(db.GetSetting("radarr_api_key", "")),
    });

    [HttpPost("radarr")]
    [Consumes("application/json")]
    public IActionResult SaveRadarr([FromBody] RadarrPayload payload)
    {
        var source = (payload.Source ?? "plex").Trim();
        if (source is not ("plex" or "radarr" or "disabled"))
            return BadRequest(new { detail = "Movie source must be 'plex', 'radarr', or 'disabled'." });

        if (source == "radarr" && string.IsNullOrWhiteSpace(payload.Url))
            return BadRequest(new { detail = "Radarr URL cannot be empty." });

        // A blank URL or key normally means "keep what you had" — e.g. a Plex save submits
        // neither, and must not wipe Radarr's stored config out from under it. But a blank
        // key may only fall back to the stored one when the submitted URL is the one that
        // key belongs to: the UI never receives the stored key back, so pairing a blank key
        // with a *different* URL isn't "leave it as-is" — it's "no key was ever entered for
        // this server". Falling back here would have the server ship the real key, in an
        // X-Api-Key header, to whatever host the caller just named — and this endpoint
        // accepts the very API key credential that's meant to be pasted into Radarr, so an
        // authenticated caller could otherwise make the key exfiltrate itself. Same rule as
        // TestRadarr and Database.SetPlexServersMergingTokens (see UrlsMatch below).
        var storedUrl = db.GetSetting("radarr_url", "").Trim().TrimEnd('/');
        var storedKey = db.GetSetting("radarr_api_key", "");
        var submittedUrl = (payload.Url ?? "").Trim().TrimEnd('/');
        var urlIsChanging = !string.IsNullOrWhiteSpace(submittedUrl) &&
                             !string.IsNullOrEmpty(storedUrl) &&
                             !UrlsMatch(submittedUrl, storedUrl);

        if (string.IsNullOrWhiteSpace(payload.ApiKey))
        {
            if (urlIsChanging)
                return BadRequest(new { detail = "Enter the API key for the new Radarr server." });
            if (source == "radarr" && string.IsNullOrWhiteSpace(storedKey))
                return BadRequest(new { detail = "Radarr API key cannot be empty." });
        }

        db.SetSetting("movie_library_source", source);
        // Keep the historical setting in sync for cached clients and older code. It has
        // no disabled value, so Plex is the safest legacy no-op fallback in that case.
        db.SetSetting("library_source", source == "disabled" ? "plex" : source);
        if (!string.IsNullOrWhiteSpace(payload.Url))
        {
            var existing = db.GetArrInstances("radarr").OrderBy(i => i.Priority).FirstOrDefault();
            if (existing is null && !string.IsNullOrWhiteSpace(payload.ApiKey))
                db.CreateArrInstance("radarr", "Radarr", submittedUrl, payload.ApiKey.Trim(),
                    enabled: true, qualityLabel: null, priority: 0, tags: null);
            else if (existing is not null)
                db.UpdateArrInstance(existing.Id, "radarr", existing.Name, submittedUrl,
                    payload.ApiKey, existing.Enabled, existing.QualityLabel, existing.Priority, existing.Tags);
            db.SetSetting("radarr_url", payload.Url.Trim().TrimEnd('/'));
        }
        if (!string.IsNullOrWhiteSpace(payload.ApiKey))
            db.SetSetting("radarr_api_key", payload.ApiKey.Trim());

        return Ok(new { source, configured = !string.IsNullOrWhiteSpace(db.GetSetting("radarr_api_key", "")) });
    }

    // Ordinal comparison after trimming a single trailing slash — same rule as
    // Database.UrlsMatch, which guards the equivalent Plex-token re-attachment case.
    // Enough to treat "http://host:7878" and "http://host:7878/" as the same server
    // without being lenient about anything that would actually change the destination.
    private static bool UrlsMatch(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);

    private ArrInstance? PreferredArr(string serviceType) =>
        db.GetArrInstances(serviceType).OrderBy(i => i.Priority).ThenBy(i => i.Id).FirstOrDefault();

    [HttpPost("radarr/test")]
    [Consumes("application/json")]
    public async Task<IActionResult> TestRadarr([FromBody] RadarrPayload payload, CancellationToken ct)
    {
        // Test what the user is about to save, not what is stored, so a wrong key is
        // caught while they are still looking at the field. Probes directly against the
        // submitted values — never writes to settings, so this can't race a scheduled
        // sync or a real save that lands mid-probe (see RadarrLibrarySource.ProbeAsync).
        var url = (payload.Url ?? "").Trim().TrimEnd('/');

        string key;
        if (!string.IsNullOrWhiteSpace(payload.ApiKey))
        {
            key = payload.ApiKey.Trim();
        }
        else
        {
            // No key submitted — only fall back to the stored key when the submitted
            // URL is the one that key belongs to. Otherwise an authenticated caller
            // could make the server ship the real Radarr key to a host of their
            // choosing (the response never reveals the key, but it would still spend it).
            var storedUrl = db.GetSetting("radarr_url", "").Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(storedUrl) && string.Equals(url, storedUrl, StringComparison.OrdinalIgnoreCase))
            {
                key = db.GetSetting("radarr_api_key", "");
            }
            else
            {
                return Ok(new { ok = false, detail = "Enter the API key for this server." });
            }
        }

        var reason = await radarr.ProbeAsync(url, key, ct);
        return Ok(new { ok = reason is null, detail = reason ?? "Radarr is reachable." });
    }

    public record RadarrPayload(string? Source, string? Url, string? ApiKey);

    // ── Sonarr TV-show source ───────────────────────────────────────────────

    [HttpGet("sonarr")]
    public IActionResult GetSonarr() => Ok(new
    {
        source = db.GetShowLibrarySource(),
        url = PreferredArr("sonarr")?.Url ?? db.GetSetting("sonarr_url", ""),
        configured = PreferredArr("sonarr")?.ApiKey.Length > 0
            || !string.IsNullOrWhiteSpace(db.GetSetting("sonarr_api_key", "")),
    });

    [HttpPost("sonarr")]
    [Consumes("application/json")]
    public IActionResult SaveSonarr([FromBody] SonarrPayload payload)
    {
        var source = (payload.Source ?? "disabled").Trim();
        if (source is not ("plex" or "sonarr" or "disabled"))
            return BadRequest(new { detail = "Show source must be 'plex', 'sonarr', or 'disabled'." });
        if (source == "sonarr" && string.IsNullOrWhiteSpace(payload.Url))
            return BadRequest(new { detail = "Sonarr URL cannot be empty." });

        var storedUrl = db.GetSetting("sonarr_url", "").Trim().TrimEnd('/');
        var storedKey = db.GetSetting("sonarr_api_key", "");
        var submittedUrl = (payload.Url ?? "").Trim().TrimEnd('/');
        var urlIsChanging = !string.IsNullOrWhiteSpace(submittedUrl) &&
                            !string.IsNullOrEmpty(storedUrl) &&
                            !UrlsMatch(submittedUrl, storedUrl);
        if (string.IsNullOrWhiteSpace(payload.ApiKey))
        {
            if (urlIsChanging)
                return BadRequest(new { detail = "Enter the API key for the new Sonarr server." });
            if (source == "sonarr" && string.IsNullOrWhiteSpace(storedKey))
                return BadRequest(new { detail = "Sonarr API key cannot be empty." });
        }

        db.SetSetting("show_library_source", source);
        if (!string.IsNullOrWhiteSpace(payload.Url))
        {
            var existing = db.GetArrInstances("sonarr").OrderBy(i => i.Priority).FirstOrDefault();
            if (existing is null && !string.IsNullOrWhiteSpace(payload.ApiKey))
                db.CreateArrInstance("sonarr", "Sonarr", submittedUrl, payload.ApiKey.Trim(),
                    enabled: true, qualityLabel: null, priority: 0, tags: null);
            else if (existing is not null)
                db.UpdateArrInstance(existing.Id, "sonarr", existing.Name, submittedUrl,
                    payload.ApiKey, existing.Enabled, existing.QualityLabel, existing.Priority, existing.Tags);
            db.SetSetting("sonarr_url", submittedUrl);
        }
        if (!string.IsNullOrWhiteSpace(payload.ApiKey))
            db.SetSetting("sonarr_api_key", payload.ApiKey.Trim());

        return Ok(new
        {
            source,
            configured = !string.IsNullOrWhiteSpace(db.GetSetting("sonarr_api_key", "")),
        });
    }

    [HttpPost("sonarr/test")]
    [Consumes("application/json")]
    public async Task<IActionResult> TestSonarr([FromBody] SonarrPayload payload, CancellationToken ct)
    {
        var url = (payload.Url ?? "").Trim().TrimEnd('/');
        string key;
        if (!string.IsNullOrWhiteSpace(payload.ApiKey))
        {
            key = payload.ApiKey.Trim();
        }
        else
        {
            var storedUrl = db.GetSetting("sonarr_url", "").Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(storedUrl) || !UrlsMatch(url, storedUrl))
                return Ok(new { ok = false, detail = "Enter the API key for this server." });
            key = db.GetSetting("sonarr_api_key", "");
        }

        var reason = await sonarr.ProbeAsync(url, key, ct);
        return Ok(new { ok = reason is null, detail = reason ?? "Sonarr is reachable." });
    }

    public record SonarrPayload(string? Source, string? Url, string? ApiKey);

    // ── Plex server URL (manual override) ──────────────────────────────────────
    // Both endpoints are bearer-only: each sends or binds the stored Plex token to an
    // operator-supplied host, so the externally-held API key must not reach them (same
    // gate as apikey management above).
    private IActionResult PlexUrlForbidden() => StatusCode(StatusCodes.Status403Forbidden,
        new { detail = "Changing the Plex server URL requires the access token, not the API key." });

    [HttpPost("plex/test")]
    [Consumes("application/json")]
    public async Task<IActionResult> TestPlex([FromBody] PlexUrlPayload payload, CancellationToken ct)
    {
        if (!AuthenticatedWithBearerToken) return PlexUrlForbidden();

        var url = NormalizePlexUrl(payload.Url);
        if (url is null)
            return BadRequest(new { detail = "Enter a valid server address, e.g. http://192.168.1.50:32400." });

        // Probe with the STORED token for that server — never a token from the request body.
        if (!db.GetPlexServersDict().TryGetValue(payload.ServerId ?? "", out var srv))
            return NotFound(new { detail = "That Plex server is not connected." });

        var reason = await plex.ProbeAsync(url, srv.Token, ct);
        return Ok(new { ok = reason is null, detail = reason ?? "Plex is reachable." });
    }

    [HttpPost("plex/server")]
    [Consumes("application/json")]
    public IActionResult SavePlexUrl([FromBody] PlexUrlPayload payload)
    {
        if (!AuthenticatedWithBearerToken) return PlexUrlForbidden();

        var url = NormalizePlexUrl(payload.Url);
        if (url is null)
            return BadRequest(new { detail = "Enter a valid server address, e.g. http://192.168.1.50:32400." });

        if (!db.UpdatePlexServerUrl(payload.ServerId ?? "", url))
            return NotFound(new { detail = "That Plex server is not connected." });

        return Ok(new { selectedServers = db.GetPlexServersRedacted() });
    }

    public record PlexUrlPayload(string? ServerId, string? Url);

    // Normalizes a user-entered Plex address: trims, defaults to http:// when no scheme is
    // given (Plex local is http on :32400), requires an http(s) URL with a host, and strips a
    // trailing slash. Returns null when the input can't be a valid server address. Private and
    // loopback hosts are allowed on purpose — Plex servers are private, like the discovered URLs.
    private static string? NormalizePlexUrl(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return null;
        if (!text.Contains("://")) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;
        return text.TrimEnd('/');
    }

    // ── ThemeForge's own API key ─────────────────────────────────────────────

    // The API key must not be able to read or regenerate itself: otherwise whoever holds
    // it could re-issue it forever and lock the operator out of their own integration.
    // Only the master bearer token may read or regenerate it. This is the one carve-out —
    // the API key otherwise authenticates like the bearer token everywhere else, including
    // endpoints that overwrite the Radarr key or Plex token; see the README's API key section.
    private bool AuthenticatedWithBearerToken => HttpContext.AuthenticatedWithBearerToken();

    private IActionResult ApiKeyManagementForbidden() => StatusCode(StatusCodes.Status403Forbidden,
        new { detail = "Managing the API key requires the access token, not the API key." });

    /// <summary>
    /// Returns the API key in full. Unlike Radarr's key — which ThemeForge holds and never
    /// discloses — this one is issued to the operator to paste into an external tool, so
    /// it has to be readable.
    /// </summary>
    [HttpGet("apikey")]
    public IActionResult GetApiKey()
    {
        if (!AuthenticatedWithBearerToken) return ApiKeyManagementForbidden();

        Response.Headers.CacheControl = "no-store";
        return Ok(new { key = keys.Current });
    }

    [HttpPost("apikey/regenerate")]
    public IActionResult RegenerateApiKey()
    {
        if (!AuthenticatedWithBearerToken) return ApiKeyManagementForbidden();

        Response.Headers.CacheControl = "no-store";
        return Ok(new { key = keys.Regenerate() });
    }
}

public class SettingsPayload
{
    public List<Dictionary<string, object?>> SelectedServers { get; set; } = [];
    public Dictionary<string, List<string>> SelectedLibraries { get; set; } = [];

    /// <summary>
    /// Nullable on purpose: <see cref="SettingsController.Save"/> writes the other
    /// collections unconditionally, so an absent field must mean "leave unchanged" rather
    /// than "select nothing". A frontend bundle cached from before this shipped omits it,
    /// and would otherwise wipe the operator's show libraries on their next settings save.
    /// An explicit empty dictionary still means "deselect everything".
    /// </summary>
    public Dictionary<string, List<string>>? SelectedShowLibraries { get; set; }

    public List<Dictionary<string, string>> PathMappings { get; set; } = [];
    public List<string> LibraryPaths { get; set; } = [];
    public Dictionary<string, int> Advanced { get; set; } = [];
    public bool AutoDownload { get; set; } = false;
    public bool AutoSync { get; set; } = false;
}

public record TestPathMappingPayload(
    string? SourcePath,
    bool SourceIsFolder = false,
    List<Dictionary<string, string>>? PathMappings = null,
    List<string>? LibraryPaths = null);
