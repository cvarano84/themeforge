using System.Net;
using System.Text;
using System.Text.Json;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

public class SonarrShowLibrarySourceTests
{
    private const string Secret = "secret-sonarr-key";

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static (SonarrShowLibrarySource Source, Database Db) New(
        TempDir dir, HttpMessageHandler handler, string? root = null)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.SetSetting("sonarr_url", "http://sonarr.local:8989/");
        db.SetSetting("sonarr_api_key", Secret);
        db.SetSetting("show_library_source", "sonarr");
        db.SetLibraryPaths([root ?? dir.Path]);
        return (new SonarrShowLibrarySource(db, new LocalFolderResolver(db), new Factory(handler)), db);
    }

    private static string Series(string path, string extra = "") => $$"""
        [{"id":42,"title":"Severance","year":2022,"firstAired":"2022-02-18T00:00:00Z",
          "path":"{{path.Replace("\\", "/")}}","statistics":{"episodeFileCount":9,"sizeOnDisk":12345},
          "images":[{"coverType":"poster","remoteUrl":"https://example/poster.jpg"}]{{extra}}}]
        """;

    [Fact]
    public async Task Connectivity_uses_v3_system_status_and_a_header_key()
    {
        using var dir = new TempDir();
        var handler = new Handler(_ => Json("""{"version":"4.0"}"""));
        var (source, _) = New(dir, handler);

        Assert.Null(await source.CheckAsync(CancellationToken.None));
        Assert.Equal("/api/v3/system/status", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(Secret, handler.LastRequest.Headers.GetValues("X-Api-Key").Single());
        Assert.DoesNotContain(Secret, handler.LastRequest.RequestUri.ToString());
    }

    [Fact]
    public async Task Downloaded_series_is_imported_with_year_path_and_poster()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "Severance"); Directory.CreateDirectory(folder);
        var handler = new Handler(_ => Json(Series(folder)));
        var (source, _) = New(dir, handler);

        var show = Assert.Single(await source.FetchAsync(_ => { }, CancellationToken.None));

        Assert.Equal(folder, show.Folder);
        Assert.Equal("sonarr", show.Source);
        Assert.Equal("42", show.SourceRef);
        Assert.Equal("Severance", show.Title);
        Assert.Equal(2022, show.Year);
        Assert.False(show.HasPlexTheme);
        Assert.True(show.HasPoster);
    }

    [Theory]
    [InlineData("", 1, 1)]
    [InlineData("/tv/Waiting", 0, 0)]
    public async Task Empty_paths_and_series_without_downloaded_media_are_skipped(
        string path, int files, int size)
    {
        using var dir = new TempDir();
        var body = JsonSerializer.Serialize(new[]
        {
            new { id = 1, title = "Waiting", path, statistics = new { episodeFileCount = files, sizeOnDisk = size } },
        });
        var handler = new Handler(_ => Json(body));
        var (source, _) = New(dir, handler);

        Assert.Empty(await source.FetchAsync(_ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task Malformed_individual_series_is_skipped_without_losing_valid_series()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "Severance"); Directory.CreateDirectory(folder);
        var safeFolder = folder.Replace("\\", "/");
        var body = "[{\"id\":\"broken\",\"title\":7,\"path\":false,\"statistics\":{\"episodeFileCount\":\"many\"}}," +
                   "{\"id\":42,\"title\":\"Severance\",\"path\":\"" + safeFolder +
                   "\",\"statistics\":{\"episodeFileCount\":1}}]";
        var handler = new Handler(_ => Json(body));
        var (source, _) = New(dir, handler);
        var logs = new List<string>();

        var show = Assert.Single(await source.FetchAsync(logs.Add, CancellationToken.None));

        Assert.Equal("Severance", show.Title);
        Assert.Contains(logs, line => line.Contains("malformed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Path_mapping_translates_Sonarr_folder_and_normalizes_trailing_slash()
    {
        using var dir = new TempDir();
        var tvRoot = Path.Combine(dir.Path, "shows");
        var folder = Path.Combine(tvRoot, "Severance"); Directory.CreateDirectory(folder);
        var handler = new Handler(_ => Json(Series("/tv/Severance/")));
        var (source, db) = New(dir, handler, tvRoot);
        db.SetPathMappings([new() { ["source"] = "/tv", ["target"] = tvRoot }]);

        var show = Assert.Single(await source.FetchAsync(_ => { }, CancellationToken.None));

        Assert.Equal(folder, show.Folder);
        Assert.False(show.SourcePath.EndsWith('/'));
        Assert.Equal("1", db.GetSetting("last_show_sync_mapping", "0"));
    }

    [Fact]
    public async Task Unresolved_paths_are_counted_and_sampled()
    {
        using var dir = new TempDir();
        var handler = new Handler(_ => Json(Series("/tv/Missing")));
        var (source, db) = New(dir, handler);

        Assert.Empty(await source.FetchAsync(_ => { }, CancellationToken.None));
        Assert.Equal("1", db.GetSetting("last_show_sync_unresolved_count", "0"));
        Assert.Equal("/tv/Missing", db.GetSetting("last_show_sync_unresolved_sample", ""));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"series\":[]}")]
    public async Task Malformed_or_non_array_responses_fail_cleanly(string body)
    {
        using var dir = new TempDir();
        var handler = new Handler(_ => Json(body));
        var (source, _) = New(dir, handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.FetchAsync(_ => { }, CancellationToken.None));

        Assert.Contains("unexpected response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, error.Message);
    }

    [Fact]
    public async Task Fetch_reports_401_without_leaking_the_key()
    {
        using var dir = new TempDir();
        var handler = new Handler(_ => Json("{}", HttpStatusCode.Unauthorized));
        var (source, _) = New(dir, handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.FetchAsync(_ => { }, CancellationToken.None));

        Assert.Contains("401", error.Message);
        Assert.DoesNotContain(Secret, error.Message);
    }

    [Fact]
    public async Task Timeout_and_unreachable_host_have_actionable_sanitized_messages()
    {
        using var timeoutDir = new TempDir();
        var (timeoutSource, _) = New(timeoutDir, new Handler(_ => throw new TaskCanceledException(Secret)));
        var timeout = await Assert.ThrowsAsync<InvalidOperationException>(
            () => timeoutSource.FetchAsync(_ => { }, CancellationToken.None));
        Assert.Contains("did not respond", timeout.Message);
        Assert.DoesNotContain(Secret, timeout.Message);

        using var unreachableDir = new TempDir();
        var (unreachableSource, _) = New(unreachableDir,
            new Handler(_ => throw new HttpRequestException($"connection failed {Secret}")));
        var unreachable = await Assert.ThrowsAsync<InvalidOperationException>(
            () => unreachableSource.FetchAsync(_ => { }, CancellationToken.None));
        Assert.Contains("unreachable", unreachable.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, unreachable.Message);
    }

    [Fact]
    public async Task Non_success_response_is_reported_without_body_or_key()
    {
        using var dir = new TempDir();
        var handler = new Handler(_ => Json($"{{\"detail\":\"{Secret}\"}}", HttpStatusCode.BadGateway));
        var (source, _) = New(dir, handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.FetchAsync(_ => { }, CancellationToken.None));

        Assert.Contains("502", error.Message);
        Assert.DoesNotContain(Secret, error.Message);
    }

    [Fact]
    public async Task Poster_fetch_uses_media_cover_endpoint_and_returns_buffered_bytes()
    {
        using var dir = new TempDir();
        byte[] bytes = [1, 2, 3, 4];
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });
        var (source, _) = New(dir, handler);

        await using var stream = await source.FetchPosterAsync("42", 300, CancellationToken.None);
        Assert.NotNull(stream);
        using var copied = new MemoryStream();
        await stream!.CopyToAsync(copied);
        Assert.Equal(bytes, copied.ToArray());
        Assert.Equal("/api/v3/mediacover/42/poster.jpg", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.DoesNotContain(Secret, handler.LastRequest.RequestUri.ToString());
    }

    [Fact]
    public async Task Oversized_posters_are_rejected()
    {
        using var dir = new TempDir();
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[20 * 1024 * 1024 + 1]),
        });
        var (source, _) = New(dir, handler);

        Assert.Null(await source.FetchPosterAsync("42", 300, CancellationToken.None));
    }

    [Fact]
    public async Task Key_never_appears_in_logs()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "Severance"); Directory.CreateDirectory(folder);
        var handler = new Handler(_ => Json(Series(folder)));
        var (source, _) = New(dir, handler);
        var logs = new List<string>();

        await source.FetchAsync(logs.Add, CancellationToken.None);

        Assert.DoesNotContain(logs, line => line.Contains(Secret, StringComparison.Ordinal));
    }
}
