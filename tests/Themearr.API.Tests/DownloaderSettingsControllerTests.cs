using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

[Collection("Downloader environment")]
public sealed class DownloaderSettingsControllerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Database _db;
    private readonly DownloaderConfiguration _configuration;

    public DownloaderSettingsControllerTests()
    {
        _db = new Database(Path.Combine(_dir.Path, "settings.db"));
        _db.Init();
        _configuration = new DownloaderConfiguration(_db);
    }

    [Fact]
    public void Legacy_credential_routes_are_absent_and_values_never_appear_in_settings_response()
    {
        _db.SetSetting("rapid" + "api_key", "old-secret-value");
        _db.SetSetting("rapid" + "api_username", "old-user-value");
        var controller = TestControllers.NewSettingsController(_db, new ApiKeyStore(_db));

        var json = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(controller.Get()).Value);
        var templates = typeof(SettingsController).GetMethods()
            .SelectMany(method => method.GetCustomAttributes(true).OfType<HttpMethodAttribute>())
            .Select(attribute => attribute.Template ?? "").ToArray();

        Assert.DoesNotContain("old-secret-value", json);
        Assert.DoesNotContain("old-user-value", json);
        Assert.DoesNotContain(templates, route => route.Contains("rapid" + "api", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("96K", 300, 1)]
    [InlineData("192K", 29, 1)]
    [InlineData("192K", 1801, 1)]
    [InlineData("192K", 300, 0)]
    [InlineData("192K", 300, 4)]
    public async Task Put_rejects_invalid_values(string quality, int timeout, int concurrency)
    {
        var controller = new DownloaderSettingsController(_configuration, new ConfigurationDiagnostics(_configuration));
        var result = await controller.Save(new(quality, timeout, concurrency), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Test_endpoint_forces_a_fresh_local_configuration_check()
    {
        var diagnostics = new ConfigurationDiagnostics(_configuration);
        var controller = new DownloaderSettingsController(_configuration, diagnostics);

        var result = Assert.IsType<OkObjectResult>(await controller.Test(CancellationToken.None));

        Assert.Equal(1, diagnostics.Calls);
        Assert.True(diagnostics.LastForceRefresh);
        Assert.DoesNotContain("youtube.com", System.Text.Json.JsonSerializer.Serialize(result.Value), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _dir.Dispose();

    private sealed class ConfigurationDiagnostics(DownloaderConfiguration configuration) : IDownloaderDiagnosticsService
    {
        public int Calls { get; private set; }
        public bool LastForceRefresh { get; private set; }
        public Task<DownloaderDiagnostics> CheckAsync(bool forceRefresh = false, CancellationToken ct = default)
        {
            Calls++;
            LastForceRefresh = forceRefresh;
            var value = configuration.GetSnapshot();
            return Task.FromResult(new DownloaderDiagnostics(true, false, "healthy", "Ready",
                new(true, "available", "test"), new(true, "available", "test"),
                new(true, "available", "test"), new(true, "available", "test"),
                new(false, "none", false, true, false, false, 0, 0, null),
                new("auto", "notConfigured", true, false, null),
                value.AudioQuality, value.TimeoutSeconds, value.ConcurrentDownloads,
                value.AudioQualityManagedByEnvironment, value.TimeoutManagedByEnvironment,
                value.ConcurrencyManagedByEnvironment));
        }
    }
}
