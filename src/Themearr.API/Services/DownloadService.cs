using System.Collections.Concurrent;
using Themearr.API.Data;

namespace Themearr.API.Services;

public class DownloadService(
    IThemeAudioProvider provider, Database db, IHttpClientFactory httpClientFactory,
    IConfiguration config, ILogger<DownloadService> log, LocalFolderResolver? folderResolver = null)
{
    private sealed record JobState(bool InProgress, bool Finished, string? Error, DateTime StartedAtUtc = default);
    private readonly ConcurrentDictionary<string, JobState> _jobs = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _jobLogs = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<object>> _installationResults = new();
    private readonly ConcurrentDictionary<string, byte> _activeGroups = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _activeDestinations = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly DownloaderConfiguration _downloaderConfiguration = new(db);
    private readonly LocalFolderResolver _folders = folderResolver ?? new LocalFolderResolver(db);

    private const int MaxLogLines = 300;

    // Hard ceiling on a single download. A stalled CDN connection (silent TCP drop)
    // can leave the response-stream read hanging forever; without this bound the job
    // stays "in progress" and wedges the auto-download loop until a restart.
    private TimeSpan DownloadTimeout
    {
        get
        {
            var raw = CompatibilityConfiguration.EnvironmentValue(
                          "THEMEFORGE_DOWNLOAD_TIMEOUT_SECONDS", "THEMEARR_DOWNLOAD_TIMEOUT_SECONDS")
                      ?? CompatibilityConfiguration.Setting(config, "DownloadTimeoutSeconds");
            return int.TryParse(raw, out var s) && s > 0 ? TimeSpan.FromSeconds(s) : TimeSpan.FromMinutes(15);
        }
    }

    // Job state is namespaced by media type: movie and show ids come from the same
    // MediaFolderId hash space, so a show and a movie pointing at the same folder would
    // otherwise share (and clobber) one job entry.
    private static string JobKey(string mediaType, string id) => $"{mediaType}:{id}";

    public bool Start(string id, string youtubeUrl, string mediaType = "movie")
    {
        var key = JobKey(mediaType, id);
        if (_jobs.TryGetValue(key, out var existing) && existing.InProgress)
            return false;

        var item = mediaType == "show" ? db.GetStoredShow(id) : db.GetStoredMovie(id);
        var folder = item?["folderName"]?.ToString();
        if (string.IsNullOrWhiteSpace(folder)) return false;

        var groupKey = item is null ? key : MediaGrouping.GroupKey(item, mediaType == "show");
        if (!_activeGroups.TryAdd(groupKey, 0)) return false;

        var destination = Path.GetFullPath(Path.Combine(folder, "theme.mp3"));
        if (!_activeDestinations.TryAdd(destination, 0))
        {
            _activeGroups.TryRemove(groupKey, out _);
            return false;
        }

        try
        {
            var url = NormaliseYoutubeUrl(youtubeUrl.Trim());
            var logs = _jobLogs.GetOrAdd(key, _ => new ConcurrentQueue<string>());
            while (logs.TryDequeue(out _)) { }

            _jobs[key] = new JobState(true, false, null, DateTime.UtcNow);
            _installationResults.TryRemove(key, out _);
            _ = Task.Run(() => RunAsync(id, url, mediaType, destination, groupKey));
            return true;
        }
        catch
        {
            _activeDestinations.TryRemove(destination, out _);
            _activeGroups.TryRemove(groupKey, out _);
            throw;
        }
    }

    // Defense-in-depth beyond the per-job timeout: if a job somehow stays "in progress"
    // past the timeout plus a grace margin (e.g. a backend that ignores cancellation),
    // stop counting it as blocking so a single pathological download can't wedge the
    // auto-download loop until a restart.
    private TimeSpan WatchdogGrace
    {
        get
        {
            var raw = CompatibilityConfiguration.EnvironmentValue(
                          "THEMEFORGE_DOWNLOAD_WATCHDOG_GRACE_SECONDS", "THEMEARR_DOWNLOAD_WATCHDOG_GRACE_SECONDS")
                      ?? CompatibilityConfiguration.Setting(config, "DownloadWatchdogGraceSeconds");
            return int.TryParse(raw, out var s) && s > 0 ? TimeSpan.FromSeconds(s) : TimeSpan.FromSeconds(30);
        }
    }

    public bool IsAnyInProgress()
    {
        var providerTimeout = TimeSpan.FromSeconds(_downloaderConfiguration.GetSnapshot().TimeoutSeconds + 5);
        var effectiveTimeout = providerTimeout < DownloadTimeout ? providerTimeout : DownloadTimeout;
        var staleBefore = DateTime.UtcNow - (effectiveTimeout + WatchdogGrace);
        return _jobs.Values.Any(j => j.InProgress && j.StartedAtUtc > staleBefore);
    }

    public bool IsInProgress(string id, string mediaType = "movie") =>
        _jobs.TryGetValue(JobKey(mediaType, id), out var state) && state.InProgress;

    public async Task<string?> DownloadBlockedReasonAsync(
        bool isProviderUrl, CancellationToken cancellationToken = default)
    {
        if (!isProviderUrl) return null;
        var diagnostics = await provider.CheckConfigurationAsync(false, cancellationToken);
        return diagnostics.Ready ? null : diagnostics.Summary;
    }

    /// <summary>
    /// Performs the path-only download preflight. Controllers call this before any
    /// provider diagnostics or YouTube search, so an impossible destination never
    /// starts yt-dlp. RunAsync repeats the checks immediately before writing.
    /// </summary>
    public string? DestinationBlockedReason(string id, string mediaType = "movie")
    {
        var item = mediaType == "show" ? db.GetStoredShow(id) : db.GetStoredMovie(id);
        if (item is null) return $"{mediaType} not found";

        var folder = item["folderName"]?.ToString() ?? "";
        var sourcePath = item["sourcePath"]?.ToString() ?? "";
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            var resolution = _folders.ResolveStoredSource(
                sourcePath, item["source"]?.ToString(), mediaType == "show", item["instanceId"]?.ToString());
            if (resolution.ResolvedFolderPath is null)
                return ResolutionFailureMessage(folder, resolution);
            folder = resolution.ResolvedFolderPath;
        }

        var roots = db.GetTrustedLibraryRoots();
        if (roots.Count == 0 || !Directory.Exists(folder) || !ThemeFiles.IsWithinRoots(folder, roots))
            return "Refusing to write outside the configured library roots.\n" +
                $"Current resolved destination: {LogSanitizer.Clean(folder)}\n" +
                $"Configured local roots: {FormatRoots(roots)}\n" +
                "Likely resolution: configure a path mapping from the Plex/Radarr source root " +
                "to the mounted container root and run a full sync.";
        return null;
    }

    // True if this URL would be handled by the theme-audio provider (a YouTube URL)
    // rather than fetched directly. Used to decide whether a provider readiness
    // check applies before starting a download.
    public static bool IsProviderUrl(string url) => ExtractVideoId(url) != null;

    public object GetStatus(string id, string mediaType = "movie")
    {
        var key = JobKey(mediaType, id);
        if (!_jobs.TryGetValue(key, out var state))
            return new { inProgress = false, finished = false, error = (string?)null, logs = Array.Empty<string>() };

        _jobLogs.TryGetValue(key, out var logQueue);
        var lines = logQueue?.ToArray() ?? [];
        if (lines.Length > 50) lines = lines[^50..];

        _installationResults.TryGetValue(key, out var installations);
        return new { inProgress = state.InProgress, finished = state.Finished, error = state.Error,
            logs = lines, installations = installations ?? [] };
    }

    // Takes the namespaced job key (see JobKey), not a bare media id.
    private void AddLog(string jobKey, string message)
    {
        if (!_jobLogs.TryGetValue(jobKey, out var logQueue)) return;
        logQueue.Enqueue(message);
        while (logQueue.Count > MaxLogLines)
            logQueue.TryDequeue(out _);
    }

    private async Task RunAsync(
        string id, string url, string mediaType, string reservedDestination, string reservedGroup)
    {
        var key = JobKey(mediaType, id);
        try
        {
            var item = (mediaType == "show" ? db.GetStoredShow(id) : db.GetStoredMovie(id))
                ?? throw new KeyNotFoundException($"{mediaType} not found: {id}");

            var folder = item["folderName"]?.ToString()
                ?? throw new InvalidOperationException($"{mediaType} has no folder path");
            var effectiveId = id;

            // The source path is the trusted input for current resolution. Re-resolving
            // here repairs a stale row after settings change and fails closed when a
            // formerly valid mapping has been removed.
            var sourcePath = item["sourcePath"]?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                var resolution = _folders.ResolveStoredSource(
                    sourcePath, item["source"]?.ToString(), mediaType == "show", item["instanceId"]?.ToString());
                if (resolution.ResolvedFolderPath is null)
                    throw new UnauthorizedAccessException(ResolutionFailureMessage(folder, resolution));

                if (!SamePath(folder, resolution.ResolvedFolderPath))
                {
                    folder = resolution.ResolvedFolderPath;
                    if (mediaType == "show")
                        db.UpsertShows([new ShowRecord(folder,
                            item["source"]?.ToString() ?? "plex", item["sourceRef"]?.ToString() ?? "",
                            item["title"]?.ToString() ?? "", item["year"] as int?, sourcePath,
                            item["plexHasTheme"] is true, true,
                            item.GetValueOrDefault("instanceId")?.ToString(),
                            item.GetValueOrDefault("remoteItemId")?.ToString(),
                            item.GetValueOrDefault("qualityLabel")?.ToString(),
                            item.GetValueOrDefault("tmdbId")?.ToString(),
                            item.GetValueOrDefault("tvdbId")?.ToString(),
                            item.GetValueOrDefault("imdbId")?.ToString())]);
                    else
                        db.UpsertMovies([new MovieRecord(folder,
                            item["source"]?.ToString() ?? "plex", item["sourceRef"]?.ToString() ?? "",
                            item["title"]?.ToString() ?? "", item["year"] as int?, sourcePath,
                            item.GetValueOrDefault("instanceId")?.ToString(),
                            item.GetValueOrDefault("remoteItemId")?.ToString(),
                            item.GetValueOrDefault("qualityLabel")?.ToString(),
                            item.GetValueOrDefault("tmdbId")?.ToString(),
                            item.GetValueOrDefault("imdbId")?.ToString())]);
                    effectiveId = MediaFolderId.For(folder);
                }
            }

            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                throw new ArgumentException("Invalid URL");

            var videoId = ExtractVideoId(url);

            string? themeTitle = null;

            // Confine writes to the configured library roots so a malicious/compromised
            // Plex server can't redirect a theme write to an arbitrary directory (e.g.
            // /opt/themearr). No roots means no write authority.
            var roots = db.GetTrustedLibraryRoots();
            if (roots.Count == 0 || !Directory.Exists(folder) || !ThemeFiles.IsWithinRoots(folder, roots))
                throw new UnauthorizedAccessException(
                    "Refusing to write outside the configured library roots.\n" +
                    $"Current resolved destination: {LogSanitizer.Clean(folder)}\n" +
                    $"Configured local roots: {FormatRoots(roots)}\n" +
                    "Likely resolution: configure a path mapping from the Plex/Radarr source root " +
                    "to the mounted container root and run a full sync.");

            var outputPath = Path.Combine(folder, "theme.mp3");

            var locations = GetLogicalLocations(item, mediaType);
            var existingTheme = locations
                .OrderBy(location => InstancePriority(location))
                .Select(location => ThemeFiles.FindThemeFile(location["folderName"]?.ToString() ?? ""))
                .FirstOrDefault(path => path is not null && new FileInfo(path).Length > 0);

            // Fail fast with an actionable message if the folder isn't writable — the
            // common Proxmox/LXC case where the themearr service user lacks permission
            // on a bind-mounted media folder. Without this the download fails opaquely
            // for every movie and the auto-loop just silently cools each one down.
            if (!ThemeFiles.IsDirectoryWritable(folder))
                throw new UnauthorizedAccessException(
                    $"Cannot write to \"{folder}\". The themearr service user needs write permission on " +
                    $"this {mediaType} folder — on Proxmox/LXC, add the themearr user to your media group.");

            // Bound the whole download (incl. the response-stream read, which
            // HttpClient.Timeout does NOT cover once streaming) so a stalled
            // connection can't hang the job forever.
            var providerTimeout = TimeSpan.FromSeconds(_downloaderConfiguration.GetSnapshot().TimeoutSeconds + 5);
            var operationTimeout = videoId == null || DownloadTimeout < providerTimeout
                ? DownloadTimeout
                : providerTimeout;
            using var cts = new CancellationTokenSource(operationTimeout);
            var token = cts.Token;

            if (!ThemeFiles.HasUsableTheme(folder) && existingTheme is not null)
            {
                AddLog(key, "[ThemeForge] Reusing an existing validated theme from another quality location…");
                await ThemeFiles.CopyAtomicAsync(existingTheme, outputPath, replace: false, token);
                themeTitle = "Copied from existing quality location";
            }
            else if (videoId != null)
            {
                // YouTube URL — delegate to the configured theme-audio provider.
                themeTitle = await provider.DownloadAsync(videoId, outputPath, msg => AddLog(key, msg), token);
            }
            else
            {
                // Non-YouTube URL — download directly
                AddLog(key, "[ThemeForge] Downloading from URL…");

                using var dlResp = await FetchFollowingSafeRedirectsAsync(url, token);

                if (!dlResp.IsSuccessStatusCode)
                {
                    var errBody = await dlResp.Content.ReadAsStringAsync(token);
                    var snippet = errBody.Length > 300 ? errBody[..300] : errBody;
                    throw new InvalidOperationException($"Download failed ({(int)dlResp.StatusCode}): {snippet}");
                }

                // Atomic: stream to theme.mp3.part then move into place, so a failed or
                // empty download never clobbers a previously-good theme.
                await ThemeFiles.WriteAtomicAsync(
                    await dlResp.Content.ReadAsStreamAsync(token), outputPath, StreamLimits.MaxThemeBytes, token);
            }

            // Remove stale alternate-extension theme files (e.g. an old theme.m4a) now
            // that the new theme.mp3 is safely in place — never before the download.
            foreach (var f in Directory.EnumerateFiles(folder, "theme.*"))
                if (!string.Equals(f, outputPath, StringComparison.Ordinal)
                    && Path.GetExtension(f) is not (".part" or ".ytdl"))
                    try { File.Delete(f); } catch { /* best effort */ }

            AddLog(key, "[ThemeForge] Download complete.");

            var installationResults = new List<object>();
            foreach (var location in locations.OrderBy(InstancePriority))
            {
                var locationFolder = location["folderName"]?.ToString() ?? "";
                var locationId = location["id"]?.ToString() ?? "";
                var instanceId = location["instanceId"]?.ToString() ?? "";
                var instanceName = db.GetArrInstance(instanceId)?.Name
                    ?? (location["source"]?.ToString() ?? mediaType);
                var label = location["qualityLabel"]?.ToString();
                try
                {
                    if (SamePath(locationFolder, folder))
                    {
                        installationResults.Add(new { id = locationId, instanceId, instanceName,
                            qualityLabel = label, status = "installed", detail = (string?)null });
                        continue;
                    }
                    if (ThemeFiles.HasUsableTheme(locationFolder))
                    {
                        installationResults.Add(new { id = locationId, instanceId, instanceName,
                            qualityLabel = label, status = "existing", detail = (string?)null });
                        continue;
                    }
                    var locationRoots = db.GetTrustedLibraryRoots();
                    if (!Directory.Exists(locationFolder) || !ThemeFiles.IsWithinRoots(locationFolder, locationRoots))
                        throw new UnauthorizedAccessException("location is unavailable or outside configured roots");
                    if (!ThemeFiles.IsDirectoryWritable(locationFolder))
                        throw new UnauthorizedAccessException("permission denied");
                    AddLog(key, $"[ThemeForge] Installing to {instanceName}{(string.IsNullOrEmpty(label) ? "" : $" ({label})")}…");
                    await ThemeFiles.CopyAtomicAsync(outputPath, Path.Combine(locationFolder, "theme.mp3"), false, token);
                    if (mediaType == "show") db.SetShowStatus(locationId, "downloaded");
                    else db.SetMovieStatus(locationId, "downloaded");
                    installationResults.Add(new { id = locationId, instanceId, instanceName,
                        qualityLabel = label, status = "installed", detail = (string?)null });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    var detail = ex is UnauthorizedAccessException ? ex.Message : "installation failed";
                    AddLog(key, $"[ThemeForge] {instanceName}: {detail}");
                    installationResults.Add(new { id = locationId, instanceId, instanceName,
                        qualityLabel = label, status = "failed", detail });
                }
            }
            _installationResults[key] = installationResults;

            var title = item["title"]?.ToString() ?? "";
            var year = item["year"] is int y ? y : (int?)null;
            if (mediaType == "show") db.SetShowStatus(effectiveId, "downloaded");
            else db.SetMovieStatus(effectiveId, "downloaded");
            db.AddThemeHistory(effectiveId, title, year, themeTitle, url, mediaType, installationResults);
            _jobs[key] = new JobState(false, true, null);
        }
        catch (ThemeAudioDownloadException ex)
        {
            log.LogWarning(ex, "Local theme download failed for {JobKey} ({FailureKind})",
                LogSanitizer.Clean(key), ex.Kind);
            AddLog(key, $"[ThemeForge] {ex.Message}");
            _jobs[key] = new JobState(false, true, ex.Message);
        }
        catch (OperationCanceledException)
        {
            var msg = $"Download timed out after {DownloadTimeout.TotalSeconds:0}s and was aborted.";
            log.LogWarning("Download for {JobKey} timed out after {Timeout}", LogSanitizer.Clean(key), DownloadTimeout);
            AddLog(key, $"[ThemeForge] {msg}");
            _jobs[key] = new JobState(false, true, msg);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Download failed for {JobKey}", LogSanitizer.Clean(key));
            _jobs[key] = new JobState(false, true, ex.Message);
        }
        finally
        {
            _activeDestinations.TryRemove(reservedDestination, out _);
            _activeGroups.TryRemove(reservedGroup, out _);
        }
    }

    private List<Dictionary<string, object?>> GetLogicalLocations(
        IReadOnlyDictionary<string, object?> item, string mediaType)
    {
        var rows = mediaType == "show" ? db.GetStoredShows() : db.GetStoredMovies();
        var key = MediaGrouping.GroupKey(item, mediaType == "show");
        return rows.Where(row => MediaGrouping.GroupKey(row, mediaType == "show") == key).ToList();
    }

    private int InstancePriority(IReadOnlyDictionary<string, object?> location)
    {
        var id = location.GetValueOrDefault("instanceId")?.ToString();
        return id is null ? int.MaxValue : db.GetArrInstance(id)?.Priority ?? int.MaxValue;
    }

    private string ResolutionFailureMessage(string currentFolder, PathResolutionResult resolution) =>
        "The stored media path can no longer be resolved safely.\n" +
        $"Source path: {LogSanitizer.Clean(resolution.SourceFolderPath)}\n" +
        $"Current resolved destination: {LogSanitizer.Clean(currentFolder)}\n" +
        $"Configured local roots: {FormatRoots(db.GetTrustedLibraryRoots())}\n" +
        $"Reason: {resolution.FailureReason}\n" +
        "Likely resolution: configure a valid path mapping and Docker mount, then run a full sync.";

    private static string FormatRoots(IReadOnlyList<string> roots) =>
        roots.Count == 0 ? "(none)" : string.Join(", ", roots.Take(5).Select(LogSanitizer.Clean));

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }
    }

    // Fetches `url`, following redirects manually so EVERY hop is re-validated against
    // the SSRF guard. A 3xx to an internal address (169.254.x, 10.x, …) is the classic
    // bypass of an initial-host-only check; the download-url endpoint validates the
    // first host, and this closes the redirect gap. Uses the "no-redirect" client.
    private async Task<HttpResponseMessage> FetchFollowingSafeRedirectsAsync(string url, CancellationToken ct)
    {
        const int MaxRedirects = 5;
        var http = httpClientFactory.CreateClient("no-redirect");
        http.Timeout = Timeout.InfiniteTimeSpan; // the CTS bounds the whole operation

        var current = new Uri(url);
        for (var hop = 0; ; hop++)
        {
            var resp = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } location)
            {
                resp.Dispose();
                if (hop >= MaxRedirects)
                    throw new InvalidOperationException("Too many redirects while downloading.");
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (next.Scheme is not ("http" or "https") || HostGuard.IsPrivateOrLoopback(next.Host))
                    throw new InvalidOperationException(
                        "Refusing to follow a redirect to a private, loopback, or non-http(s) address.");
                current = next;
                continue;
            }
            return resp;
        }
    }

    // Single source of truth for YouTube URL parsing. Returns null for non-YouTube URLs.
    private static string? ExtractVideoId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];

        if (host is "youtube.com" or "m.youtube.com" or "music.youtube.com")
        {
            var v = System.Web.HttpUtility.ParseQueryString(uri.Query)["v"]?.Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }
        if (host is "youtu.be")
        {
            var videoId = uri.AbsolutePath.Trim('/');
            return string.IsNullOrEmpty(videoId) ? null : videoId;
        }
        return null;
    }

    private static string NormaliseYoutubeUrl(string url)
    {
        var videoId = ExtractVideoId(url);
        return videoId == null ? url : $"https://www.youtube.com/watch?v={videoId}";
    }
}
