using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

public class ShowSourceRoutingTests
{
    private sealed class FakeSource(
        string name, Func<Task<IReadOnlyList<ShowRecord>>> fetch,
        string? blocked = null) : IShowLibrarySource
    {
        public string Name => name;
        public TimeSpan SyncInterval => name == "sonarr" ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(24);
        public string? SyncBlockedReason => blocked;
        public Task<IReadOnlyList<ShowRecord>> FetchAsync(Action<string> log, CancellationToken ct) => fetch();
        public Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct) => Task.FromResult<Stream?>(null);
        public Task<string?> CheckAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task Sonarr_source_is_routed_and_upserted_without_Plex_theme_exclusion()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "Series"); Directory.CreateDirectory(folder);
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init(); db.SetLibraryPaths([dir.Path]);
        db.SetSetting("show_library_source", "sonarr");
        var sonarr = new FakeSource("sonarr", () => Task.FromResult<IReadOnlyList<ShowRecord>>(
            [new ShowRecord(folder, "sonarr", "12", "Series", 2025, folder, false)]));
        var resolver = new ShowSourceResolver(db, [sonarr, new DisabledShowLibrarySource()]);

        var count = await new ShowSyncService(db, resolver, NullLogger<ShowSyncService>.Instance)
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, count);
        var show = Assert.Single(db.GetAllShows());
        Assert.Equal("pending", show["status"]);
        Assert.False((bool)show["plexHasTheme"]!);
    }

    [Fact]
    public async Task Disabled_source_is_a_clean_no_op_and_keeps_existing_rows()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "Existing"); Directory.CreateDirectory(folder);
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init(); db.SetLibraryPaths([dir.Path]);
        db.UpsertShows([new ShowRecord(folder, "sonarr", "1", "Existing", 2020, folder, false)]);
        db.SetSetting("show_library_source", "disabled");
        var resolver = new ShowSourceResolver(db, [new DisabledShowLibrarySource()]);

        var count = await new ShowSyncService(db, resolver, NullLogger<ShowSyncService>.Instance)
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Single(db.GetAllShows());
    }

    [Fact]
    public async Task Failed_sync_does_not_prune_current_library()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "Existing"); Directory.CreateDirectory(folder);
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init(); db.SetLibraryPaths([dir.Path]);
        db.UpsertShows([new ShowRecord(folder, "sonarr", "1", "Existing", 2020, folder, false)]);
        db.SetSetting("show_library_source", "sonarr");
        var failing = new FakeSource("sonarr", () => throw new InvalidOperationException("clean failure"));
        var resolver = new ShowSourceResolver(db, [failing, new DisabledShowLibrarySource()]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ShowSyncService(db, resolver, NullLogger<ShowSyncService>.Instance)
                .RunOnceAsync(CancellationToken.None));

        Assert.Single(db.GetAllShows());
    }

    [Fact]
    public async Task Source_switch_during_fetch_discards_result_and_never_prunes_new_source()
    {
        using var dir = new TempDir();
        var existing = Path.Combine(dir.Path, "Existing"); Directory.CreateDirectory(existing);
        var incoming = Path.Combine(dir.Path, "Incoming"); Directory.CreateDirectory(incoming);
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init(); db.SetLibraryPaths([dir.Path]);
        db.UpsertShows([new ShowRecord(existing, "sonarr", "1", "Existing", 2020, existing, false)]);
        db.SetSetting("show_library_source", "plex");
        db.SetSelectedShowLibraries(new() { ["server"] = ["tv"] });
        var plex = new FakeSource("plex", () =>
        {
            db.SetSetting("show_library_source", "sonarr");
            return Task.FromResult<IReadOnlyList<ShowRecord>>(
                [new ShowRecord(incoming, "plex", "server:2", "Incoming", 2024, incoming, false)]);
        });
        var sonarr = new FakeSource("sonarr", () => Task.FromResult<IReadOnlyList<ShowRecord>>([]));
        var resolver = new ShowSourceResolver(db, [plex, sonarr, new DisabledShowLibrarySource()]);

        var count = await new ShowSyncService(db, resolver, NullLogger<ShowSyncService>.Instance)
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, count);
        var only = Assert.Single(db.GetAllShows());
        Assert.Equal("Existing", only["title"]);
    }
}
