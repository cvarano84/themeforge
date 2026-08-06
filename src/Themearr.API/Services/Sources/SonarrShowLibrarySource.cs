using System.Net;
using System.Text.Json;
using Themearr.API.Data;

namespace Themearr.API.Services.Sources;

/// <summary>Imports downloaded TV series from Sonarr v3.</summary>
public sealed class SonarrShowLibrarySource(
    Database db, LocalFolderResolver folders, IHttpClientFactory factory) : IShowLibrarySource
{
    private readonly AsyncLocal<ArrInstance?> _activeInstance = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _instanceSyncs = new();
    private volatile IReadOnlySet<string> _lastSuccessfulInstanceIds = new HashSet<string>();
    public IReadOnlySet<string> LastSuccessfulInstanceIds => _lastSuccessfulInstanceIds;
    public const string ClientName = "sonarr";
    public const string HealthClientName = "sonarr-health";
    private const string MalformedResponseMessage =
        "Sonarr returned an unexpected response. Check the URL points at Sonarr and not another service.";

    public string Name => "sonarr";
    public TimeSpan SyncInterval => TimeSpan.FromMinutes(15);

    private (string Url, string Key) Config() => _activeInstance.Value is { } instance
        ? (instance.Url, instance.ApiKey)
        : (db.GetSetting("sonarr_url", "").Trim().TrimEnd('/'), db.GetSetting("sonarr_api_key", ""));

    private static HttpRequestMessage Request(string url, string key, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{url}{path}");
        request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        return request;
    }

    public string? SyncBlockedReason
    {
        get
        {
            if (db.GetArrInstances("sonarr", enabledOnly: true).Count > 0) return null;
            var (url, key) = Config();
            return string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key)
                ? "Sonarr is not configured — set its URL and API key in Settings."
                : null;
        }
    }

    public async Task<IReadOnlyList<ShowRecord>> FetchAsync(Action<string> log, CancellationToken ct)
    {
        var instances = db.GetArrInstances("sonarr", enabledOnly: true);
        if (instances.Count == 0)
        {
            _lastSuccessfulInstanceIds = new HashSet<string>();
            return await FetchConfiguredAsync(log, ct);
        }

        using var concurrency = new SemaphoreSlim(3, 3);
        var successes = new System.Collections.Concurrent.ConcurrentBag<(ArrInstance Instance, IReadOnlyList<ShowRecord> Shows)>();
        var failures = new System.Collections.Concurrent.ConcurrentBag<(ArrInstance Instance, string Detail)>();
        await Task.WhenAll(instances.Select(async instance =>
        {
            await concurrency.WaitAsync(ct);
            try
            {
                _activeInstance.Value = instance;
                try { successes.Add((instance, await FetchConfiguredAsync(log, ct))); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var detail = SafeFailure(ex);
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
            throw new InvalidOperationException("All enabled Sonarr instances failed. " +
                string.Join(" ", failures.OrderBy(f => f.Instance.Priority)
                    .Select(f => $"{f.Instance.Name}: {f.Detail}")));
        log($"{successes.Count} of {instances.Count} Sonarr instances synced successfully.");
        db.SetSetting("last_show_sync_unresolved_count", instances.Sum(i =>
            db.GetArrInstance(i.Id)?.UnresolvedPathCount ?? 0).ToString());
        return successes.SelectMany(s => s.Shows).ToList();
    }

    private async Task<IReadOnlyList<ShowRecord>> FetchConfiguredAsync(Action<string> log, CancellationToken ct)
    {
        var (url, key) = Config();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
            throw new InvalidOperationException("Sonarr is not configured — set its URL and API key in Settings.");

        var instance = _activeInstance.Value;
        log($"Fetching series from {(instance?.Name ?? "Sonarr")} at {url}");
        var http = factory.CreateClient(ClientName);
        HttpResponseMessage response;
        try
        {
            using var request = Request(url, key, "/api/v3/series");
            response = await http.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Sonarr did not respond within {http.Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Sonarr is unreachable. Check it is running and the URL in Settings is correct.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("Sonarr rejected the API key (401). Check the key in Settings.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Sonarr returned HTTP {(int)response.StatusCode} listing series.");

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
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException(MalformedResponseMessage);

                var result = new List<ShowRecord>();
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
                        if (item.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
                        var reported = item.TryGetProperty("path", out var pathNode)
                            ? pathNode.GetString()?.Trim().TrimEnd('/', '\\') ?? ""
                            : "";
                        if (string.IsNullOrEmpty(reported)) continue;

                        var downloadedEpisodes = 0;
                        long sizeOnDisk = 0;
                        if (item.TryGetProperty("statistics", out var stats) && stats.ValueKind == JsonValueKind.Object)
                        {
                            if (stats.TryGetProperty("episodeFileCount", out var files)) files.TryGetInt32(out downloadedEpisodes);
                            if (stats.TryGetProperty("sizeOnDisk", out var size)) size.TryGetInt64(out sizeOnDisk);
                        }
                        if (downloadedEpisodes <= 0 && sizeOnDisk <= 0) continue;

                        var title = item.TryGetProperty("title", out var titleNode) ? titleNode.GetString() ?? "" : "";
                        var id = item.TryGetProperty("id", out var idNode) ? idNode.GetRawText().Trim('"') : "";
                        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) throw new InvalidOperationException();

                        int? year = item.TryGetProperty("year", out var yearNode) &&
                                    yearNode.TryGetInt32(out var parsedYear) && parsedYear > 0
                            ? parsedYear : null;
                        if (year is null && item.TryGetProperty("firstAired", out var firstAired) &&
                            DateTimeOffset.TryParse(firstAired.GetString(), out var aired))
                            year = aired.Year;

                        var hasPoster = item.TryGetProperty("images", out var images) &&
                            images.ValueKind == JsonValueKind.Array &&
                            images.EnumerateArray().Any(image =>
                                image.ValueKind == JsonValueKind.Object &&
                                image.TryGetProperty("coverType", out var cover) &&
                                string.Equals(cover.GetString(), "poster", StringComparison.OrdinalIgnoreCase));

                        var resolution = folders.ResolveFolderDetailed(reported, instance?.Id, "sonarr");
                        var resolved = resolution.ResolvedFolderPath ?? "";
                        if (string.IsNullOrEmpty(resolved))
                        {
                            unresolvedCount++;
                            if (resolution.FailureReason?.Contains("outside", StringComparison.OrdinalIgnoreCase) == true)
                                outsideRoots++;
                            if (unresolvedSample.Length == 0) unresolvedSample = reported;
                            log($"Skipping {title} — unresolved Sonarr path: {reported} (add a Path Mapping)");
                            continue;
                        }

                        if (resolution.ResolutionMode == "direct") direct++;
                        else if (resolution.ResolutionMode == "mapping") mapped++;
                        else if (resolution.ResolutionMode == "suffix") suffix++;

                        var tvdbId = ReadExternalId(item, "tvdbId");
                        var tmdbId = ReadExternalId(item, "tmdbId");
                        var imdbId = item.TryGetProperty("imdbId", out var imdb) && imdb.ValueKind == JsonValueKind.String
                            ? imdb.GetString() : null;
                        var sourceRef = instance is null ? id : $"sonarr:{instance.Id}:{id}";
                        result.Add(new ShowRecord(resolved, "sonarr", sourceRef, title, year, reported,
                            false, hasPoster, instance?.Id, id, instance?.QualityLabel, tmdbId, tvdbId, imdbId));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
                    {
                        log("Skipping a malformed series entry from Sonarr.");
                    }
                }

                db.SetSetting("last_show_sync_unresolved_count", unresolvedCount.ToString());
                db.SetSetting("last_show_sync_unresolved_sample", unresolvedSample);
                db.SetSetting("last_show_sync_received", received.ToString());
                db.SetSetting("last_show_sync_direct", direct.ToString());
                db.SetSetting("last_show_sync_mapping", mapped.ToString());
                db.SetSetting("last_show_sync_suffix", suffix.ToString());
                db.SetSetting("last_show_sync_duplicates", "0");
                db.SetSetting("last_show_sync_outside_roots", outsideRoots.ToString());
                if (instance is not null)
                    db.RecordArrInstanceSync(instance.Id, true, null, unresolvedCount, unresolvedSample);
                log($"Sonarr reported {result.Count} downloaded series");
                return result;
            }
        }
    }

    public async Task<int?> TrySyncInstanceAsync(string instanceId, Action<string> log, CancellationToken ct)
    {
        var instance = db.GetArrInstance(instanceId);
        if (instance is null || instance.ServiceType != "sonarr" || !instance.Enabled)
            throw new InvalidOperationException("Enabled Sonarr instance not found.");
        var gate = _instanceSyncs.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return null;
        try
        {
            _activeInstance.Value = instance;
            var shows = await FetchConfiguredAsync(log, ct);
            db.UpsertShows(shows);
            var refreshed = db.GetArrInstance(instanceId);
            if (shows.Count > 0 && refreshed?.UnresolvedPathCount == 0)
                db.PruneShowsExcept(shows.Select(s => s.Folder), "sonarr", instanceId);
            return shows.Count;
        }
        catch (Exception ex)
        {
            db.RecordArrInstanceSync(instanceId, false, SafeFailure(ex));
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

    private static string SafeFailure(Exception ex) => ex switch
    {
        TaskCanceledException => "connection timed out.",
        HttpRequestException => "unreachable.",
        InvalidOperationException when !string.IsNullOrWhiteSpace(ex.Message) => ex.Message,
        _ => "Sonarr sync failed. Check the instance URL and server logs."
    };

    public async Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct)
    {
        var remoteRef = sourceRef;
        ArrInstance? posterInstance = null;
        var parts = sourceRef.Split(':', 3);
        if (parts.Length == 3 && parts[0] == "sonarr")
        {
            posterInstance = db.GetArrInstance(parts[1]);
            remoteRef = parts[2];
        }
        var (url, key) = posterInstance is null ? Config() : (posterInstance.Url, posterInstance.ApiKey);
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(sourceRef)) return null;

        var http = factory.CreateClient(ClientName);
        using var request = Request(url, key, $"/api/v3/mediacover/{Uri.EscapeDataString(remoteRef)}/poster.jpg");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;

        var buffer = new MemoryStream();
        try
        {
            await StreamLimits.CopyWithLimitAsync(
                await response.Content.ReadAsStreamAsync(ct), buffer, StreamLimits.MaxPosterBytes, ct);
        }
        catch (InvalidOperationException) { return null; }
        buffer.Position = 0;
        return buffer;
    }

    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        var instances = db.GetArrInstances("sonarr", enabledOnly: true);
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
            ? $"DEGRADED: {healthy} of {instances.Count} Sonarr instances are healthy. {detail}"
            : $"All enabled Sonarr instances are unavailable. {detail}";
    }

    public async Task<string?> ProbeAsync(string url, string apiKey, CancellationToken ct)
    {
        url = (url ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(apiKey))
            return "Sonarr is not configured — set its URL and API key in Settings.";

        var http = factory.CreateClient(HealthClientName);
        try
        {
            using var request = Request(url, apiKey, "/api/v3/system/status");
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return "Sonarr rejected the API key (401). Check the key in Settings.";
            if (!response.IsSuccessStatusCode)
                return $"Sonarr returned HTTP {(int)response.StatusCode}.";
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"Sonarr did not respond within {http.Timeout.TotalSeconds:0} seconds.";
        }
        catch (HttpRequestException)
        {
            return "Sonarr is unreachable. Check it is running and the URL in Settings is correct.";
        }
    }
}
