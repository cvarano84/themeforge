using System.Net;
using System.Text.Json;
using Themearr.API.Data;

namespace Themearr.API.Services.Sources;

/// <summary>
/// Radarr as a library source. Radarr knows every movie's folder, title, year and
/// whether the film is actually downloaded — everything ThemeForge needs — so a Radarr
/// user needs no Plex at all. Because theme.mp3 is read by Jellyfin, Emby and Kodi
/// too, this is what makes ThemeForge useful to them.
/// </summary>
public class RadarrLibrarySource(
    Database db,
    LocalFolderResolver folders,
    IHttpClientFactory factory,
    ThemeReconciliationService? themeReconciler = null)
    : ILibrarySource
{
    private readonly AsyncLocal<ArrInstance?> _activeInstance = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _instanceSyncs = new();
    private volatile IReadOnlySet<string> _lastSuccessfulInstanceIds = new HashSet<string>();
    public IReadOnlySet<string> LastSuccessfulInstanceIds => _lastSuccessfulInstanceIds;
    /// <summary>
    /// Named client for fetching library data (movies, posters), configured in Program.cs
    /// with a generous timeout — a large library legitimately takes longer than a bare
    /// reachability probe.
    /// </summary>
    public const string ClientName = "radarr";

    /// <summary>
    /// Named client used only for probing reachability (<see cref="CheckAsync"/> and the
    /// Settings "Test" endpoint via <see cref="ProbeAsync"/>), configured in Program.cs with
    /// a short timeout — comfortably inside HealthCache's refresh budget, so an unreachable
    /// Radarr surfaces the hand-written "did not respond within Ns" message instead of
    /// racing HealthCache's own timeout and losing. Mirrors PlexLibrarySource.ClientName.
    /// </summary>
    public const string HealthClientName = "radarr-health";

    public string Name => "radarr";

    /// <summary>Radarr is local and cheap to poll, so a new import gets its theme quickly.</summary>
    public TimeSpan SyncInterval => TimeSpan.FromMinutes(15);

    /// <summary>
    /// The one message shown for any malformed Radarr body — invalid JSON, a JSON value
    /// that isn't the expected array, or a field of an unexpected type. Deliberately does
    /// not include the underlying parser exception's text, matching every other message
    /// in this class: raw framework text is either cryptic or (in other classes) capable
    /// of leaking internals, so callers only ever see this hand-written sentence.
    /// </summary>
    private const string MalformedResponseMessage =
        "Radarr returned an unexpected response. Check the URL points at Radarr and not another service.";

    private (string Url, string Key) Config() => _activeInstance.Value is { } instance
        ? (instance.Url, instance.ApiKey)
        : (db.GetSetting("radarr_url", "").TrimEnd('/'), db.GetSetting("radarr_api_key", ""));

    private HttpRequestMessage Request(string url, string key, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{url}{path}");
        // Header, never a query parameter — the key must not end up in a URL that could
        // be logged by a proxy.
        request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        return request;
    }

    public async Task<IReadOnlyList<MovieRecord>> FetchAsync(Action<string> log, CancellationToken ct)
    {
        var instances = db.GetArrInstances("radarr", enabledOnly: true);
        if (instances.Count == 0)
        {
            _lastSuccessfulInstanceIds = new HashSet<string>();
            return await FetchConfiguredAsync(log, ct);
        }

        using var concurrency = new SemaphoreSlim(3, 3);
        var successes = new System.Collections.Concurrent.ConcurrentBag<(ArrInstance Instance, IReadOnlyList<MovieRecord> Movies)>();
        var failures = new System.Collections.Concurrent.ConcurrentBag<(ArrInstance Instance, string Detail)>();
        await Task.WhenAll(instances.Select(async instance =>
        {
            await concurrency.WaitAsync(ct);
            try
            {
                _activeInstance.Value = instance;
                try
                {
                    var movies = await FetchConfiguredAsync(log, ct);
                    successes.Add((instance, movies));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var detail = SafeFailure(ex, "Radarr");
                    db.RecordArrInstanceSync(instance.Id, false, detail);
                    failures.Add((instance, detail));
                    log($"{instance.Name} failed: {detail}");
                }
            }
            finally
            {
                _activeInstance.Value = null;
                concurrency.Release();
            }
        }));

        _lastSuccessfulInstanceIds = successes.Select(s => s.Instance.Id).ToHashSet(StringComparer.Ordinal);
        if (successes.IsEmpty)
            throw new InvalidOperationException("All enabled Radarr instances failed. " +
                string.Join(" ", failures.OrderBy(f => f.Instance.Priority)
                    .Select(f => $"{f.Instance.Name}: {f.Detail}")));

        log($"{successes.Count} of {instances.Count} Radarr instances synced successfully.");
        db.SetSetting("last_sync_unresolved_count", instances.Sum(i =>
            db.GetArrInstance(i.Id)?.UnresolvedPathCount ?? 0).ToString());
        return successes.SelectMany(s => s.Movies).ToList();
    }

    private async Task<IReadOnlyList<MovieRecord>> FetchConfiguredAsync(Action<string> log, CancellationToken ct)
    {
        var (url, key) = Config();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
            throw new InvalidOperationException("Radarr is not configured — set its URL and API key in Settings.");

        var instance = _activeInstance.Value;
        log($"Fetching movies from {(instance?.Name ?? "Radarr")} at {url}");

        var http = factory.CreateClient(ClientName);
        using var request = Request(url, key, "/api/v3/movie");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Radarr returned HTTP {(int)response.StatusCode} listing movies.");

        // A malformed body (truncated JSON, an HTML error page from a misconfigured
        // reverse proxy, etc.) throws JsonException here. Converted to the same clean,
        // hand-written message used everywhere else in this class rather than letting the
        // parser's own text — meaningless to a user picking a URL in Settings — reach them
        // through SyncService's generic catch.
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(MalformedResponseMessage);
        }

        using (doc)
        {
            // A well-formed JSON value that isn't an array (e.g. an error object from a
            // service that merely happens to speak JSON) — checked explicitly rather than
            // letting EnumerateArray() throw, so the message stays the same clean sentence
            // instead of "...requires an element of type 'Array'...".
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(MalformedResponseMessage);

            var movies = new List<MovieRecord>();
            var unresolvedCount = 0;
            var unresolvedSample = "";
            var received = 0;
            var direct = 0;
            var mapped = 0;
            var suffix = 0;
            var outsideRoots = 0;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                received++;
                try
                {
                    // Monitored but not downloaded: a folder may exist, but there is no
                    // film for a theme to accompany yet.
                    if (!item.TryGetProperty("hasFile", out var hasFile) || !hasFile.GetBoolean()) continue;

                    var reported = item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                    // Trim a trailing separator before it's used for anything below: left
                    // in, it would double up with the dummy filename appended just below
                    // ("...//placeholder.mkv"), and PlexPath.ParentDir (which trims only
                    // one trailing separator) would then hand back a folder string with a
                    // trailing slash baked in — splitting this movie's identity from the
                    // same directory resolved without the slash.
                    reported = reported.TrimEnd('/', '\\');
                    if (string.IsNullOrEmpty(reported)) continue;

                    var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var year = item.TryGetProperty("year", out var y) && y.TryGetInt32(out var yr) && yr > 0
                        ? yr : (int?)null;
                    var id = item.TryGetProperty("id", out var i) ? i.GetRawText().Trim('"') : "";

                    // Radarr reports paths from its own filesystem's perspective, exactly
                    // as Plex does — a container may call it /movies where ThemeForge sees
                    // /mnt/media. LocalFolderResolver.Resolve expects a *file* path and
                    // returns its containing folder, but Radarr reports the folder
                    // directly, so a dummy filename is appended here to reuse the existing
                    // resolver unchanged rather than duplicating its logic.
                    var resolution = folders.ResolveFolderDetailed(reported, instance?.Id, "radarr");
                    var folder = resolution.ResolvedFolderPath ?? "";
                    if (string.IsNullOrEmpty(folder))
                    {
                        unresolvedCount++;
                        if (resolution.FailureReason?.Contains("outside", StringComparison.OrdinalIgnoreCase) == true)
                            outsideRoots++;
                        if (unresolvedSample.Length == 0) unresolvedSample = reported;
                        log($"Skipping {title} — unresolved path: {reported}  (add a Path Mapping from this path to where it's mounted in ThemeForge)");
                        continue;
                    }

                    if (resolution.ResolutionMode == "direct") direct++;
                    else if (resolution.ResolutionMode == "mapping") mapped++;
                    else if (resolution.ResolutionMode == "suffix") suffix++;

                    var tmdbId = ReadExternalId(item, "tmdbId");
                    var imdbId = item.TryGetProperty("imdbId", out var imdb) && imdb.ValueKind == JsonValueKind.String
                        ? imdb.GetString() : null;
                    var sourceRef = instance is null ? id : $"radarr:{instance.Id}:{id}";
                    movies.Add(new MovieRecord(folder, "radarr", sourceRef, title, year, reported,
                        instance?.Id, id, instance?.QualityLabel, tmdbId, imdbId));
                }
                catch (InvalidOperationException)
                {
                    // A field had a type Radarr's own API never sends (e.g. hasFile as a
                    // string, path as a number) — most likely a single corrupt entry
                    // rather than a wrong URL, since the response as a whole did parse as
                    // the expected array. Skip just this movie so one bad entry doesn't
                    // cost every other movie in the library its theme.
                    log("Skipping a movie entry from Radarr — one of its fields had an unexpected type.");
                }
            }

            // Read by LibraryPathsCheck; overwritten every sync so a fixed mapping clears it.
            db.SetSetting("last_sync_unresolved_count", unresolvedCount.ToString());
            db.SetSetting("last_sync_unresolved_sample", unresolvedSample);
            db.SetSetting("last_sync_received", received.ToString());
            db.SetSetting("last_sync_direct", direct.ToString());
            db.SetSetting("last_sync_mapping", mapped.ToString());
            db.SetSetting("last_sync_suffix", suffix.ToString());
            db.SetSetting("last_sync_duplicates", "0");
            db.SetSetting("last_sync_outside_roots", outsideRoots.ToString());

            if (instance is not null)
                db.RecordArrInstanceSync(instance.Id, true, null, unresolvedCount, unresolvedSample);

            log($"Radarr reported {movies.Count} downloaded movies");
            return movies;
        }
    }

    public async Task<int?> TrySyncInstanceAsync(string instanceId, Action<string> log, CancellationToken ct)
    {
        var instance = db.GetArrInstance(instanceId);
        if (instance is null || instance.ServiceType != "radarr" || !instance.Enabled)
            throw new InvalidOperationException("Enabled Radarr instance not found.");
        var gate = _instanceSyncs.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return null;
        try
        {
            _activeInstance.Value = instance;
            var movies = await FetchConfiguredAsync(log, ct);
            db.UpsertMovies(movies);
            var refreshed = db.GetArrInstance(instanceId);
            if (movies.Count > 0 && refreshed?.UnresolvedPathCount == 0)
                db.PruneMoviesExcept(movies.Select(m => m.Folder), "radarr", instanceId);
            if (themeReconciler is not null)
                await themeReconciler.ReconcileMoviesAsync(movies, log, ct);
            return movies.Count;
        }
        catch (Exception ex)
        {
            db.RecordArrInstanceSync(instanceId, false, SafeFailure(ex, "Radarr"));
            throw;
        }
        finally
        {
            _activeInstance.Value = null;
            gate.Release();
        }
    }

    private static string? ReadExternalId(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        return null;
    }

    private static string SafeFailure(Exception ex, string service) => ex switch
    {
        TaskCanceledException => "connection timed out.",
        HttpRequestException => "unreachable.",
        InvalidOperationException when !string.IsNullOrWhiteSpace(ex.Message) => ex.Message,
        _ => $"{service} sync failed. Check the instance URL and server logs."
    };

    public async Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct)
    {
        var remoteRef = sourceRef;
        ArrInstance? posterInstance = null;
        var parts = sourceRef.Split(':', 3);
        if (parts.Length == 3 && parts[0] == "radarr")
        {
            posterInstance = db.GetArrInstance(parts[1]);
            remoteRef = parts[2];
        }
        var (url, key) = posterInstance is null ? Config() : (posterInstance.Url, posterInstance.ApiKey);
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(sourceRef))
            return null;

        var http = factory.CreateClient(ClientName);
        using var request = Request(url, key, $"/api/v3/mediacover/{Uri.EscapeDataString(remoteRef)}/poster.jpg");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;

        // Buffer the bytes under a cap rather than handing back response.Content's
        // stream: the HttpResponseMessage is disposed when this method returns (the
        // `using` above), so its stream must not outlive the call.
        var buffer = new MemoryStream();
        try
        {
            await StreamLimits.CopyWithLimitAsync(
                await response.Content.ReadAsStreamAsync(ct), buffer, StreamLimits.MaxPosterBytes, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        buffer.Position = 0;
        return buffer;
    }

    /// <summary>Mirrors the guard at the top of <see cref="FetchAsync"/> — a sync must fail
    /// this fast, before a background task even starts, rather than only inside it.</summary>
    public string? SyncBlockedReason
    {
        get
        {
            var configured = db.GetArrInstances("radarr", enabledOnly: true);
            if (configured.Count > 0) return null;
            var (url, key) = Config();
            return string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key)
                ? "Radarr is not configured — set its URL and API key in Settings."
                : null;
        }
    }

    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        var instances = db.GetArrInstances("radarr", enabledOnly: true);
        if (instances.Count == 0)
        {
            var (url, key) = Config();
            return await ProbeAsync(url, key, ct);
        }
        var results = await Task.WhenAll(instances.Select(async instance =>
        {
            var reason = await ProbeAsync(instance.Url, instance.ApiKey, ct);
            db.RecordArrInstanceHealth(instance.Id, reason is null, reason);
            return (Instance: instance, Reason: reason);
        }));
        var healthy = results.Count(r => r.Reason is null);
        if (healthy == instances.Count) return null;
        var detail = string.Join(" ", results.Where(r => r.Reason is not null)
            .Select(r => $"{r.Instance.Name}: {r.Reason}"));
        return healthy > 0
            ? $"DEGRADED: {healthy} of {instances.Count} Radarr instances are healthy. {detail}"
            : $"All enabled Radarr instances are unavailable. {detail}";
    }

    /// <summary>
    /// Probes Radarr at the given URL/key without touching stored settings — used both by
    /// <see cref="CheckAsync"/> (stored config) and by the Settings "Test" endpoint (the
    /// values the user just typed, before they've been saved). Never writes to the
    /// database, so a test can never race a scheduled sync or corrupt saved credentials.
    /// </summary>
    public async Task<string?> ProbeAsync(string url, string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(apiKey))
            return "Radarr is not configured — set its URL and API key in Settings.";
        url = url.TrimEnd('/');

        var http = factory.CreateClient(HealthClientName);
        try
        {
            using var request = Request(url, apiKey, "/api/v3/system/status");
            using var response = await http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return "Radarr rejected the API key (401). Check the key in Settings → Library source.";
            if (!response.IsSuccessStatusCode)
                return $"Radarr returned HTTP {(int)response.StatusCode}.";
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"Radarr did not respond within {http.Timeout.TotalSeconds:0} seconds.";
        }
        catch (HttpRequestException)
        {
            return "Radarr is unreachable. Check it is running and the URL in Settings is correct.";
        }
    }
}
