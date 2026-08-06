using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Controllers;
using Themearr.API.Services;

namespace Themearr.API.Tests;

[Collection("Downloader environment")]
public sealed class YoutubeCookieStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string? _oldEnvironment;
    private readonly YoutubeCookieStore _store;

    public YoutubeCookieStoreTests()
    {
        _oldEnvironment = Environment.GetEnvironmentVariable("YTDLP_COOKIES_FILE");
        Environment.SetEnvironmentVariable("YTDLP_COOKIES_FILE", null);
        var databasePath = Path.Combine(_dir.Path, "data", "themearr.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _store = new YoutubeCookieStore(new ApplicationDataDirectory(databasePath),
            NullLogger<YoutubeCookieStore>.Instance);
    }

    [Theory]
    [InlineData("# Netscape HTTP Cookie File", "\n", ".youtube.com")]
    [InlineData("# HTTP Cookie File", "\r\n", "youtube.com")]
    [InlineData("# Netscape HTTP Cookie File", "\n", "#HttpOnly_.accounts.google.com")]
    public async Task Accepts_supported_headers_line_endings_and_http_only_records(
        string header, string newline, string domain)
    {
        var status = await Upload(CookieText(header, newline, domain));

        Assert.True(status.Valid);
        Assert.Equal(1, status.RecordCount);
        Assert.Equal(1, status.YoutubeRecordCount);
        Assert.Equal("managed", status.Source);
        Assert.DoesNotContain('\r', await File.ReadAllTextAsync(_store.ManagedCookiePath));
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public async Task Rejects_empty_binary_html_json_sqlite_zip_and_malformed_files(byte[] content)
    {
        await Assert.ThrowsAsync<YoutubeCookieValidationException>(() =>
            _store.UploadAsync(new MemoryStream(content), content.Length));
        Assert.False(File.Exists(_store.ManagedCookiePath));
    }

    public static IEnumerable<object[]> InvalidFiles()
    {
        yield return [Array.Empty<byte>()];
        yield return [Encoding.UTF8.GetBytes("<html><form>login</form></html>")];
        yield return [Encoding.UTF8.GetBytes("[{\"domain\":\"youtube.com\"}]")];
        yield return [Encoding.ASCII.GetBytes("SQLite format 3\0fake")];
        yield return [new byte[] { (byte)'P', (byte)'K', 3, 4, 1, 2 }];
        yield return [Encoding.UTF8.GetBytes("# Netscape HTTP Cookie File\n")];
        yield return [Encoding.UTF8.GetBytes("# Netscape HTTP Cookie File\n.example.com\tTRUE\t/\tTRUE\t1\tN\tV\n")];
        yield return [Encoding.UTF8.GetBytes("# Netscape HTTP Cookie File\n.youtube.com only spaces\n")];
        yield return [new byte[] { 0xff, 0xfe, 0xfd }];
        yield return [Encoding.UTF8.GetBytes("# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t1\tN\tV\0")];
    }

    [Fact]
    public async Task Rejects_oversized_stream_even_when_declared_length_is_small()
    {
        var oversized = new byte[YoutubeCookieStore.MaximumBytes + 1];
        await Assert.ThrowsAsync<YoutubeCookieValidationException>(() =>
            _store.UploadAsync(new MemoryStream(oversized), 1));
    }

    [Fact]
    public async Task Invalid_replacement_keeps_existing_file_and_removes_temporary_files()
    {
        await Upload(CookieText(value: "FAKE_OLD_VALUE"));
        var before = await File.ReadAllTextAsync(_store.ManagedCookiePath);

        await Assert.ThrowsAsync<YoutubeCookieValidationException>(() =>
            Upload("<html>not cookies</html>"));

        Assert.Equal(before, await File.ReadAllTextAsync(_store.ManagedCookiePath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(_store.ManagedCookiePath)!, "*.tmp"));
    }

    [Fact]
    public async Task Valid_replacement_is_visible_immediately_and_temp_files_are_removed()
    {
        await Upload(CookieText(value: "FAKE_OLD_VALUE"));
        await Upload(CookieText(value: "FAKE_NEW_VALUE"));

        var resolution = _store.Resolve();
        Assert.Equal(_store.ManagedCookiePath, resolution.ActivePath);
        Assert.Contains("FAKE_NEW_VALUE", await File.ReadAllTextAsync(_store.ManagedCookiePath));
        Assert.DoesNotContain("FAKE_OLD_VALUE", await File.ReadAllTextAsync(_store.ManagedCookiePath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(_store.ManagedCookiePath)!, "*.tmp"));
    }

    [Fact]
    public async Task Managed_path_is_beneath_data_directory_and_has_restrictive_linux_permissions()
    {
        await Upload(CookieText());
        Assert.Equal(Path.Combine(_dir.Path, "data", "secrets", "youtube-cookies.txt"),
            _store.ManagedCookiePath);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(_store.ManagedCookiePath));
    }

    [Fact]
    public async Task Symlink_destination_is_rejected_when_supported()
    {
        var target = Path.Combine(_dir.Path, "outside.txt");
        await File.WriteAllTextAsync(target, "outside");
        Directory.CreateDirectory(Path.GetDirectoryName(_store.ManagedCookiePath)!);
        try { File.CreateSymbolicLink(_store.ManagedCookiePath, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { return; }

        await Assert.ThrowsAsync<InvalidOperationException>(() => Upload(CookieText()));
        Assert.Equal("outside", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task Delete_is_idempotent_and_stops_resolving_managed_file()
    {
        await Upload(CookieText());
        Assert.True((await _store.DeleteAsync()).CanUpload);
        Assert.False(File.Exists(_store.ManagedCookiePath));
        var second = await _store.DeleteAsync();
        Assert.False(second.Configured);
        Assert.Null(_store.Resolve().ActivePath);
    }

    [Fact]
    public async Task Environment_source_takes_precedence_and_is_read_only()
    {
        await Upload(CookieText(value: "MANAGED_FAKE_VALUE"));
        var environmentFile = Path.Combine(_dir.Path, "environment-cookies.txt");
        await File.WriteAllTextAsync(environmentFile, CookieText(value: "ENV_FAKE_VALUE"));
        Environment.SetEnvironmentVariable("YTDLP_COOKIES_FILE", environmentFile);

        var resolution = _store.Resolve();
        Assert.Equal(environmentFile, resolution.ActivePath);
        Assert.Equal("environment", resolution.Status.Source);
        Assert.True(resolution.Status.ManagedByEnvironment);
        Assert.False(resolution.Status.CanUpload);
        Assert.False(resolution.Status.CanDelete);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Upload(CookieText()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.DeleteAsync());
        Assert.True(File.Exists(environmentFile));
    }

    [Fact]
    public void Missing_environment_file_reports_sanitized_warning_and_disables_upload()
    {
        var missing = Path.Combine(_dir.Path, "private", "cookies.txt");
        Environment.SetEnvironmentVariable("YTDLP_COOKIES_FILE", missing);
        var status = _store.Resolve().Status;

        Assert.True(status.Configured);
        Assert.False(status.Valid);
        Assert.False(status.CanUpload);
        Assert.DoesNotContain(missing, JsonSerializer.Serialize(status));
    }

    [Fact]
    public async Task Controller_ignores_traversal_filename_never_returns_values_and_sets_no_store()
    {
        var bytes = Encoding.UTF8.GetBytes(CookieText(value: "NEVER_RETURN_THIS_FAKE_VALUE"));
        var form = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "../../outside.txt");
        var controller = Controller();

        var result = Assert.IsType<OkObjectResult>(await controller.Upload(form, CancellationToken.None));
        var json = JsonSerializer.Serialize(result.Value);

        Assert.DoesNotContain("NEVER_RETURN_THIS_FAKE_VALUE", json);
        Assert.DoesNotContain("FAKE_SESSION", json);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "outside.txt")));
        Assert.True(File.Exists(_store.ManagedCookiePath));
    }

    [Fact]
    public async Task Controller_rejects_declared_oversize_with_413_and_status_get_is_sanitized()
    {
        var form = new FormFile(new MemoryStream([1]), 0, YoutubeCookieStore.MaximumBytes + 1,
            "file", "cookies.txt");
        var controller = Controller();
        Assert.Equal(StatusCodes.Status413PayloadTooLarge,
            Assert.IsType<ObjectResult>(await controller.Upload(form, CancellationToken.None)).StatusCode);

        await Upload(CookieText(value: "SECRET_FAKE_VALUE"));
        controller = Controller();
        var get = Assert.IsType<OkObjectResult>(controller.Get());
        Assert.DoesNotContain("SECRET_FAKE_VALUE", JsonSerializer.Serialize(get.Value));
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
    }

    [Fact]
    public void Cookie_route_is_inside_the_authenticated_api_boundary() =>
        Assert.True(ApiAuthMiddleware.RequiresAuth(new PathString("/api/settings/youtube-cookies")));

    private YoutubeCookiesController Controller()
    {
        var controller = new YoutubeCookiesController(_store)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return controller;
    }

    private Task<YoutubeCookieStatus> Upload(string text) =>
        Upload(Encoding.UTF8.GetBytes(text));

    private Task<YoutubeCookieStatus> Upload(byte[] bytes) =>
        _store.UploadAsync(new MemoryStream(bytes), bytes.Length);

    private static string CookieText(
        string header = "# Netscape HTTP Cookie File", string newline = "\n",
        string domain = ".youtube.com", string value = "OBVIOUSLY_FAKE_VALUE") =>
        string.Join(newline, header,
            $"{domain}\tTRUE\t/\tTRUE\t2147483647\tFAKE_SESSION\t{value}", "");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("YTDLP_COOKIES_FILE", _oldEnvironment);
        _dir.Dispose();
    }
}
