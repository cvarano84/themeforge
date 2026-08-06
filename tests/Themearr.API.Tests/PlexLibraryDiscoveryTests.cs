using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class PlexLibraryDiscoveryTests
{
    private const string Secret = "plex-secret-token";

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Sections() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            <MediaContainer size="2">
              <Directory key="1" type="movie" title="Movies"/>
              <Directory key="2" type="show" title="Television"/>
            </MediaContainer>
            """, Encoding.UTF8, "application/xml"),
    };

    private static (SetupController Controller, Handler Handler) New(TempDir dir,
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        var handler = new Handler(respond ?? (_ => Sections()));
        var plex = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db));
        return (new SetupController(db, plex), handler);
    }

    private static PlexLibrariesRequest Request(string? type = null, int servers = 1)
    {
        var request = new PlexLibrariesRequest { LibraryType = type };
        for (var i = 0; i < servers; i++)
            request.Servers.Add(new Dictionary<string, object?>
            {
                ["id"] = $"server{i}", ["url"] = $"http://plex{i}.local:32400", ["token"] = Secret,
            });
        return request;
    }

    private static JsonElement Json(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    [Theory]
    [InlineData(null, "movie")]
    [InlineData("movie", "movie")]
    [InlineData("show", "show")]
    public async Task Discovers_the_requested_library_type(string? requested, string expected)
    {
        using var dir = new TempDir();
        var (controller, _) = New(dir);

        var json = Json(await controller.PlexLibraries(Request(requested)));
        var library = json.GetProperty("libraries").GetProperty("server0")[0];

        Assert.Equal(expected, library.GetProperty("type").GetString());
        Assert.Equal(expected == "show" ? "Television" : "Movies", library.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Invalid_library_type_is_rejected_before_any_Plex_call()
    {
        using var dir = new TempDir();
        var (controller, handler) = New(dir);

        Assert.IsType<BadRequestObjectResult>(await controller.PlexLibraries(Request("music")));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Multiple_Plex_servers_are_discovered_independently()
    {
        using var dir = new TempDir();
        var (controller, handler) = New(dir);

        var json = Json(await controller.PlexLibraries(Request("show", servers: 2)));

        Assert.Equal(2, json.GetProperty("libraries").EnumerateObject().Count());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Tokens_are_header_only_and_never_returned()
    {
        using var dir = new TempDir();
        var (controller, handler) = New(dir);

        var result = await controller.PlexLibraries(Request("show"));
        var serialized = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value);

        Assert.DoesNotContain(Secret, serialized);
        Assert.DoesNotContain(Secret, handler.Requests.Single().RequestUri!.ToString());
        Assert.Equal(Secret, handler.Requests.Single().Headers.GetValues("X-Plex-Token").Single());
    }

    [Fact]
    public async Task Discovery_errors_do_not_reflect_tokens_or_exception_details()
    {
        using var dir = new TempDir();
        var (controller, _) = New(dir, _ => throw new HttpRequestException($"failed with {Secret}"));

        var result = Assert.IsType<ObjectResult>(await controller.PlexLibraries(Request("show")));
        var serialized = JsonSerializer.Serialize(result.Value);

        Assert.Equal(502, result.StatusCode);
        Assert.DoesNotContain(Secret, serialized);
        Assert.DoesNotContain("failed with", serialized);
    }
}
