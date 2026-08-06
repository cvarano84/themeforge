using Themearr.API.Data;

namespace Themearr.API.Services;

public enum PoTokenMode
{
    Auto,
    Disabled,
    Required,
}

public sealed record DownloaderConfigurationSnapshot(
    string YtDlpPath,
    string FfmpegPath,
    PoTokenMode PoTokenMode,
    Uri? PoTokenProviderUrl,
    string PoTokenPluginDirectory,
    string AudioQuality,
    int TimeoutSeconds,
    int ConcurrentDownloads,
    bool AudioQualityManagedByEnvironment,
    bool TimeoutManagedByEnvironment,
    bool ConcurrencyManagedByEnvironment,
    IReadOnlyList<string> ValidationErrors);

public sealed class DownloaderConfiguration(Database db)
{
    public const string DefaultAudioQuality = "192K";
    public const int DefaultTimeoutSeconds = 300;
    public const int DefaultConcurrentDownloads = 1;
    public const int MinimumTimeoutSeconds = 30;
    public const int MaximumTimeoutSeconds = 1800;
    public const int MinimumConcurrentDownloads = 1;
    public const int MaximumConcurrentDownloads = 3;

    public static readonly IReadOnlySet<string> SupportedAudioQualities =
        new HashSet<string>(["128K", "192K", "256K", "320K"], StringComparer.Ordinal);

    public DownloaderConfigurationSnapshot GetSnapshot()
    {
        var errors = new List<string>();
        var qualityEnv = Environment.GetEnvironmentVariable("YTDLP_AUDIO_QUALITY");
        var timeoutEnv = Environment.GetEnvironmentVariable("YTDLP_DOWNLOAD_TIMEOUT_SECONDS");
        var concurrencyEnv = Environment.GetEnvironmentVariable("YTDLP_CONCURRENT_DOWNLOADS");
        var poMode = ReadPoTokenMode(Environment.GetEnvironmentVariable("YTDLP_PO_TOKEN_MODE"), errors);
        var poUrl = ReadPoTokenProviderUrl(Environment.GetEnvironmentVariable("YTDLP_PO_TOKEN_PROVIDER_URL"), errors);

        var quality = ReadQuality(qualityEnv, db.GetSetting("ytdlp_audio_quality", DefaultAudioQuality), errors);
        var timeout = ReadBoundedInt(timeoutEnv, db.GetSetting("ytdlp_download_timeout_seconds", DefaultTimeoutSeconds.ToString()),
            DefaultTimeoutSeconds, MinimumTimeoutSeconds, MaximumTimeoutSeconds,
            "YTDLP_DOWNLOAD_TIMEOUT_SECONDS", errors);
        var concurrency = ReadBoundedInt(concurrencyEnv, db.GetSetting("ytdlp_concurrent_downloads", DefaultConcurrentDownloads.ToString()),
            DefaultConcurrentDownloads, MinimumConcurrentDownloads, MaximumConcurrentDownloads,
            "YTDLP_CONCURRENT_DOWNLOADS", errors);

        return new DownloaderConfigurationSnapshot(
            Environment.GetEnvironmentVariable("YTDLP_PATH")?.Trim() is { Length: > 0 } yt ? yt : "yt-dlp",
            Environment.GetEnvironmentVariable("FFMPEG_PATH")?.Trim() is { Length: > 0 } ff ? ff : "ffmpeg",
            poMode,
            poUrl,
            Environment.GetEnvironmentVariable("YTDLP_PO_TOKEN_PLUGIN_DIR")?.Trim() is { Length: > 0 } pluginDir
                ? pluginDir : "/usr/local/share/yt-dlp-plugins",
            quality, timeout, concurrency,
            qualityEnv is not null, timeoutEnv is not null, concurrencyEnv is not null, errors);
    }

    public DownloaderConfigurationSnapshot Save(string audioQuality, int timeoutSeconds, int concurrentDownloads)
    {
        if (!SupportedAudioQualities.Contains(audioQuality))
            throw new ArgumentException("Audio quality must be one of 128K, 192K, 256K, or 320K.");
        if (timeoutSeconds is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds)
            throw new ArgumentException($"Download timeout must be between {MinimumTimeoutSeconds} and {MaximumTimeoutSeconds} seconds.");
        if (concurrentDownloads is < MinimumConcurrentDownloads or > MaximumConcurrentDownloads)
            throw new ArgumentException($"Concurrent downloads must be between {MinimumConcurrentDownloads} and {MaximumConcurrentDownloads}.");

        db.SetSetting("ytdlp_audio_quality", audioQuality);
        db.SetSetting("ytdlp_download_timeout_seconds", timeoutSeconds.ToString());
        db.SetSetting("ytdlp_concurrent_downloads", concurrentDownloads.ToString());
        return GetSnapshot();
    }

    public static IReadOnlyDictionary<string, string> MinimalProcessEnvironment(string temporaryDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HOME"] = temporaryDirectory,
            ["TMP"] = temporaryDirectory,
            ["TEMP"] = temporaryDirectory,
            ["TMPDIR"] = temporaryDirectory,
            ["XDG_CACHE_HOME"] = Path.Combine(temporaryDirectory, ".cache"),
            ["NO_COLOR"] = "1",
            ["LC_ALL"] = "C.UTF-8",
            ["LANG"] = "C.UTF-8",
        };

        foreach (var name in new[] { "SystemRoot", "WINDIR", "SSL_CERT_FILE", "SSL_CERT_DIR" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                result[name] = value;
        return result;
    }

    private static string ReadQuality(string? environment, string database, List<string> errors)
    {
        var value = environment ?? database;
        if (SupportedAudioQualities.Contains(value)) return value;
        errors.Add(environment is not null
            ? "YTDLP_AUDIO_QUALITY must be one of 128K, 192K, 256K, or 320K."
            : "The saved audio quality is unsupported.");
        return DefaultAudioQuality;
    }

    private static int ReadBoundedInt(string? environment, string database, int fallback,
        int minimum, int maximum, string name, List<string> errors)
    {
        var value = environment ?? database;
        if (int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum)
            return parsed;
        errors.Add(environment is not null
            ? $"{name} must be between {minimum} and {maximum}."
            : $"The saved {name.ToLowerInvariant()} value is invalid.");
        return fallback;
    }

    private static PoTokenMode ReadPoTokenMode(string? value, List<string> errors)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "auto": return PoTokenMode.Auto;
            case "disabled": return PoTokenMode.Disabled;
            case "required": return PoTokenMode.Required;
            default:
                errors.Add("YTDLP_PO_TOKEN_MODE must be auto, disabled, or required.");
                return PoTokenMode.Auto;
        }
    }

    private static Uri? ReadPoTokenProviderUrl(string? value, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            errors.Add("YTDLP_PO_TOKEN_PROVIDER_URL must be an absolute HTTP(S) URL without credentials, query parameters, or fragments.");
            return null;
        }
        return uri;
    }
}

public static class ExecutableLocator
{
    public static string? Resolve(string configured, string executableName)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        var value = configured.Trim();
        if (Directory.Exists(value))
            return ResolveCandidate(Path.Combine(value, executableName));

        if (Path.IsPathFullyQualified(value) || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
            return ResolveCandidate(Path.GetFullPath(value));

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = ResolveCandidate(Path.Combine(directory, value));
            if (match is not null) return match;
        }
        return null;
    }

    private static string? ResolveCandidate(string candidate)
    {
        var candidates = OperatingSystem.IsWindows() && Path.GetExtension(candidate).Length == 0
            ? new[] { candidate + ".exe", candidate }
            : new[] { candidate };

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    var mode = File.GetUnixFileMode(path);
                    if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                        continue;
                }
                catch (PlatformNotSupportedException) { }
            }
            return Path.GetFullPath(path);
        }
        return null;
    }
}
