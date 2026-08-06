using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

[CollectionDefinition("Downloader environment", DisableParallelization = true)]
public sealed class DownloaderEnvironmentCollection;

[Collection("Downloader environment")]
public sealed class YtDlpThemeAudioProviderTests : IDisposable
{
    private const string VideoId = "abc12345678";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "themearr-ytdlp-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _oldEnvironment = new();
    private readonly DownloaderConfiguration _configuration;
    private readonly IYoutubeCookieStore _cookies;

    public YtDlpThemeAudioProviderTests()
    {
        Directory.CreateDirectory(_root);
        var db = new Database(Path.Combine(_root, "test.db"));
        db.Init();
        _configuration = new DownloaderConfiguration(db);
        _cookies = new YoutubeCookieStore(new ApplicationDataDirectory(Path.Combine(_root, "test.db")),
            NullLogger<YoutubeCookieStore>.Instance);
        SetEnvironment("YTDLP_PATH", FakeExecutable("yt-dlp"));
        SetEnvironment("FFMPEG_PATH", FakeExecutable("ffmpeg"));
        SetEnvironment("YTDLP_AUDIO_QUALITY", null);
        SetEnvironment("YTDLP_DOWNLOAD_TIMEOUT_SECONDS", null);
        SetEnvironment("YTDLP_CONCURRENT_DOWNLOADS", null);
        SetEnvironment("YTDLP_COOKIES_FILE", null);
        SetEnvironment("YTDLP_PO_TOKEN_MODE", "disabled");
        SetEnvironment("YTDLP_PO_TOKEN_PROVIDER_URL", null);
    }

    [Theory]
    [InlineData("abc12345678")]
    [InlineData("A_-09bcDEfg")]
    public void Accepts_conservative_eleven_character_ids(string value) =>
        Assert.True(YtDlpThemeAudioProvider.IsValidVideoId(value));

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("abc123456789")]
    [InlineData("abc1234567/")]
    [InlineData("abc1234567?")]
    [InlineData("ábč12345678")]
    public async Task Rejects_invalid_ids_before_process_execution(string value)
    {
        var runner = new FakeRunner(_ => throw new InvalidOperationException("must not run"));
        var provider = CreateProvider(runner);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DownloadAsync(value, OutputPath(), _ => { }));
        Assert.Null(runner.Request);
    }

    [Fact]
    public async Task Successful_download_uses_safe_arguments_parses_title_and_atomically_replaces_theme()
    {
        var destination = OutputPath();
        await File.WriteAllBytesAsync(destination, [1, 2, 3]);
        var runner = SuccessfulRunner([0x49, 0x44, 0x33, 4, 5, 6]);
        var provider = CreateProvider(runner);

        var title = await provider.DownloadAsync(VideoId, destination, _ => { });

        Assert.Equal("Example Theme", title);
        Assert.Equal(new byte[] { 0x49, 0x44, 0x33, 4, 5, 6 }, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".part"));
        Assert.NotNull(runner.Request);
        Assert.Equal("https://www.youtube.com/watch?v=" + VideoId, runner.Request!.Arguments[^1]);
        Assert.Contains("--ignore-config", runner.Request.Arguments);
        Assert.Contains("--no-remote-components", runner.Request.Arguments);
        Assert.DoesNotContain(runner.Request.Arguments, a => a.Contains(';') || a.Contains("&&"));
        Assert.False(Directory.Exists(runner.Request.WorkingDirectory));

        var startInfo = ExternalProcessRunner.CreateStartInfo(runner.Request);
        Assert.False(startInfo.UseShellExecute);
        Assert.Empty(startInfo.Arguments);
        Assert.Equal(runner.Request.Arguments, startInfo.ArgumentList.ToArray());
    }

    [Fact]
    public async Task Includes_cookies_only_for_an_existing_regular_non_symlink_file()
    {
        var cookie = Path.Combine(_root, "cookies.txt");
        await File.WriteAllTextAsync(cookie, CookieFile("secret-cookie-value"));
        SetEnvironment("YTDLP_COOKIES_FILE", cookie);
        var runner = SuccessfulRunner([1]);

        await CreateProvider(runner).DownloadAsync(VideoId, OutputPath(), _ => { });

        var index = runner.Request!.Arguments.ToList().IndexOf("--cookies");
        Assert.True(index >= 0);
        Assert.Equal(cookie, runner.Request.Arguments[index + 1]);
        Assert.DoesNotContain("secret-cookie-value", string.Join(' ', runner.Request.Arguments));

        SetEnvironment("YTDLP_COOKIES_FILE", Path.Combine(_root, "missing.txt"));
        var missingRunner = SuccessfulRunner([2]);
        await CreateProvider(missingRunner).DownloadAsync(VideoId, OutputPath("other"), _ => { });
        Assert.DoesNotContain("--cookies", missingRunner.Request!.Arguments);
    }

    [Fact]
    public async Task Rejects_empty_missing_oversized_and_outside_outputs_and_cleans_temp_directory()
    {
        foreach (var scenario in new[] { "empty", "missing", "oversized", "outside" })
        {
            var runner = new FakeRunner(request =>
            {
                var output = Path.Combine(request.WorkingDirectory, VideoId + ".mp3");
                if (scenario == "empty") File.WriteAllBytes(output, []);
                if (scenario == "oversized") using (var file = File.Create(output)) file.SetLength(StreamLimits.MaxThemeBytes + 1);
                if (scenario == "outside") File.WriteAllBytes(output, [1]);
                request.OnStandardOutput?.Invoke("THEMEARR_TITLE:\"Title\"");
                request.OnStandardOutput?.Invoke("THEMEARR_FILE:" +
                    System.Text.Json.JsonSerializer.Serialize(scenario == "outside" ? Path.Combine(_root, "outside.mp3") : output));
                return new(0, "", "", false, false);
            });

            var ex = await Assert.ThrowsAsync<ThemeAudioDownloadException>(() =>
                CreateProvider(runner).DownloadAsync(VideoId, OutputPath(scenario), _ => { }));
            Assert.Contains(ex.Kind, new[] { ThemeAudioFailureKind.Extraction, ThemeAudioFailureKind.Oversized });
            Assert.False(Directory.Exists(runner.Request!.WorkingDirectory));
            Assert.False(File.Exists(OutputPath(scenario) + ".part"));
        }
    }

    [Fact]
    public async Task Classifies_authentication_failure_without_exposing_cookie_path()
    {
        var cookie = Path.Combine(_root, "private-cookies.txt");
        await File.WriteAllTextAsync(cookie, CookieFile("contents-must-not-appear"));
        SetEnvironment("YTDLP_COOKIES_FILE", cookie);
        var runner = new FakeRunner(_ => new(1, "", $"ERROR: Sign in required; cookies at {cookie}", false, false));

        var ex = await Assert.ThrowsAsync<ThemeAudioDownloadException>(() =>
            CreateProvider(runner).DownloadAsync(VideoId, OutputPath(), _ => { }));

        Assert.Equal(ThemeAudioFailureKind.AuthenticationRequired, ex.Kind);
        Assert.Contains("fresh cookies.txt", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(cookie, ex.Message);
        Assert.DoesNotContain("contents-must-not-appear", ex.Message);
        Assert.False(Directory.Exists(runner.Request!.WorkingDirectory));
    }

    [Fact]
    public async Task Rejects_symbolic_link_output_when_supported()
    {
        var target = Path.Combine(_root, "real.mp3");
        await File.WriteAllBytesAsync(target, [1, 2, 3]);
        var runner = new FakeRunner(request =>
        {
            var output = Path.Combine(request.WorkingDirectory, VideoId + ".mp3");
            try { File.CreateSymbolicLink(output, target); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return new(1, "", "symbolic links unsupported by test host", false, false);
            }
            request.OnStandardOutput?.Invoke("THEMEARR_FILE:" + System.Text.Json.JsonSerializer.Serialize(output));
            return new(0, "", "", false, false);
        });

        var ex = await Assert.ThrowsAsync<ThemeAudioDownloadException>(() =>
            CreateProvider(runner).DownloadAsync(VideoId, OutputPath("symlink"), _ => { }));
        if (ex.Kind == ThemeAudioFailureKind.UnexpectedProcessFailure) return;
        Assert.Equal(ThemeAudioFailureKind.Extraction, ex.Kind);
        Assert.Contains("symbolic link", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reports_timeout_and_cancellation_and_cleans_temporary_directories()
    {
        var timedOut = new FakeRunner(_ => new(null, "", "", true, false));
        var timeout = await Assert.ThrowsAsync<ThemeAudioDownloadException>(() =>
            CreateProvider(timedOut).DownloadAsync(VideoId, OutputPath("timeout"), _ => { }));
        Assert.Equal(ThemeAudioFailureKind.Timeout, timeout.Kind);
        Assert.False(Directory.Exists(timedOut.Request!.WorkingDirectory));

        var cancelled = new FakeRunner(_ => new(null, "", "", false, true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateProvider(cancelled).DownloadAsync(VideoId, OutputPath("cancelled"), _ => { }, new CancellationToken(true)));
        Assert.False(Directory.Exists(cancelled.Request!.WorkingDirectory));
    }

    [Fact]
    public void Configuration_defaults_and_strict_bounds_are_enforced()
    {
        var snapshot = _configuration.GetSnapshot();
        Assert.Equal("192K", snapshot.AudioQuality);
        Assert.Equal(300, snapshot.TimeoutSeconds);
        Assert.Equal(1, snapshot.ConcurrentDownloads);
        Assert.Throws<ArgumentException>(() => _configuration.Save("96K", 300, 1));
        Assert.Throws<ArgumentException>(() => _configuration.Save("192K", 29, 1));
        Assert.Throws<ArgumentException>(() => _configuration.Save("192K", 1801, 1));
        Assert.Throws<ArgumentException>(() => _configuration.Save("192K", 300, 0));
        Assert.Throws<ArgumentException>(() => _configuration.Save("192K", 300, 4));
        Assert.Equal("320K", _configuration.Save("320K", 1800, 3).AudioQuality);
    }

    [Fact]
    public void Environment_overrides_database_and_invalid_values_are_configuration_errors()
    {
        _configuration.Save("128K", 60, 2);
        SetEnvironment("YTDLP_AUDIO_QUALITY", "320K");
        SetEnvironment("YTDLP_DOWNLOAD_TIMEOUT_SECONDS", "600");
        SetEnvironment("YTDLP_CONCURRENT_DOWNLOADS", "3");
        var valid = _configuration.GetSnapshot();
        Assert.Equal("320K", valid.AudioQuality);
        Assert.Equal(600, valid.TimeoutSeconds);
        Assert.Equal(3, valid.ConcurrentDownloads);
        Assert.True(valid.AudioQualityManagedByEnvironment);
        Assert.Empty(valid.ValidationErrors);

        SetEnvironment("YTDLP_AUDIO_QUALITY", "best");
        SetEnvironment("YTDLP_DOWNLOAD_TIMEOUT_SECONDS", "10");
        SetEnvironment("YTDLP_CONCURRENT_DOWNLOADS", "99");
        var invalid = _configuration.GetSnapshot();
        Assert.Equal(3, invalid.ValidationErrors.Count);
        Assert.Equal("192K", invalid.AudioQuality);
        Assert.Equal(300, invalid.TimeoutSeconds);
        Assert.Equal(1, invalid.ConcurrentDownloads);
    }

    private YtDlpThemeAudioProvider CreateProvider(FakeRunner runner) => new(
        _configuration, _cookies, new ReadyDiagnostics(_configuration, _cookies), runner,
        new YtDlpConcurrencyGate(_configuration), NullLogger<YtDlpThemeAudioProvider>.Instance);

    private FakeRunner SuccessfulRunner(byte[] bytes) => new(request =>
    {
        var output = Path.Combine(request.WorkingDirectory, VideoId + ".mp3");
        File.WriteAllBytes(output, bytes);
        request.OnStandardOutput?.Invoke("THEMEARR_TITLE:\"Example Theme\"");
        request.OnStandardOutput?.Invoke("THEMEARR_FILE:" + System.Text.Json.JsonSerializer.Serialize(output));
        return new(0, "", "", false, false);
    });

    private string OutputPath(string folder = "media")
    {
        var directory = Path.Combine(_root, folder);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "theme.mp3");
    }

    private string FakeExecutable(string name)
    {
        var path = Path.Combine(_root, OperatingSystem.IsWindows() ? name + ".exe" : name);
        File.WriteAllBytes(path, [0]);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static string CookieFile(string fakeValue) =>
        "# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t2147483647\tFAKE_SESSION\t" + fakeValue + "\n";

    private void SetEnvironment(string name, string? value)
    {
        if (!_oldEnvironment.ContainsKey(name)) _oldEnvironment[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _oldEnvironment) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class FakeRunner(Func<ExternalProcessRequest, ExternalProcessResult> result) : IExternalProcessRunner
    {
        public ExternalProcessRequest? Request { get; private set; }
        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(result(request));
        }
    }

    private sealed class ReadyDiagnostics(DownloaderConfiguration configuration, IYoutubeCookieStore cookies) : IDownloaderDiagnosticsService
    {
        public Task<DownloaderDiagnostics> CheckAsync(bool forceRefresh = false, CancellationToken ct = default)
        {
            var settings = configuration.GetSnapshot();
            return Task.FromResult(new DownloaderDiagnostics(true, false, "healthy", "Ready",
                new(true, "available", "test"), new(true, "available", "test"),
                new(true, "available", "test"), new(true, "available", "test"),
                cookies.Resolve().Status, new("disabled", "disabled", false, false, null),
                settings.AudioQuality, settings.TimeoutSeconds, settings.ConcurrentDownloads,
                settings.AudioQualityManagedByEnvironment, settings.TimeoutManagedByEnvironment,
                settings.ConcurrencyManagedByEnvironment));
        }
    }
}
