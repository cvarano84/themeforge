using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class PathMappingEndpointTests
{
    private sealed class Keys : IApiKeyStore
    {
        public string Current => "test";
        public string Regenerate() => "test";
    }

    [Fact]
    public void Test_mapping_returns_full_diagnostics_and_performs_no_file_write()
    {
        using var dir = new TempDir();
        var mediaRoot = Path.Combine(dir.Path, "media");
        var movie = Path.Combine(mediaRoot, "Movie"); Directory.CreateDirectory(movie);
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init();
        var controller = TestControllers.NewSettingsController(db, new Keys());
        var mappings = new List<Dictionary<string, string>>
        {
            new() { ["source"] = "/mnt/plex/Movies", ["target"] = mediaRoot },
        };
        var before = Directory.GetFiles(mediaRoot, "*", SearchOption.AllDirectories);

        var response = Assert.IsType<OkObjectResult>(controller.TestPathMapping(new(
            "/mnt/plex/Movies/Movie/movie.mkv", false, mappings, [mediaRoot])));
        var result = Assert.IsType<PathResolutionResult>(response.Value);

        Assert.Equal("/mnt/plex/Movies/Movie", result.SourceFolderPath);
        Assert.Equal(mediaRoot, result.MatchedMapping!["target"]);
        Assert.Equal(movie, result.MappedCandidate);
        Assert.True(result.CandidateExists);
        Assert.True(result.CandidateWithinRoots);
        Assert.Equal("mapping", result.ResolutionMode);
        Assert.Equal(movie, result.ResolvedFolderPath);
        Assert.Equal(before, Directory.GetFiles(mediaRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Invalid_mapping_is_rejected_before_settings_are_saved()
    {
        using var dir = new TempDir();
        using var outside = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init();
        var controller = TestControllers.NewSettingsController(db, new Keys());
        var payload = new SettingsPayload
        {
            LibraryPaths = [dir.Path],
            PathMappings = [new() { ["source"] = "/mnt/plex/Movies", ["target"] = outside.Path }],
        };

        Assert.IsType<BadRequestObjectResult>(controller.Save(payload));
        Assert.Empty(db.GetLibraryPaths());
        Assert.Empty(db.GetPathMappings());
    }
}
