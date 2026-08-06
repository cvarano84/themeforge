using Themearr.API.Data;

namespace Themearr.API.Tests;

public class TrustedLibraryRootsTests
{
    [Fact]
    public void Trusted_roots_include_only_explicit_local_library_paths()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "themearr.db"));
        db.Init();
        db.SetLibraryPaths(["/local/library"]);
        db.SetPathMappings([new Dictionary<string, string>
        {
            ["source"] = "/remote/plex",
            ["target"] = "/mapped/library",
        }]);

        var roots = db.GetTrustedLibraryRoots();

        Assert.Contains("/local/library", roots);
        Assert.DoesNotContain("/mapped/library", roots);
        Assert.DoesNotContain("/remote/plex", roots);
    }
}
