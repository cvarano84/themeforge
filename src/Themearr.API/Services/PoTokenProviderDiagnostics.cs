using System.Text.Json;

namespace Themearr.API.Services;

public sealed record PoTokenProviderStatus(
    string Mode,
    string Status,
    bool PluginDetected,
    bool ProviderReachable,
    string? Version,
    string? Detail = null);

public interface IPoTokenProviderDiagnostics
{
    Task<PoTokenProviderStatus> CheckAsync(
        DownloaderConfigurationSnapshot settings,
        string? ytDlpPath,
        string workingDirectory,
        CancellationToken ct = default);
}

public sealed class PoTokenProviderDiagnostics(
    IExternalProcessRunner processes,
    IHttpClientFactory clients) : IPoTokenProviderDiagnostics
{
    public const string ClientName = "po-token-provider-health";

    public async Task<PoTokenProviderStatus> CheckAsync(
        DownloaderConfigurationSnapshot settings,
        string? ytDlpPath,
        string workingDirectory,
        CancellationToken ct = default)
    {
        var mode = settings.PoTokenMode.ToString().ToLowerInvariant();
        if (settings.PoTokenMode == PoTokenMode.Disabled)
            return new(mode, "disabled", false, false, null,
                "Automatic PO-token support is disabled.");

        var pluginDetected = await DetectPluginAsync(
            ytDlpPath, settings.PoTokenPluginDirectory, workingDirectory, ct);
        if (settings.PoTokenProviderUrl is null)
            return new(mode,
                settings.PoTokenMode == PoTokenMode.Required ? "requiredUnavailable" : "notConfigured",
                pluginDetected, false, null,
                "The PO-token provider URL is not configured.");

        var reachable = false;
        string? version = null;
        string? detail = null;
        try
        {
            var ping = new Uri(settings.PoTokenProviderUrl.ToString().TrimEnd('/') + "/ping");
            using var response = await clients.CreateClient(ClientName).GetAsync(
                ping, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var limited = await ReadLimitedAsync(stream, 16 * 1024, ct);
                if (limited is not null)
                {
                    using var json = JsonDocument.Parse(limited);
                    if (json.RootElement.TryGetProperty("version", out var value) &&
                        value.ValueKind == JsonValueKind.String)
                        version = SanitizeVersion(value.GetString());
                    reachable = true;
                }
                else detail = "The PO-token provider health response was too large.";
            }
            else detail = "The PO-token provider health check returned an error.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            detail = ex is TaskCanceledException
                ? "The PO-token provider health check timed out."
                : "The PO-token provider is unreachable or returned an invalid health response.";
        }

        var ready = pluginDetected && reachable;
        return new(mode,
            ready ? "ready" : settings.PoTokenMode == PoTokenMode.Required ? "requiredUnavailable" : "degraded",
            pluginDetected, reachable, version,
            ready ? "Automatically supplies short-lived YouTube playback tokens when required."
                : !pluginDetected ? "The pinned yt-dlp PO-token plugin was not detected."
                : detail);
    }

    private async Task<bool> DetectPluginAsync(
        string? ytDlpPath, string pluginDirectory, string workingDirectory, CancellationToken ct)
    {
        if (ytDlpPath is null || !HasPinnedPluginArtifact(pluginDirectory)) return false;
        try
        {
            var result = await processes.RunAsync(new ExternalProcessRequest(
                ytDlpPath,
                ["--ignore-config", "--no-plugin-dirs", "--plugin-dirs", pluginDirectory,
                    "--verbose", "--list-extractors"],
                workingDirectory,
                DownloaderConfiguration.MinimalProcessEnvironment(workingDirectory),
                TimeSpan.FromSeconds(4)), ct);
            var output = result.StandardOutput + "\n" + result.StandardError;
            return result.ExitCode == 0 && !result.TimedOut && !result.Cancelled &&
                   !output.Contains("Traceback", StringComparison.OrdinalIgnoreCase) &&
                   !output.Contains("ImportError", StringComparison.OrdinalIgnoreCase);
        }
        catch (ExternalProcessStartException) { return false; }
    }

    internal static bool HasPinnedPluginArtifact(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Take(1000)
                .Any(path =>
                {
                    var name = Path.GetFileName(path);
                    return name.Contains("bgutil", StringComparison.OrdinalIgnoreCase) &&
                           (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("getpot_bgutil", StringComparison.OrdinalIgnoreCase));
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string? SanitizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sanitized = new string(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        return sanitized.Length is > 0 and <= 64 ? sanitized : null;
    }

    private static async Task<byte[]?> ReadLimitedAsync(Stream stream, int maximumBytes, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (output.Length <= maximumBytes)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) return output.ToArray();
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return null;
    }
}
