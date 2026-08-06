using System.Text.RegularExpressions;

namespace Themearr.API.Services;

public sealed record DownloaderComponentStatus(bool Available, string Status, string? Version, string? Detail = null);

public sealed record DownloaderDiagnostics(
    bool Ready,
    bool Degraded,
    string Status,
    string Summary,
    DownloaderComponentStatus YtDlp,
    DownloaderComponentStatus Ffmpeg,
    DownloaderComponentStatus Ffprobe,
    DownloaderComponentStatus JavaScriptRuntime,
    YoutubeCookieStatus Cookies,
    PoTokenProviderStatus PoTokenProvider,
    string AudioQuality,
    int TimeoutSeconds,
    int ConcurrentDownloads,
    bool AudioQualityManagedByEnvironment,
    bool TimeoutManagedByEnvironment,
    bool ConcurrencyManagedByEnvironment);

public interface IDownloaderDiagnosticsService
{
    Task<DownloaderDiagnostics> CheckAsync(bool forceRefresh = false, CancellationToken ct = default);
}

public sealed class DownloaderDiagnosticsService(
    DownloaderConfiguration configuration,
    IYoutubeCookieStore cookies,
    IPoTokenProviderDiagnostics poTokens,
    IExternalProcessRunner processes,
    ILogger<DownloaderDiagnosticsService> log) : IDownloaderDiagnosticsService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DownloaderDiagnostics? _cached;
    private DateTime _expiresAt;

    public async Task<DownloaderDiagnostics> CheckAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _cached is not null && DateTime.UtcNow < _expiresAt)
                return _cached;

            _cached = await CheckCoreAsync(ct);
            _expiresAt = DateTime.UtcNow.Add(CacheDuration);
            return _cached;
        }
        finally { _lock.Release(); }
    }

    private async Task<DownloaderDiagnostics> CheckCoreAsync(CancellationToken ct)
    {
        var settings = configuration.GetSnapshot();
        var ytPath = ExecutableLocator.Resolve(settings.YtDlpPath, "yt-dlp");
        var ffmpegPath = ExecutableLocator.Resolve(settings.FfmpegPath, "ffmpeg");
        var ffprobePath = ResolveFfprobe(settings.FfmpegPath, ffmpegPath);
        var denoPath = ExecutableLocator.Resolve("deno", "deno");
        var probeRoot = Path.Combine(Path.GetTempPath(), $"themearr-downloader-check-{Guid.NewGuid():N}");

        DownloaderComponentStatus yt;
        DownloaderComponentStatus ffmpeg;
        DownloaderComponentStatus ffprobe;
        DownloaderComponentStatus deno;
        PoTokenProviderStatus poToken;
        var temporaryStorageReady = false;
        try
        {
            Directory.CreateDirectory(probeRoot);
            await File.WriteAllTextAsync(Path.Combine(probeRoot, "write-test"), "ok", ct);
            temporaryStorageReady = true;

            var checks = await Task.WhenAll(
                CheckExecutableAsync(ytPath, ["--version"], probeRoot, "yt-dlp", ct),
                CheckExecutableAsync(ffmpegPath, ["-version"], probeRoot, "FFmpeg", ct),
                CheckExecutableAsync(ffprobePath, ["-version"], probeRoot, "FFprobe", ct),
                CheckExecutableAsync(denoPath, ["--version"], probeRoot, "Deno", ct));
            yt = checks[0];
            ffmpeg = checks[1];
            ffprobe = checks[2];
            deno = checks[3];
            poToken = await poTokens.CheckAsync(settings, ytPath, probeRoot, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Local downloader temporary-directory check failed: {Reason}", LogSanitizer.Clean(ex.Message));
            yt = ytPath is null ? Missing("yt-dlp") : new(false, "error", null, "Version check could not run.");
            ffmpeg = ffmpegPath is null ? Missing("FFmpeg") : new(false, "error", null, "Version check could not run.");
            ffprobe = ffprobePath is null ? Missing("FFprobe") : new(false, "error", null, "Version check could not run.");
            deno = denoPath is null ? Missing("Deno") : new(false, "error", null, "Version check could not run.");
            poToken = new(settings.PoTokenMode.ToString().ToLowerInvariant(), "degraded", false, false, null,
                "PO-token diagnostics could not run.");
        }
        finally
        {
            try { if (Directory.Exists(probeRoot)) Directory.Delete(probeRoot, recursive: true); } catch { /* best effort */ }
        }

        var cookieStatus = cookies.Resolve().Status;
        var poRequiredUnavailable = settings.PoTokenMode == PoTokenMode.Required && poToken.Status != "ready";
        var fatal = settings.ValidationErrors.Count > 0 || !yt.Available || !ffmpeg.Available ||
                    !ffprobe.Available || !temporaryStorageReady || poRequiredUnavailable;
        var cookiesInvalid = cookieStatus.Configured && !cookieStatus.Valid;
        var poDegraded = poToken.Status == "degraded";
        var degraded = !fatal && (!deno.Available || cookiesInvalid || poDegraded);
        var status = fatal ? "unhealthy" : degraded ? "degraded" : "healthy";
        var summary = fatal
            ? settings.ValidationErrors.FirstOrDefault()
                ?? (!yt.Available ? "yt-dlp executable was not found or could not be started."
                    : !ffmpeg.Available ? "FFmpeg executable was not found or could not be started."
                    : !ffprobe.Available ? "FFprobe executable was not found or could not be started."
                    : poRequiredUnavailable ? "The required PO-token plugin or provider is unavailable."
                    : "The temporary download directory is not writable.")
            : cookiesInvalid
                ? cookieStatus.Detail ?? "The configured cookies file is invalid or unreadable."
                : poDegraded
                    ? poToken.Detail ?? "The PO-token provider is unavailable."
                : !deno.Available
                    ? "Local downloader is available, but Deno was not found; some YouTube videos may fail extraction."
                    : $"Local theme downloader ready. yt-dlp {yt.Version}, FFmpeg available.";

        return new DownloaderDiagnostics(
            !fatal, degraded, status, summary, yt, ffmpeg, ffprobe, deno, cookieStatus, poToken,
            settings.AudioQuality, settings.TimeoutSeconds, settings.ConcurrentDownloads,
            settings.AudioQualityManagedByEnvironment, settings.TimeoutManagedByEnvironment,
            settings.ConcurrencyManagedByEnvironment);
    }

    private async Task<DownloaderComponentStatus> CheckExecutableAsync(
        string? path, IReadOnlyList<string> arguments, string workingDirectory, string name, CancellationToken ct)
    {
        if (path is null) return Missing(name);
        try
        {
            var result = await processes.RunAsync(new ExternalProcessRequest(
                path, arguments, workingDirectory,
                DownloaderConfiguration.MinimalProcessEnvironment(workingDirectory),
                TimeSpan.FromSeconds(4)), ct);
            if (result.TimedOut) return new(false, "error", null, $"{name} version check timed out.");
            if (result.Cancelled) throw new OperationCanceledException(ct);
            if (result.ExitCode != 0) return new(false, "error", null, $"{name} version check failed.");

            var firstLine = (result.StandardOutput + "\n" + result.StandardError)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(ProcessOutputSanitizer.CleanLine)
                .FirstOrDefault(line => line.Length > 0);
            var version = name is "FFmpeg" or "FFprobe"
                ? Regex.Match(firstLine ?? "", @"ff(?:mpeg|probe) version\s+([^\s]+)", RegexOptions.IgnoreCase).Groups[1].Value
                : name == "Deno"
                    ? Regex.Match(firstLine ?? "", @"deno\s+([v\d][^\s]*)", RegexOptions.IgnoreCase).Groups[1].Value
                    : firstLine;
            return new(true, "available", string.IsNullOrWhiteSpace(version) ? "unknown" : version);
        }
        catch (ExternalProcessStartException)
        {
            return new(false, "missing", null, $"{name} executable could not be started.");
        }
    }

    private static DownloaderComponentStatus Missing(string name) =>
        new(false, "missing", null, $"{name} executable was not found.");

    private static string? ResolveFfprobe(string configuredFfmpeg, string? resolvedFfmpeg)
    {
        if (Directory.Exists(configuredFfmpeg))
            return ExecutableLocator.Resolve(configuredFfmpeg, "ffprobe");
        if (resolvedFfmpeg is not null)
        {
            var sibling = Path.Combine(Path.GetDirectoryName(resolvedFfmpeg)!,
                OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            var resolved = ExecutableLocator.Resolve(sibling, "ffprobe");
            if (resolved is not null) return resolved;
        }
        return ExecutableLocator.Resolve("ffprobe", "ffprobe");
    }
}

internal static class ProcessOutputSanitizer
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex Authorization = new(
        @"(?i)(authorization\s*[:=]\s*)(?:bearer\s+)?[^\s,;]+", RegexOptions.Compiled);
    private static readonly Regex YoutubeCredential = new(
        @"(?i)\b(po[_ -]?token|pot|sapisidhash|sapisid|__secure-3papisid)\b(\s*[:=+]\s*)[^\s,;]+",
        RegexOptions.Compiled);

    public static string CleanLine(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var clean = Ansi.Replace(value, "");
        clean = new string(clean.Where(c => !char.IsControl(c) || c == '\t').ToArray()).Trim();
        return clean.Length > 1000 ? clean[..1000] + "…" : clean;
    }

    public static string SafeErrorExcerpt(string raw, string temporaryDirectory, string? cookiesPath)
    {
        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(CleanLine)
            .Where(line => line.Length > 0)
            .TakeLast(8)
            .ToArray();
        var text = string.Join(" ", lines)
            .Replace(temporaryDirectory, "<temporary directory>", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(cookiesPath))
            text = text.Replace(cookiesPath, "<cookies file>", StringComparison.OrdinalIgnoreCase);
        text = Authorization.Replace(text, "$1<redacted>");
        text = YoutubeCredential.Replace(text, "$1$2<redacted>");
        return text.Length > 1500 ? text[..1500] + "…" : text;
    }
}

internal static class FileValidation
{
    public static bool IsRegularNonSymlink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return false;
            return info.LinkTarget is null;
        }
        catch { return false; }
    }
}
