using System.Text.Json;

namespace Themearr.API.Services;

public enum ThemeAudioFailureKind
{
    Configuration,
    Timeout,
    Unavailable,
    AuthenticationRequired,
    Extraction,
    Conversion,
    Oversized,
    UnexpectedProcessFailure,
}

public enum ThemeAudioFailureCode
{
    COOKIE_NOT_CONFIGURED,
    COOKIE_FILE_INVALID,
    COOKIE_FILE_MISSING,
    YOUTUBE_AUTHENTICATION_REQUIRED,
    YOUTUBE_BOT_CHECK,
    YOUTUBE_ACCOUNT_ACCESS_DENIED,
    YOUTUBE_PRIVATE_VIDEO,
    YOUTUBE_MEMBERS_ONLY,
    YOUTUBE_AGE_RESTRICTED,
    PO_TOKEN_PROVIDER_UNAVAILABLE,
    PO_TOKEN_REQUIRED,
    PO_TOKEN_PLUGIN_MISSING,
    YOUTUBE_RATE_LIMITED,
    YOUTUBE_VIDEO_UNAVAILABLE,
    YTDLP_EXTRACTION_FAILED,
    YTDLP_DOWNLOAD_FAILED,
    FFMPEG_CONVERSION_FAILED,
    DOWNLOAD_TIMEOUT,
}

public sealed class ThemeAudioDownloadException(
    ThemeAudioFailureKind kind, string message, Exception? inner = null,
    ThemeAudioFailureCode? code = null) : Exception(message, inner)
{
    public ThemeAudioFailureKind Kind { get; } = kind;
    public ThemeAudioFailureCode Code { get; } = code ?? kind switch
    {
        ThemeAudioFailureKind.Timeout => ThemeAudioFailureCode.DOWNLOAD_TIMEOUT,
        ThemeAudioFailureKind.Conversion => ThemeAudioFailureCode.FFMPEG_CONVERSION_FAILED,
        ThemeAudioFailureKind.Extraction => ThemeAudioFailureCode.YTDLP_EXTRACTION_FAILED,
        _ => ThemeAudioFailureCode.YTDLP_DOWNLOAD_FAILED,
    };
}

public sealed class YtDlpThemeAudioProvider(
    DownloaderConfiguration configuration,
    IYoutubeCookieStore cookies,
    IDownloaderDiagnosticsService diagnostics,
    IExternalProcessRunner processes,
    YtDlpConcurrencyGate concurrency,
    ILogger<YtDlpThemeAudioProvider> log) : IThemeAudioProvider
{
    private const string TitleMarker = "THEMEARR_TITLE:";
    private const string FileMarker = "THEMEARR_FILE:";
    private const string ProgressMarker = "THEMEARR_PROGRESS:";

    public Task<DownloaderDiagnostics> CheckConfigurationAsync(
        bool forceRefresh = false, CancellationToken ct = default) => diagnostics.CheckAsync(forceRefresh, ct);

    public async Task<string?> DownloadAsync(
        string videoId, string outputPath, Action<string> progress, CancellationToken ct = default)
    {
        if (!IsValidVideoId(videoId))
            throw new ArgumentException("Invalid YouTube video ID. Expected 11 letters, digits, underscores, or hyphens.", nameof(videoId));

        var readiness = await diagnostics.CheckAsync(false, ct);
        if (!readiness.Ready)
            throw new ThemeAudioDownloadException(ThemeAudioFailureKind.Configuration, readiness.Summary);

        var settings = configuration.GetSnapshot();
        var cookieResolution = cookies.Resolve();
        if (cookieResolution.Status.Configured && !cookieResolution.Status.Valid)
            log.LogWarning("The configured YouTube cookies file is invalid or unreadable and will not be passed to yt-dlp.");
        var ytPath = ExecutableLocator.Resolve(settings.YtDlpPath, "yt-dlp")
            ?? throw new ThemeAudioDownloadException(ThemeAudioFailureKind.Configuration,
                "yt-dlp executable was not found.");
        var ffmpegPath = ExecutableLocator.Resolve(settings.FfmpegPath, "ffmpeg")
            ?? throw new ThemeAudioDownloadException(ThemeAudioFailureKind.Configuration,
                "FFmpeg executable was not found.");
        var denoPath = ExecutableLocator.Resolve("deno", "deno");
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"themearr-ytdlp-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(temporaryDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                catch (PlatformNotSupportedException) { }
            }

            using var lease = await concurrency.AcquireAsync(ct);
            progress("[ThemeForge] Starting local YouTube audio download…");
            log.LogInformation("Starting local theme download for validated video {VideoId}", videoId);

            string? title = null;
            string? reportedFile = null;
            var poReady = readiness.PoTokenProvider.Status == "ready";
            var arguments = BuildArguments(videoId, temporaryDirectory, ffmpegPath, denoPath, settings,
                cookieResolution.ActivePath, poReady);
            var request = new ExternalProcessRequest(
                ytPath, arguments, temporaryDirectory,
                DownloaderConfiguration.MinimalProcessEnvironment(temporaryDirectory),
                TimeSpan.FromSeconds(settings.TimeoutSeconds),
                line =>
                {
                    if (TryParseMarker(line, TitleMarker, out var parsedTitle)) title = parsedTitle;
                    else if (TryParseMarker(line, FileMarker, out var parsedFile)) reportedFile = parsedFile;
                    else if (line.StartsWith(ProgressMarker, StringComparison.Ordinal))
                        progress("[yt-dlp] " + ProcessOutputSanitizer.CleanLine(line[ProgressMarker.Length..]));
                });

            ExternalProcessResult result;
            try { result = await processes.RunAsync(request, ct); }
            catch (ExternalProcessStartException ex)
            {
                throw new ThemeAudioDownloadException(ThemeAudioFailureKind.Configuration,
                    "yt-dlp could not be started. Check YTDLP_PATH and executable permissions.", ex);
            }

            if (result.Cancelled) throw new OperationCanceledException(ct);
            if (result.TimedOut)
                throw new ThemeAudioDownloadException(ThemeAudioFailureKind.Timeout,
                    $"Local YouTube download timed out after {settings.TimeoutSeconds} seconds and was terminated.",
                    code: ThemeAudioFailureCode.DOWNLOAD_TIMEOUT);
            if (result.ExitCode != 0)
                throw ClassifyFailure(result.StandardError, temporaryDirectory,
                    cookieResolution, readiness.PoTokenProvider);

            progress("[ThemeForge] Validating converted MP3…");
            var finalFile = ValidateOutput(videoId, temporaryDirectory, reportedFile);
            var info = new FileInfo(finalFile);
            if (info.Length > StreamLimits.MaxThemeBytes)
                throw new ThemeAudioDownloadException(ThemeAudioFailureKind.Oversized,
                    $"Converted theme exceeded the {StreamLimits.MaxThemeBytes / (1024 * 1024)} MB limit.");

            await using var source = new FileStream(finalFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await ThemeFiles.WriteAtomicAsync(source, outputPath, StreamLimits.MaxThemeBytes, ct);
            return title;
        }
        finally
        {
            try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true); }
            catch (Exception ex) { log.LogWarning("Could not fully remove a local downloader temporary directory: {Reason}", LogSanitizer.Clean(ex.Message)); }
        }
    }

    internal static bool IsValidVideoId(string? videoId)
    {
        if (videoId is null || videoId.Length != 11) return false;
        return videoId.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
    }

    internal static IReadOnlyList<string> BuildArguments(
        string videoId, string temporaryDirectory, string ffmpegPath, string? denoPath,
        DownloaderConfigurationSnapshot settings, string? cookiesPath, bool poProviderReady)
    {
        var arguments = new List<string>
        {
            "--ignore-config",
            "--no-plugin-dirs",
            "--no-remote-components",
            "--no-playlist",
            "--no-overwrites",
            "--extract-audio",
            "--audio-format", "mp3",
            "--audio-quality", settings.AudioQuality,
            "--ffmpeg-location", ffmpegPath,
            "--output", Path.Combine(temporaryDirectory, "%(id)s.%(ext)s"),
            "--newline",
            "--progress",
            "--progress-template", $"download:{ProgressMarker}%(progress._percent_str)s downloaded, ETA %(progress.eta)s",
            "--print", $"before_dl:{TitleMarker}%(title)j",
            "--print", $"after_move:{FileMarker}%(filepath)j",
        };
        if (denoPath is not null)
        {
            arguments.Add("--js-runtimes");
            arguments.Add("deno:" + denoPath);
        }
        if (cookiesPath is { } activeCookies && FileValidation.IsRegularNonSymlink(activeCookies))
        {
            arguments.Add("--cookies");
            arguments.Add(activeCookies);
        }
        if (settings.PoTokenMode != PoTokenMode.Disabled && poProviderReady &&
            settings.PoTokenProviderUrl is not null)
        {
            arguments.Add("--plugin-dirs");
            arguments.Add(settings.PoTokenPluginDirectory);
            arguments.Add("--extractor-args");
            arguments.Add("youtubepot-bgutilhttp:base_url=" +
                          settings.PoTokenProviderUrl.ToString().TrimEnd('/'));
            arguments.Add("--extractor-args");
            arguments.Add("youtube:player_client=default,mweb");
        }
        arguments.Add("https://www.youtube.com/watch?v=" + videoId);
        return arguments;
    }

    private static bool TryParseMarker(string line, string marker, out string? value)
    {
        value = null;
        if (!line.StartsWith(marker, StringComparison.Ordinal)) return false;
        try { value = JsonSerializer.Deserialize<string>(line[marker.Length..]); }
        catch (JsonException) { }
        return true;
    }

    private static string ValidateOutput(string videoId, string temporaryDirectory, string? reportedFile)
    {
        if (string.IsNullOrWhiteSpace(reportedFile))
            throw new ThemeAudioDownloadException(ThemeAudioFailureKind.Extraction,
                "yt-dlp completed without reporting the converted MP3 path.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(temporaryDirectory));
        var normalized = Path.GetFullPath(Path.IsPathFullyQualified(reportedFile)
            ? reportedFile
            : Path.Combine(root, reportedFile));
        var relative = Path.GetRelativePath(root, normalized);
        if (Path.IsPathFullyQualified(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ThemeAudioDownloadException(ThemeAudioFailureKind.Extraction,
                "yt-dlp reported an output path outside its temporary directory.");

        var mp3Files = Directory.EnumerateFiles(root, "*.mp3", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath).ToArray();
        if (mp3Files.Length != 1)
            throw new ThemeAudioDownloadException(ThemeAudioFailureKind.Extraction,
                mp3Files.Length == 0 ? "yt-dlp did not produce the expected MP3." : "yt-dlp produced more than one MP3 unexpectedly.");

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var expected = Path.Combine(root, videoId + ".mp3");
        if (!string.Equals(normalized, mp3Files[0], comparison) || !string.Equals(normalized, expected, comparison))
            throw new ThemeAudioDownloadException(ThemeAudioFailureKind.Extraction,
                "yt-dlp reported an unexpected MP3 filename.");
        if (!FileValidation.IsRegularNonSymlink(normalized))
            throw new ThemeAudioDownloadException(ThemeAudioFailureKind.Extraction,
                "yt-dlp output was not a regular file or was a symbolic link.");
        if (new FileInfo(normalized).Length == 0)
            throw new ThemeAudioDownloadException(ThemeAudioFailureKind.Extraction,
                "yt-dlp produced an empty MP3.");
        return normalized;
    }

    internal static ThemeAudioDownloadException ClassifyFailure(
        string stderr, string temporaryDirectory, YoutubeCookieResolution cookies,
        PoTokenProviderStatus poToken)
    {
        var detail = ProcessOutputSanitizer.SafeErrorExcerpt(stderr, temporaryDirectory, cookies.ActivePath);
        var lower = detail.ToLowerInvariant();
        if (lower.Contains("too many requests") || lower.Contains("http error 429"))
            return Failure(ThemeAudioFailureKind.Unavailable, ThemeAudioFailureCode.YOUTUBE_RATE_LIMITED,
                "YouTube rate-limited this request. Keep concurrency low and retry later.", detail);
        if (lower.Contains("members-only") || lower.Contains("members only") || lower.Contains("join this channel"))
            return Failure(ThemeAudioFailureKind.AuthenticationRequired, ThemeAudioFailureCode.YOUTUBE_MEMBERS_ONLY,
                "This is a members-only video. The configured YouTube account must have access.", detail);
        if (lower.Contains("private video"))
            return Failure(ThemeAudioFailureKind.Unavailable, ThemeAudioFailureCode.YOUTUBE_PRIVATE_VIDEO,
                "This is a private YouTube video and the configured account does not have access.", detail);
        if (lower.Contains("age-restricted") || lower.Contains("age restricted") || lower.Contains("confirm your age"))
            return Failure(ThemeAudioFailureKind.AuthenticationRequired, ThemeAudioFailureCode.YOUTUBE_AGE_RESTRICTED,
                cookies.Status.Valid
                    ? "The configured YouTube account could not access this age-restricted video."
                    : "This video is age-restricted. Upload a valid YouTube cookies.txt file under Settings → Local YouTube Downloader.", detail);
        if (lower.Contains("confirm you're not a bot") || lower.Contains("confirm you’re not a bot") ||
            lower.Contains("bot check"))
            return Failure(ThemeAudioFailureKind.AuthenticationRequired, ThemeAudioFailureCode.YOUTUBE_BOT_CHECK,
                cookies.Status.Valid
                    ? "YouTube rejected the configured cookie session during bot verification. Export a fresh cookies.txt file and replace the current upload."
                    : "YouTube requested authentication or bot verification. Upload a valid YouTube cookies.txt file under Settings → Local YouTube Downloader.", detail);
        if (lower.Contains("po token provider") && (lower.Contains("unavailable") || lower.Contains("error reaching")))
            return Failure(ThemeAudioFailureKind.Configuration, ThemeAudioFailureCode.PO_TOKEN_PROVIDER_UNAVAILABLE,
                "The PO-token provider is unavailable. Check the themearr-pot-provider container and retry.", detail);
        if (lower.Contains("po token") && (lower.Contains("required") || lower.Contains("missing") ||
            lower.Contains("not provided")))
            return Failure(ThemeAudioFailureKind.Extraction, ThemeAudioFailureCode.PO_TOKEN_REQUIRED,
                "YouTube requires additional playback verification for this video. Enable the configured PO-token provider and retry.", detail);
        if (lower.Contains("plugin") && lower.Contains("bgutil") && (lower.Contains("missing") || lower.Contains("not found")))
            return Failure(ThemeAudioFailureKind.Configuration, ThemeAudioFailureCode.PO_TOKEN_PLUGIN_MISSING,
                "The yt-dlp PO-token plugin is missing. Rebuild or update to the supported ThemeForge image.", detail);
        if (lower.Contains("account") && (lower.Contains("not permitted") || lower.Contains("does not have access") ||
            lower.Contains("not available")))
            return Failure(ThemeAudioFailureKind.AuthenticationRequired, ThemeAudioFailureCode.YOUTUBE_ACCOUNT_ACCESS_DENIED,
                "The configured YouTube account does not have access to this video.", detail);
        if (lower.Contains("cookies") && (lower.Contains("expired") || lower.Contains("invalid") || lower.Contains("not valid")))
            return Failure(ThemeAudioFailureKind.AuthenticationRequired, ThemeAudioFailureCode.COOKIE_FILE_INVALID,
                "YouTube rejected the configured cookie session. Export a fresh cookies.txt file and replace the current upload.", detail);
        if (lower.Contains("sign in") || lower.Contains("login") || lower.Contains("authentication"))
            return Failure(ThemeAudioFailureKind.AuthenticationRequired, ThemeAudioFailureCode.YOUTUBE_AUTHENTICATION_REQUIRED,
                cookies.Status.Valid
                    ? "YouTube rejected the configured cookie session. Export a fresh cookies.txt file and replace the current upload."
                    : "YouTube requires an authenticated session. Upload a valid YouTube cookies.txt file under Settings → Local YouTube Downloader.", detail);
        if (lower.Contains("video unavailable") || lower.Contains("not available in your country") ||
            lower.Contains("removed") || lower.Contains("copyright"))
            return Failure(ThemeAudioFailureKind.Unavailable, ThemeAudioFailureCode.YOUTUBE_VIDEO_UNAVAILABLE,
                "The selected YouTube video is unavailable.", detail);
        if (lower.Contains("ffmpeg") || lower.Contains("postprocess") || lower.Contains("conversion"))
            return Failure(ThemeAudioFailureKind.Conversion, ThemeAudioFailureCode.FFMPEG_CONVERSION_FAILED,
                "FFmpeg could not convert the downloaded audio.", detail);
        if (lower.Contains("extract") || lower.Contains("no video formats") || lower.Contains("requested format"))
            return Failure(ThemeAudioFailureKind.Extraction, ThemeAudioFailureCode.YTDLP_EXTRACTION_FAILED,
                "yt-dlp could not extract audio from this video.", detail);
        return Failure(ThemeAudioFailureKind.UnexpectedProcessFailure, ThemeAudioFailureCode.YTDLP_DOWNLOAD_FAILED,
            "yt-dlp exited unexpectedly.", detail);
    }

    private static ThemeAudioDownloadException Failure(
        ThemeAudioFailureKind kind, ThemeAudioFailureCode code, string message, string detail) =>
        new(kind, $"{message} {detail}".Trim(), code: code);
}
