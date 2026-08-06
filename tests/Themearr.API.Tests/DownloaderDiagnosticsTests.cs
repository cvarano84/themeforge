using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

[Collection("Downloader environment")]
public sealed class DownloaderDiagnosticsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Dictionary<string, string?> _old = new();
    private readonly DownloaderConfiguration _configuration;
    private readonly VersionRunner _runner = new();
    private readonly IYoutubeCookieStore _cookies;

    public DownloaderDiagnosticsTests()
    {
        var db = new Database(Path.Combine(_dir.Path, "diagnostics.db"));
        db.Init();
        _configuration = new DownloaderConfiguration(db);
        _cookies = new YoutubeCookieStore(new ApplicationDataDirectory(Path.Combine(_dir.Path, "diagnostics.db")),
            NullLogger<YoutubeCookieStore>.Instance);
        _ = Executable("ffprobe");
        Set("YTDLP_PO_TOKEN_MODE", "disabled");
    }

    [Fact]
    public async Task Missing_yt_dlp_is_unhealthy_without_running_a_download()
    {
        Set("YTDLP_PATH", Path.Combine(_dir.Path, "missing-ytdlp"));
        Set("FFMPEG_PATH", Executable("ffmpeg"));
        var result = await Service().CheckAsync(true);

        Assert.False(result.Ready);
        Assert.Equal("unhealthy", result.Status);
        Assert.Contains("yt-dlp", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_runner.Requests, r => r.Arguments.Any(a => a.Contains("youtube.com")));
    }

    [Fact]
    public async Task Missing_ffmpeg_is_unhealthy()
    {
        Set("YTDLP_PATH", Executable("yt-dlp"));
        Set("FFMPEG_PATH", Path.Combine(_dir.Path, "missing-ffmpeg"));
        var result = await Service().CheckAsync(true);

        Assert.False(result.Ready);
        Assert.Contains("FFmpeg", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_optional_cookies_file_is_degraded_and_path_is_not_returned()
    {
        Set("YTDLP_PATH", Executable("yt-dlp"));
        Set("FFMPEG_PATH", Executable("ffmpeg"));
        var missing = Path.Combine(_dir.Path, "private", "cookies.txt");
        Set("YTDLP_COOKIES_FILE", missing);
        var result = await Service().CheckAsync(true);

        Assert.True(result.Ready);
        Assert.True(result.Degraded);
        Assert.True(result.Cookies.Configured);
        Assert.False(result.Cookies.Valid);
        Assert.Equal("environment", result.Cookies.Source);
        Assert.DoesNotContain(missing, System.Text.Json.JsonSerializer.Serialize(result));
    }

    private DownloaderDiagnosticsService Service() => new(
        _configuration, _cookies, new DisabledPoDiagnostics(), _runner,
        NullLogger<DownloaderDiagnosticsService>.Instance);

    private string Executable(string name)
    {
        var path = Path.Combine(_dir.Path, OperatingSystem.IsWindows() ? name + ".exe" : name);
        File.WriteAllBytes(path, [0]);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private void Set(string name, string? value)
    {
        if (!_old.ContainsKey(name)) _old[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _old) Environment.SetEnvironmentVariable(name, value);
        _dir.Dispose();
    }

    private sealed class VersionRunner : IExternalProcessRunner
    {
        public List<ExternalProcessRequest> Requests { get; } = [];
        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var output = Path.GetFileName(request.ExecutablePath).Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)
                ? "ffmpeg version 7.1" : "2026.07.04";
            return Task.FromResult(new ExternalProcessResult(0, output, "", false, false));
        }
    }

    private sealed class DisabledPoDiagnostics : IPoTokenProviderDiagnostics
    {
        public Task<PoTokenProviderStatus> CheckAsync(DownloaderConfigurationSnapshot settings,
            string? ytDlpPath, string workingDirectory, CancellationToken ct = default) =>
            Task.FromResult(new PoTokenProviderStatus("disabled", "disabled", false, false, null));
    }
}
