using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Themearr.API.Data;
using Themearr.API.Services.Sources;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/settings/arr-instances")]
public sealed class ArrInstancesController(
    Database db, RadarrLibrarySource radarr, SonarrShowLibrarySource sonarr) : ControllerBase
{
    [HttpGet]
    public IActionResult List() => Ok(db.GetArrInstances().Select(Redacted));

    [HttpPost]
    [Consumes("application/json")]
    public IActionResult Create([FromBody] ArrInstancePayload payload)
    {
        if (Validate(payload, requireKey: true) is { } invalid) return BadRequest(new { detail = invalid });
        var service = NormalizeService(payload.ServiceType);
        var url = Database.NormalizeArrUrl(payload.Url);
        if (db.ArrInstanceUrlExists(service, url))
            return Conflict(new { detail = $"A {ServiceName(service)} instance already uses this URL." });
        try
        {
            var created = db.CreateArrInstance(service, payload.Name!, url, payload.ApiKey!,
                payload.Enabled, payload.QualityLabel, payload.Priority, payload.Tags);
            return Created($"/api/settings/arr-instances/{created.Id}", Redacted(created));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return Conflict(new { detail = $"A {ServiceName(service)} instance already uses this URL." });
        }
    }

    [HttpPut("{instanceId}")]
    [Consumes("application/json")]
    public IActionResult Update(string instanceId, [FromBody] ArrInstancePayload payload)
    {
        var current = db.GetArrInstance(instanceId);
        if (current is null) return NotFound(new { detail = "Arr instance not found." });
        if (Validate(payload, requireKey: false) is { } invalid) return BadRequest(new { detail = invalid });

        var service = NormalizeService(payload.ServiceType);
        var url = Database.NormalizeArrUrl(payload.Url);
        var urlChanged = !string.Equals(url, current.Url, StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(service, current.ServiceType, StringComparison.Ordinal);
        if (urlChanged && string.IsNullOrWhiteSpace(payload.ApiKey))
            return BadRequest(new { detail = "Enter the API key for the new server URL." });
        if (db.ArrInstanceUrlExists(service, url, instanceId))
            return Conflict(new { detail = $"A {ServiceName(service)} instance already uses this URL." });

        try
        {
            var updated = db.UpdateArrInstance(instanceId, service, payload.Name!, url, payload.ApiKey,
                payload.Enabled, payload.QualityLabel, payload.Priority, payload.Tags)!;
            return Ok(Redacted(updated));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return Conflict(new { detail = $"A {ServiceName(service)} instance already uses this URL." });
        }
    }

    [HttpDelete("{instanceId}")]
    public IActionResult Delete(string instanceId) => db.DeleteArrInstance(instanceId)
        ? Ok(new { deleted = true })
        : NotFound(new { detail = "Arr instance not found." });

    [HttpPost("test")]
    [Consumes("application/json")]
    public async Task<IActionResult> Test([FromBody] ArrInstanceTestPayload payload, CancellationToken ct)
    {
        var service = NormalizeService(payload.ServiceType);
        if (service.Length == 0)
            return BadRequest(new { detail = "serviceType must be 'radarr' or 'sonarr'." });
        var url = Database.NormalizeArrUrl(payload.Url);
        if (!ValidUrl(url)) return BadRequest(new { detail = "Enter a valid HTTP or HTTPS server URL." });

        var key = payload.ApiKey?.Trim() ?? "";
        if (key.Length == 0 && !string.IsNullOrWhiteSpace(payload.InstanceId))
        {
            var stored = db.GetArrInstance(payload.InstanceId);
            // A secret can only be reattached to the exact service and destination it
            // was stored against. Never place it in an error or response.
            if (stored is not null
                && stored.ServiceType == service
                && string.Equals(stored.Url, url, StringComparison.OrdinalIgnoreCase))
                key = stored.ApiKey;
        }
        if (key.Length == 0) return Ok(new { ok = false, detail = "Enter the API key for this server." });

        var reason = service == "radarr"
            ? await radarr.ProbeAsync(url, key, ct)
            : await sonarr.ProbeAsync(url, key, ct);
        return Ok(new { ok = reason is null, detail = reason ?? $"{ServiceName(service)} is reachable." });
    }

    private static object Redacted(ArrInstance instance) => new
    {
        id = instance.Id,
        serviceType = instance.ServiceType,
        name = instance.Name,
        url = instance.Url,
        configured = instance.ApiKey.Length > 0,
        enabled = instance.Enabled,
        qualityLabel = instance.QualityLabel,
        priority = instance.Priority,
        tags = instance.Tags,
        createdAt = instance.CreatedAt,
        updatedAt = instance.UpdatedAt,
        lastSuccessfulSync = instance.LastSuccessfulSyncAt,
        health = instance.Health,
        healthDetail = instance.HealthDetail,
        unresolvedPathCount = instance.UnresolvedPathCount,
        unresolvedPathSample = instance.UnresolvedPathSample,
    };

    private static string? Validate(ArrInstancePayload payload, bool requireKey)
    {
        if (NormalizeService(payload.ServiceType).Length == 0)
            return "serviceType must be 'radarr' or 'sonarr'.";
        if (string.IsNullOrWhiteSpace(payload.Name)) return "Instance name cannot be empty.";
        if (payload.Name.Trim().Length > 120) return "Instance name cannot exceed 120 characters.";
        if (!ValidUrl(Database.NormalizeArrUrl(payload.Url))) return "Enter a valid HTTP or HTTPS server URL.";
        if (requireKey && string.IsNullOrWhiteSpace(payload.ApiKey)) return "API key cannot be empty.";
        if (payload.QualityLabel?.Trim().Length > 80) return "Quality label cannot exceed 80 characters.";
        if (payload.Tags is { Count: > 50 }) return "At most 50 tags may be stored.";
        return null;
    }

    private static bool ValidUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(uri.Host);
    private static string NormalizeService(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    { "radarr" => "radarr", "sonarr" => "sonarr", _ => "" };
    private static string ServiceName(string service) => service == "radarr" ? "Radarr" : "Sonarr";
}

public sealed record ArrInstancePayload(
    string? ServiceType,
    string? Name,
    string? Url,
    string? ApiKey,
    bool Enabled = true,
    string? QualityLabel = null,
    int Priority = 0,
    List<string>? Tags = null);

public sealed record ArrInstanceTestPayload(
    string? ServiceType,
    string? Url,
    string? ApiKey,
    string? InstanceId = null);
