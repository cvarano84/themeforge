using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public sealed class ThemeReconciliationServiceTests
{
    private sealed record Library(Database Db, List<string> Folders, List<string> Ids);

    private static Library Build(TempDir temp, int count)
    {
        var db = new Database(Path.Combine(temp.Path, "db", "test.db"));
        db.Init();
        db.SetLibraryPaths([temp.Path]);

        var folders = new List<string>();
        var ids = new List<string>();
        var movies = new List<MovieRecord>();
        for (var i = 0; i < count; i++)
        {
            var folder = Path.Combine(temp.Path, $"movies-{i}", "The Matrix (1999)");
            Directory.CreateDirectory(folder);
            var instance = db.CreateArrInstance("radarr", $"Radarr {i + 1}",
                $"http://radarr-{i + 1}:7878", $"key-{i + 1}", true,
                i == 0 ? "HD" : $"Quality {i + 1}", i, null);
            folders.Add(folder);
            ids.Add(MediaFolderId.For(folder));
            movies.Add(new MovieRecord(folder, "radarr", $"radarr:{instance.Id}:{100 + i}",
                "The Matrix", 1999, folder, instance.Id, (100 + i).ToString(),
                instance.QualityLabel, "603", "tt0133093"));
        }
        db.UpsertMovies(movies);
        return new Library(db, folders, ids);
    }

    private static ThemeReconciliationService Service(Database db) =>
        new(db, NullLogger<ThemeReconciliationService>.Instance);

    private static void Theme(string folder, byte marker = 1) =>
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [0x49, 0x44, 0x33, marker]);

    private static Dictionary<string, object?> Movie(Database db, string id) =>
        db.GetStoredMovie(id)!;

    [Fact]
    public async Task Existing_destination_theme_is_left_untouched()
    {
        using var temp = new TempDir();
        var library = Build(temp, 1);
        Theme(library.Folders[0], marker: 7);
        var before = await File.ReadAllBytesAsync(Path.Combine(library.Folders[0], "theme.mp3"));

        var result = await Service(library.Db).ReconcileMovieAsync(Movie(library.Db, library.Ids[0]));

        Assert.Equal(1, result.Satisfied);
        Assert.Equal(0, result.Copied);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(library.Folders[0], "theme.mp3")));
        Assert.Equal("downloaded", Movie(library.Db, library.Ids[0])["status"]);
    }

    [Fact]
    public async Task Missing_upgrade_destination_copies_from_another_Radarr_location()
    {
        using var temp = new TempDir();
        var library = Build(temp, 2);
        Theme(library.Folders[1], marker: 8);

        var result = await Service(library.Db).ReconcileMovieAsync(Movie(library.Db, library.Ids[0]));

        Assert.Equal(1, result.Copied);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(library.Folders[1], "theme.mp3")),
            await File.ReadAllBytesAsync(Path.Combine(library.Folders[0], "theme.mp3")));
        Assert.Empty(library.Db.GetPendingMovies());
    }

    [Fact]
    public async Task No_existing_copy_leaves_destinations_pending_for_provider_acquisition()
    {
        using var temp = new TempDir();
        var library = Build(temp, 2);
        foreach (var id in library.Ids) library.Db.SetMovieStatus(id, "downloaded");

        var result = await Service(library.Db).ReconcileMovieAsync(Movie(library.Db, library.Ids[0]));

        Assert.Equal(0, result.Copied);
        Assert.Equal(2, result.Missing);
        Assert.Equal(2, library.Db.GetPendingMovies().Count);
    }

    [Fact]
    public async Task Stale_downloaded_database_state_loses_to_the_filesystem()
    {
        using var temp = new TempDir();
        var library = Build(temp, 2);
        Theme(library.Folders[1], marker: 9);
        library.Db.SetMovieStatus(library.Ids[0], "downloaded");
        library.Db.SetMovieStatus(library.Ids[1], "downloaded");

        await Service(library.Db).ReconcileMovieAsync(Movie(library.Db, library.Ids[0]));

        Assert.True(ThemeFiles.FindUsableThemeMp3(library.Folders[0]) is not null);
        Assert.Equal("downloaded", Movie(library.Db, library.Ids[0])["status"]);
    }

    [Fact]
    public async Task One_source_repairs_two_missing_locations_without_acquisition()
    {
        using var temp = new TempDir();
        var library = Build(temp, 3);
        Theme(library.Folders[1], marker: 10);

        var result = await Service(library.Db).ReconcileMovieAsync(Movie(library.Db, library.Ids[0]));

        Assert.Equal(2, result.Copied);
        Assert.All(library.Folders, folder => Assert.NotNull(ThemeFiles.FindUsableThemeMp3(folder)));
        Assert.Empty(library.Db.GetPendingMovies());
    }

    [Fact]
    public async Task Duplicate_reconciliation_is_idempotent_and_copies_once()
    {
        using var temp = new TempDir();
        var library = Build(temp, 2);
        Theme(library.Folders[1], marker: 11);
        var copies = 0;
        var service = new ThemeReconciliationService(library.Db,
            NullLogger<ThemeReconciliationService>.Instance,
            async (source, destination, ct) =>
            {
                Interlocked.Increment(ref copies);
                await Task.Delay(50, ct);
                return await ThemeFiles.CopyAtomicAsync(source, destination, false, ct);
            });
        var movie = Movie(library.Db, library.Ids[0]);

        await Task.WhenAll(
            service.ReconcileMovieAsync(movie),
            service.ReconcileMovieAsync(movie),
            service.ReconcileMovieAsync(movie));

        Assert.Equal(1, copies);
        Assert.NotNull(ThemeFiles.FindUsableThemeMp3(library.Folders[0]));
    }

    [Fact]
    public async Task Copy_failure_keeps_destination_pending_and_source_untouched()
    {
        using var temp = new TempDir();
        var library = Build(temp, 2);
        Theme(library.Folders[1], marker: 12);
        var sourceBefore = await File.ReadAllBytesAsync(Path.Combine(library.Folders[1], "theme.mp3"));
        var service = new ThemeReconciliationService(library.Db,
            NullLogger<ThemeReconciliationService>.Instance,
            (_, _, _) => throw new IOException("simulated copy failure"));

        var result = await service.ReconcileMovieAsync(Movie(library.Db, library.Ids[0]));

        Assert.Equal(1, result.Failed);
        Assert.Null(ThemeFiles.FindUsableThemeMp3(library.Folders[0]));
        Assert.Contains(library.Db.GetPendingMovies(), row => row["id"]?.ToString() == library.Ids[0]);
        Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(Path.Combine(library.Folders[1], "theme.mp3")));
    }

    [Fact]
    public async Task Newly_imported_pending_movie_still_reuses_an_existing_copy()
    {
        using var temp = new TempDir();
        var library = Build(temp, 2);
        Theme(library.Folders[1], marker: 13);
        Assert.Equal("pending", Movie(library.Db, library.Ids[0])["status"]);

        var result = await Service(library.Db).ReconcileMovieAsync(Movie(library.Db, library.Ids[0]));

        Assert.Equal(1, result.Copied);
        Assert.Equal("downloaded", Movie(library.Db, library.Ids[0])["status"]);
    }
}
