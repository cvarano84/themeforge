using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

[Collection("Downloader environment")]
public sealed class PoTokenProviderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Dictionary<string, string?> _old = new();
    private readonly DownloaderConfiguration _configuration;

    public PoTokenProviderTests()
    {
        var db = new Database(Path.Combine(_dir.Path, "settings.db"));
        db.Init();
        _configuration = new DownloaderConfiguration(db);
        Set("YTDLP_PO_TOKEN_MODE", null);
        Set("YTDLP_PO_TOKEN_PROVIDER_URL", null);
        Set("YTDLP_PO_TOKEN_PLUGIN_DIR", null);
    }

    [Theory]
    [InlineData(null, PoTokenMode.Auto)]
    [InlineData("auto", PoTokenMode.Auto)]
    [InlineData("disabled", PoTokenMode.Disabled)]
    [InlineData("required", PoTokenMode.Required)]
    public void Parses_supported_modes(string? value, PoTokenMode expected)
    {
        Set("YTDLP_PO_TOKEN_MODE", value);
        Assert.Equal(expected, _configuration.GetSnapshot().PoTokenMode);
    }

    [Fact]
    public void Invalid_mode_safely_defaults_and_reports_configuration_error()
    {
        Set("YTDLP_PO_TOKEN_MODE", "aggressive");
        var snapshot = _configuration.GetSnapshot();
        Assert.Equal(PoTokenMode.Auto, snapshot.PoTokenMode);
        Assert.Contains(snapshot.ValidationErrors, e => e.Contains("YTDLP_PO_TOKEN_MODE"));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://provider:4416")]
    [InlineData("http://user:password@provider:4416")]
    [InlineData("http://provider:4416?token=fake")]
    [InlineData("http://provider:4416/#fragment")]
    public void Rejects_malformed_or_sensitive_provider_urls(string value)
    {
        Set("YTDLP_PO_TOKEN_PROVIDER_URL", value);
        var snapshot = _configuration.GetSnapshot();
        Assert.Null(snapshot.PoTokenProviderUrl);
        Assert.Contains(snapshot.ValidationErrors, e => e.Contains("YTDLP_PO_TOKEN_PROVIDER_URL"));
    }

    [Fact]
    public async Task Detects_plugin_and_reads_safe_version_from_bounded_ping_without_contacting_youtube()
    {
        var pluginDirectory = PluginDirectory();
        Set("YTDLP_PO_TOKEN_PROVIDER_URL", "http://themearr-pot-provider:4416");
        Set("YTDLP_PO_TOKEN_PLUGIN_DIR", pluginDirectory);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"server_uptime\":12,\"version\":\"1.3.1\"}", Encoding.UTF8, "application/json"),
        });
        var runner = new RecordingRunner(new(0, "extractors", "[debug] Plugin directories loaded", false, false));

        var status = await new PoTokenProviderDiagnostics(runner, new StubFactory(handler))
            .CheckAsync(_configuration.GetSnapshot(), FakeExecutable("yt-dlp"), _dir.Path);

        Assert.Equal("ready", status.Status);
        Assert.True(status.PluginDetected);
        Assert.True(status.ProviderReachable);
        Assert.Equal("1.3.1", status.Version);
        Assert.Equal("/ping", handler.Requests.Single().RequestUri!.AbsolutePath);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.Host.Contains("youtube", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(runner.Requests.SelectMany(r => r.Arguments), a => a.Contains("youtube.com"));
    }

    [Fact]
    public async Task Auto_degrades_and_required_reports_unavailable_when_provider_cannot_be_reached()
    {
        var pluginDirectory = PluginDirectory();
        Set("YTDLP_PO_TOKEN_PROVIDER_URL", "http://themearr-pot-provider:4416");
        Set("YTDLP_PO_TOKEN_PLUGIN_DIR", pluginDirectory);
        var service = new PoTokenProviderDiagnostics(
            new RecordingRunner(new(0, "", "", false, false)),
            new StubFactory(new StubHandler(_ => throw new HttpRequestException("offline at secret-url"))));

        Set("YTDLP_PO_TOKEN_MODE", "auto");
        var automatic = await service.CheckAsync(_configuration.GetSnapshot(), FakeExecutable("yt-dlp"), _dir.Path);
        Assert.Equal("degraded", automatic.Status);
        Assert.DoesNotContain("secret-url", automatic.Detail);

        Set("YTDLP_PO_TOKEN_MODE", "required");
        var required = await service.CheckAsync(_configuration.GetSnapshot(), FakeExecutable("yt-dlp"), _dir.Path);
        Assert.Equal("requiredUnavailable", required.Status);
    }

    [Fact]
    public async Task Missing_plugin_is_reported_separately()
    {
        Set("YTDLP_PO_TOKEN_PROVIDER_URL", "http://themearr-pot-provider:4416");
        Set("YTDLP_PO_TOKEN_PLUGIN_DIR", Path.Combine(_dir.Path, "missing-plugins"));
        var status = await new PoTokenProviderDiagnostics(
                new RecordingRunner(new(0, "", "", false, false)),
                new StubFactory(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"version\":\"1.3.1\"}") })))
            .CheckAsync(_configuration.GetSnapshot(), FakeExecutable("yt-dlp"), _dir.Path);

        Assert.False(status.PluginDetected);
        Assert.Equal("degraded", status.Status);
        Assert.Contains("plugin", status.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Downloader_arguments_are_individual_and_only_added_when_ready_and_enabled()
    {
        var pluginDirectory = PluginDirectory();
        Set("YTDLP_PO_TOKEN_PROVIDER_URL", "http://themearr-pot-provider:4416");
        Set("YTDLP_PO_TOKEN_PLUGIN_DIR", pluginDirectory);
        Set("YTDLP_PO_TOKEN_MODE", "auto");
        var snapshot = _configuration.GetSnapshot();

        var ready = YtDlpThemeAudioProvider.BuildArguments("abc12345678", _dir.Path,
            FakeExecutable("ffmpeg"), null, snapshot, null, poProviderReady: true);
        var extractorIndexes = ready.Select((value, index) => (value, index))
            .Where(item => item.value == "--extractor-args").Select(item => item.index).ToArray();
        Assert.Equal(2, extractorIndexes.Length);
        Assert.Equal("youtubepot-bgutilhttp:base_url=http://themearr-pot-provider:4416",
            ready[extractorIndexes[0] + 1]);
        Assert.Equal("youtube:player_client=default,mweb", ready[extractorIndexes[1] + 1]);
        Assert.Contains("--plugin-dirs", ready);

        var degraded = YtDlpThemeAudioProvider.BuildArguments("abc12345678", _dir.Path,
            FakeExecutable("ffmpeg"), null, snapshot, null, poProviderReady: false);
        Assert.DoesNotContain("--extractor-args", degraded);

        Set("YTDLP_PO_TOKEN_MODE", "disabled");
        var disabled = YtDlpThemeAudioProvider.BuildArguments("abc12345678", _dir.Path,
            FakeExecutable("ffmpeg"), null, _configuration.GetSnapshot(), null, poProviderReady: true);
        Assert.DoesNotContain("--extractor-args", disabled);
    }

    [Theory]
    [InlineData("ERROR: Sign in to confirm you're not a bot", ThemeAudioFailureCode.YOUTUBE_BOT_CHECK)]
    [InlineData("ERROR: PO Token is required but not provided", ThemeAudioFailureCode.PO_TOKEN_REQUIRED)]
    [InlineData("ERROR: PO token provider unavailable", ThemeAudioFailureCode.PO_TOKEN_PROVIDER_UNAVAILABLE)]
    [InlineData("ERROR: cookies are expired and invalid", ThemeAudioFailureCode.COOKIE_FILE_INVALID)]
    public void Classifies_authentication_and_po_failures_without_tokens(
        string error, ThemeAudioFailureCode code)
    {
        var cookies = new YoutubeCookieResolution(
            new(true, "managed", false, true, true, true, 1, 1, DateTime.UtcNow), "cookies.txt");
        var po = new PoTokenProviderStatus("auto", "ready", true, true, "1.3.1");
        var result = YtDlpThemeAudioProvider.ClassifyFailure(error, _dir.Path, cookies, po);
        Assert.Equal(code, result.Code);
        Assert.DoesNotContain("po_token=", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitizer_removes_generated_tokens_authorization_and_sapisid_hashes()
    {
        var sanitized = ProcessOutputSanitizer.SafeErrorExcerpt(
            "Authorization: Bearer FAKE_AUTH_SECRET po_token=mweb.gvs+FAKE_PO_SECRET SAPISIDHASH=FAKE_HASH_SECRET",
            _dir.Path, null);

        Assert.DoesNotContain("FAKE_AUTH_SECRET", sanitized);
        Assert.DoesNotContain("FAKE_PO_SECRET", sanitized);
        Assert.DoesNotContain("FAKE_HASH_SECRET", sanitized);
        Assert.Contains("<redacted>", sanitized);
    }

    private string PluginDirectory()
    {
        var path = Path.Combine(_dir.Path, "plugins");
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "bgutil-ytdlp-pot-provider.zip"), [1, 2, 3]);
        return path;
    }

    private string FakeExecutable(string name)
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

    private sealed class RecordingRunner(ExternalProcessResult result) : IExternalProcessRunner
    {
        public List<ExternalProcessRequest> Requests { get; } = [];
        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        { Timeout = TimeSpan.FromSeconds(1) };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(response(request));
        }
    }
}
