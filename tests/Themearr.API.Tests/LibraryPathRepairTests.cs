using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class LibraryPathRepairTests
{
    private static (Database db, LocalFolderResolver resolver) New(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.SetLibraryPaths([dir.Path]);
        return (db, new LocalFolderResolver(db));
    }

    [Fact]
    public void Full_repair_replaces_a_stale_Plex_folder_preserves_theme_status_and_creates_no_duplicate()
    {
        using var dir = new TempDir();
        var (db, resolver) = New(dir);
        var title = "“Wuthering Heights” (2026) [tmdb-1316092]";
        var local = Path.Combine(dir.Path, title); Directory.CreateDirectory(local);
        File.WriteAllBytes(Path.Combine(local, "theme.mp3"), [0x49, 0x44, 0x33, 1]);
        db.SetPathMappings([new() { ["source"] = "/mnt/plex/Movies", ["target"] = dir.Path }]);
        var sourceFile = $"/mnt/plex/Movies/{title}/movie.mkv";
        db.UpsertMovies([new MovieRecord(PlexPath.ParentDir(sourceFile), "plex", "srv:42", title, 2026, sourceFile)]);

        var result = new LibraryPathRepairService(db, resolver).RepairAll();

        Assert.Equal(new PathRepairResult(1, 0, 1, 0), result);
        var movie = Assert.Single(db.GetStoredMovies());
        Assert.Equal(local, movie["folderName"]);
        Assert.Equal("downloaded", Assert.Single(db.GetAllMovies())["status"]);
    }

    [Fact]
    public void Changing_a_mapping_relocates_the_existing_source_identity_on_next_repair()
    {
        using var dir = new TempDir();
        var (db, resolver) = New(dir);
        var firstRoot = Path.Combine(dir.Path, "first");
        var secondRoot = Path.Combine(dir.Path, "second");
        var first = Path.Combine(firstRoot, "Film"); Directory.CreateDirectory(first);
        var second = Path.Combine(secondRoot, "Film"); Directory.CreateDirectory(second);
        var source = "/mnt/plex/Movies/Film/file.mkv";
        db.SetPathMappings([new() { ["source"] = "/mnt/plex/Movies", ["target"] = firstRoot }]);
        db.UpsertMovies([new MovieRecord(first, "plex", "srv:7", "Film", 2020, source)]);

        db.SetPathMappings([new() { ["source"] = "/mnt/plex/Movies", ["target"] = secondRoot }]);
        var result = new LibraryPathRepairService(db, resolver).RepairAll();

        Assert.Equal(1, result.Repaired);
        Assert.Equal(second, Assert.Single(db.GetStoredMovies())["folderName"]);
        Assert.Null(db.GetMovie(MediaFolderId.For(first)));
        Assert.NotNull(db.GetMovie(MediaFolderId.For(second)));
    }

    [Fact]
    public void Removing_a_required_mapping_quarantines_the_old_record_instead_of_authorizing_it()
    {
        using var dir = new TempDir();
        var (db, resolver) = New(dir);
        var mappedRoot = Path.Combine(dir.Path, "nested", "deep");
        var local = Path.Combine(mappedRoot, "Film"); Directory.CreateDirectory(local);
        db.SetSetting("search_depth", "1");
        db.SetPathMappings([new() { ["source"] = "/mnt/plex/Movies", ["target"] = mappedRoot }]);
        db.UpsertMovies([new MovieRecord(local, "plex", "srv:7", "Film", 2020,
            "/mnt/plex/Movies/Film/file.mkv")]);

        db.SetPathMappings([]);
        var result = new LibraryPathRepairService(db, resolver).RepairAll();

        Assert.Equal(1, result.Unresolved);
        Assert.Empty(db.GetStoredMovies());
        Assert.Empty(db.GetPendingMovies());
    }

    [Fact]
    public void Shows_use_the_same_mapping_repair_and_do_not_duplicate()
    {
        using var dir = new TempDir();
        var (db, resolver) = New(dir);
        var local = Path.Combine(dir.Path, "The Wire"); Directory.CreateDirectory(local);
        db.SetPathMappings([new() { ["source"] = "/mnt/plex/Shows", ["target"] = dir.Path }]);
        db.UpsertShows([new ShowRecord("/mnt/plex/Shows/The Wire", "plex", "srv:46", "The Wire", 2002,
            "/mnt/plex/Shows/The Wire", false)]);

        var result = new LibraryPathRepairService(db, resolver).RepairAll();

        Assert.Equal(1, result.Repaired);
        Assert.Equal(local, Assert.Single(db.GetStoredShows())["folderName"]);
        Assert.Single(db.GetAllShows());
    }
}
