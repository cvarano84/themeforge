using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

public sealed class ArrInstancesTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(respond?.Invoke(request) ?? Json("{}"));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        { Timeout = TimeSpan.FromSeconds(2) };
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private static (ArrInstancesController Controller, Database Db, Handler Handler) New(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init();
        var handler = new Handler();
        var factory = new Factory(handler);
        var folders = new LocalFolderResolver(db);
        return (new ArrInstancesController(db,
            new RadarrLibrarySource(db, folders, factory),
            new SonarrShowLibrarySource(db, folders, factory)), db, handler);
    }

    [Fact]
    public void Creates_multiple_instances_and_never_serializes_api_keys()
    {
        using var dir = new TempDir();
        var (controller, db, _) = New(dir);

        Assert.IsType<CreatedResult>(controller.Create(new ArrInstancePayload(
            "radarr", "Movies", "http://radarr:7878/", "movie-secret", true, "1080p", 10)));
        Assert.IsType<CreatedResult>(controller.Create(new ArrInstancePayload(
            "radarr", "Movies - 4K", "http://radarr-4k:7878///", "4k-secret", true, "4K", 0)));
        Assert.IsType<CreatedResult>(controller.Create(new ArrInstancePayload(
            "sonarr", "Anime", "http://sonarr-anime:8989", "anime-secret", false, "Anime", 2)));

        Assert.Equal(3, db.GetArrInstances().Count);
        Assert.Equal("http://radarr:7878", db.GetArrInstances("radarr").Single(i => i.Name == "Movies").Url);
        var response = Assert.IsType<OkObjectResult>(controller.List());
        var json = JsonSerializer.Serialize(response.Value);
        Assert.DoesNotContain("movie-secret", json);
        Assert.DoesNotContain("4k-secret", json);
        Assert.DoesNotContain("anime-secret", json);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Duplicate_normalized_service_url_is_rejected()
    {
        using var dir = new TempDir();
        var (controller, _, _) = New(dir);
        Assert.IsType<CreatedResult>(controller.Create(new ArrInstancePayload(
            "radarr", "A", "http://RADARR:7878/", "key-a")));
        Assert.IsType<ConflictObjectResult>(controller.Create(new ArrInstancePayload(
            "RADARR", "B", "http://radarr:7878", "key-b")));
        // Same URL is valid for another service type.
        Assert.IsType<CreatedResult>(controller.Create(new ArrInstancePayload(
            "sonarr", "C", "http://radarr:7878", "key-c")));
    }

    [Fact]
    public void Blank_key_keeps_secret_only_for_the_same_destination()
    {
        using var dir = new TempDir();
        var (controller, db, _) = New(dir);
        var created = db.CreateArrInstance("radarr", "Movies", "http://radarr:7878", "stored-secret",
            true, "1080p", 0, null);

        Assert.IsType<OkObjectResult>(controller.Update(created.Id, new ArrInstancePayload(
            "radarr", "Movies renamed", "http://radarr:7878/", "", true, "1080p", 1)));
        Assert.Equal("stored-secret", db.GetArrInstance(created.Id)!.ApiKey);

        Assert.IsType<BadRequestObjectResult>(controller.Update(created.Id, new ArrInstancePayload(
            "radarr", "Movies renamed", "http://other:7878", "", true, "1080p", 1)));
        Assert.Equal("http://radarr:7878", db.GetArrInstance(created.Id)!.Url);
    }

    [Fact]
    public async Task Blank_test_key_is_never_sent_to_a_changed_url()
    {
        using var dir = new TempDir();
        var (controller, db, handler) = New(dir);
        var created = db.CreateArrInstance("radarr", "Movies", "http://radarr:7878", "stored-secret",
            true, null, 0, null);

        var response = Assert.IsType<OkObjectResult>(await controller.Test(new ArrInstanceTestPayload(
            "radarr", "http://attacker.invalid:7878", "", created.Id), CancellationToken.None));
        Assert.False((bool)response.Value!.GetType().GetProperty("ok")!.GetValue(response.Value)!);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Legacy_credentials_migrate_once_and_remain_as_fallback_settings()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "legacy.db");
        var initial = new Database(path); initial.Init();
        initial.SetSetting("radarr_url", "http://radarr:7878/");
        initial.SetSetting("radarr_api_key", "legacy-secret");

        var firstRestart = new Database(path); firstRestart.Init();
        var secondRestart = new Database(path); secondRestart.Init();

        var instance = Assert.Single(secondRestart.GetArrInstances("radarr"));
        Assert.Equal("Radarr", instance.Name);
        Assert.Equal("legacy-secret", instance.ApiKey);
        Assert.Equal("legacy-secret", secondRestart.GetSetting("radarr_api_key"));
    }

    [Fact]
    public void Namespaced_ids_group_by_external_identity_without_colliding()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "group.db")); db.Init();
        var a = db.CreateArrInstance("radarr", "Movies", "http://a:7878", "a", true, "1080p", 10, null);
        var b = db.CreateArrInstance("radarr", "Movies 4K", "http://b:7878", "b", true, "4K", 0, null);
        var fa = Path.Combine(dir.Path, "movies", "Matrix"); Directory.CreateDirectory(fa);
        var fb = Path.Combine(dir.Path, "movies4k", "Matrix"); Directory.CreateDirectory(fb);
        db.UpsertMovies([
            new MovieRecord(fa, "radarr", $"radarr:{a.Id}:1", "The Matrix", 1999, "/movies/Matrix", a.Id, "1", "1080p", "603", "tt0133093"),
            new MovieRecord(fb, "radarr", $"radarr:{b.Id}:1", "The Matrix", 1999, "/movies/Matrix", b.Id, "1", "4K", "603", "tt0133093")]);

        var physical = db.GetAllMovies();
        Assert.Equal(2, physical.Select(row => row["sourceRef"]).Distinct().Count());
        var logical = Assert.Single(MediaGrouping.Group(physical, db.GetArrInstances(), shows: false));
        Assert.Equal(2, logical["locationCount"]);
        Assert.Equal("4K", logical["qualityLabel"]); // lower numeric priority wins metadata/poster.
    }

    [Fact]
    public async Task Healthy_instance_continues_when_another_instance_fails()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "partial.db")); db.Init();
        var folder = Path.Combine(dir.Path, "movies", "Heat"); Directory.CreateDirectory(folder);
        db.SetLibraryPaths([dir.Path]);
        var good = db.CreateArrInstance("radarr", "Movies", "http://good:7878", "good-key", true, "1080p", 0, null);
        var bad = db.CreateArrInstance("radarr", "Movies 4K", "http://bad:7878", "bad-key", true, "4K", 1, null);
        var body = JsonSerializer.Serialize(new[] { new
        {
            id = 7, title = "Heat", year = 1995, hasFile = true, path = folder,
            tmdbId = 949, imdbId = "tt0113277",
        }});
        var handler = new Handler(request => request.RequestUri!.Host == "good"
            ? Json(body)
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var source = new RadarrLibrarySource(db, new LocalFolderResolver(db), new Factory(handler));
        var logs = new List<string>();

        var movies = await source.FetchAsync(logs.Add, CancellationToken.None);

        var movie = Assert.Single(movies);
        Assert.Equal($"radarr:{good.Id}:7", movie.SourceRef);
        Assert.Contains(good.Id, source.LastSuccessfulInstanceIds);
        Assert.DoesNotContain(bad.Id, source.LastSuccessfulInstanceIds);
        Assert.Equal("healthy", db.GetArrInstance(good.Id)!.Health);
        Assert.Equal("error", db.GetArrInstance(bad.Id)!.Health);
        Assert.Contains(logs, line => line.Contains("1 of 2 Radarr instances synced successfully"));
    }

    [Fact]
    public async Task All_enabled_instance_failures_return_an_actionable_aggregate()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "failed.db")); db.Init();
        db.CreateArrInstance("sonarr", "TV", "http://one:8989", "one", true, "1080p", 0, null);
        db.CreateArrInstance("sonarr", "TV 4K", "http://two:8989", "two", true, "4K", 1, null);
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var source = new SonarrShowLibrarySource(db, new LocalFolderResolver(db), new Factory(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.FetchAsync(_ => { }, CancellationToken.None));
        Assert.Contains("All enabled Sonarr instances failed", error.Message);
        Assert.Contains("TV:", error.Message);
        Assert.Contains("TV 4K:", error.Message);
        Assert.DoesNotContain("one", error.Message); // API keys never appear in aggregate errors.
    }

    [Fact]
    public void Identical_remote_paths_resolve_through_instance_scoped_mappings()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "paths.db")); db.Init();
        var movies = Path.Combine(dir.Path, "movies");
        var movies4k = Path.Combine(dir.Path, "movies4k");
        var aFolder = Path.Combine(movies, "Matrix");
        var bFolder = Path.Combine(movies4k, "Matrix");
        Directory.CreateDirectory(aFolder); Directory.CreateDirectory(bFolder);
        db.SetLibraryPaths([dir.Path]);
        var a = db.CreateArrInstance("radarr", "Movies", "http://a:7878", "a", true, null, 0, null);
        var b = db.CreateArrInstance("radarr", "Movies 4K", "http://b:7878", "b", true, null, 1, null);
        db.SetPathMappings([
            new Dictionary<string, string> { ["source"] = "/movies", ["target"] = movies, ["instanceId"] = a.Id },
            new Dictionary<string, string> { ["source"] = "/movies", ["target"] = movies4k, ["instanceId"] = b.Id },
        ]);
        var resolver = new LocalFolderResolver(db);

        Assert.Equal(Path.GetFullPath(aFolder), resolver.ResolveFolderDetailed("/movies/Matrix", a.Id, "radarr").ResolvedFolderPath);
        Assert.Equal(Path.GetFullPath(bFolder), resolver.ResolveFolderDetailed("/movies/Matrix", b.Id, "radarr").ResolvedFolderPath);
    }
}
