using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public sealed class MultiQualityDownloadTests
{
    private sealed class Provider(bool block = false) : IThemeAudioProvider
    {
        public int Calls;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DownloaderDiagnostics> CheckConfigurationAsync(bool forceRefresh = false, CancellationToken ct = default) =>
            TestDownloaderDiagnostics.Ready();
        public async Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (block) await Release.Task.WaitAsync(ct);
            File.WriteAllBytes(outputPath, [0x49, 0x44, 0x33, 1, 2, 3]);
            return "Theme";
        }
    }

    private sealed class Factory : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(); }

    private static (DownloadService Service, Database Db, Provider Provider, string FirstId, string A, string B)
        Build(TempDir dir, bool existingSecond = false, bool block = false)
    {
        var db = new Database(Path.Combine(dir.Path, "db", "test.db")); db.Init();
        var aFolder = Path.Combine(dir.Path, "movies", "Matrix"); Directory.CreateDirectory(aFolder);
        var bFolder = Path.Combine(dir.Path, "movies4k", "Matrix"); Directory.CreateDirectory(bFolder);
        db.SetLibraryPaths([dir.Path]);
        var a = db.CreateArrInstance("radarr", "Movies", "http://a:7878", "a", true, "1080p", 10, null);
        var b = db.CreateArrInstance("radarr", "Movies 4K", "http://b:7878", "b", true, "4K", 0, null);
        db.UpsertMovies([
            new MovieRecord(aFolder, "radarr", $"radarr:{a.Id}:1", "The Matrix", 1999, aFolder,
                a.Id, "1", "1080p", "603", "tt0133093"),
            new MovieRecord(bFolder, "radarr", $"radarr:{b.Id}:1", "The Matrix", 1999, bFolder,
                b.Id, "1", "4K", "603", "tt0133093")]);
        if (existingSecond) File.WriteAllBytes(Path.Combine(bFolder, "theme.mp3"), [0x49, 0x44, 0x33, 9]);
        var provider = new Provider(block);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Themearr:DownloadTimeoutSeconds"] = "10" }).Build();
        return (new DownloadService(provider, db, new Factory(), config,
            NullLogger<DownloadService>.Instance), db, provider, MediaFolderId.For(aFolder), aFolder, bFolder);
    }

    private static async Task Wait(DownloadService service, string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var status = service.GetStatus(id);
            if ((bool)status.GetType().GetProperty("finished")!.GetValue(status)!) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("download did not finish");
    }

    [Fact]
    public async Task One_provider_download_installs_every_quality_atomically()
    {
        using var dir = new TempDir();
        var (service, db, provider, id, a, b) = Build(dir);
        Assert.True(service.Start(id, "https://www.youtube.com/watch?v=abc12345678"));
        await Wait(service, id);

        Assert.Equal(1, provider.Calls);
        Assert.True(ThemeFiles.HasUsableTheme(a));
        Assert.True(ThemeFiles.HasUsableTheme(b));
        Assert.False(File.Exists(Path.Combine(a, "theme.mp3.part")));
        Assert.False(File.Exists(Path.Combine(b, "theme.mp3.part")));
        var history = Assert.Single(db.GetThemeHistory());
        Assert.NotNull(history["installationResults"]);
    }

    [Fact]
    public async Task Existing_quality_theme_is_reused_without_provider_download()
    {
        using var dir = new TempDir();
        var (service, _, provider, id, a, b) = Build(dir, existingSecond: true);
        Assert.True(service.Start(id, "https://www.youtube.com/watch?v=abc12345678"));
        await Wait(service, id);

        Assert.Equal(0, provider.Calls);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(b, "theme.mp3")),
            await File.ReadAllBytesAsync(Path.Combine(a, "theme.mp3")));
    }

    [Fact]
    public void Group_lock_blocks_duplicate_jobs_for_the_same_logical_item()
    {
        using var dir = new TempDir();
        var (service, db, provider, id, _, _) = Build(dir, block: true);
        var secondId = db.GetStoredMovies().Single(row => row["id"]?.ToString() != id)["id"]!.ToString()!;
        Assert.True(service.Start(id, "https://www.youtube.com/watch?v=abc12345678"));
        Assert.False(service.Start(secondId, "https://www.youtube.com/watch?v=abc12345678"));
        provider.Release.TrySetResult();
    }
}
