using Microsoft.Data.Sqlite;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

public class SourceSettingsMigrationTests
{
    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler());
        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                throw new InvalidOperationException("network not expected");
        }
    }

    private static void SeedSettings(string path, params (string Key, string Value)[] settings)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
        create.ExecuteNonQuery();
        foreach (var (key, value) in settings)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO settings (key, value) VALUES ($key, $value)";
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$value", value);
            insert.ExecuteNonQuery();
        }
    }

    [Theory]
    [InlineData("plex")]
    [InlineData("radarr")]
    public void Legacy_movie_source_is_migrated(string legacy)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "test.db");
        SeedSettings(path, ("library_source", legacy));

        var db = new Database(path); db.Init();

        Assert.Equal(legacy, db.GetMovieLibrarySource());
        Assert.Equal(legacy, db.GetSetting("library_source", ""));
    }

    [Fact]
    public void Selected_Plex_show_libraries_initialize_show_source_to_Plex()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "test.db");
        SeedSettings(path, ("plex_selected_show_libraries", "{\"server\":[\"7\"]}"));

        var db = new Database(path); db.Init();

        Assert.Equal("plex", db.GetShowLibrarySource());
    }

    [Fact]
    public void Existing_explicit_show_source_is_preserved()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "test.db");
        SeedSettings(path,
            ("show_library_source", "sonarr"),
            ("plex_selected_show_libraries", "{\"server\":[\"7\"]}"));

        var db = new Database(path); db.Init();

        Assert.Equal("sonarr", db.GetShowLibrarySource());
    }

    [Theory]
    [InlineData("plex", "plex")]
    [InlineData("plex", "sonarr")]
    [InlineData("plex", "disabled")]
    [InlineData("radarr", "plex")]
    [InlineData("radarr", "sonarr")]
    [InlineData("radarr", "disabled")]
    [InlineData("disabled", "plex")]
    [InlineData("disabled", "sonarr")]
    public void Every_supported_movie_show_combination_is_independent(string movie, string show)
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init();

        db.SetSetting("movie_library_source", movie);
        db.SetSetting("show_library_source", show);

        Assert.Equal(movie, db.GetMovieLibrarySource());
        Assert.Equal(show, db.GetShowLibrarySource());
    }

    [Fact]
    public void Saving_movie_source_does_not_change_show_source_and_vice_versa()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init();
        db.SetSetting("show_library_source", "sonarr");
        db.SetSetting("sonarr_url", "http://sonarr:8989");
        db.SetSetting("sonarr_api_key", "show-key");
        var factory = new Factory();
        var plexService = new PlexService(new HttpClient(), db, new LocalFolderResolver(db));
        var controller = new SettingsController(
            db,
            new RadarrLibrarySource(db, new LocalFolderResolver(db), factory),
            new SonarrShowLibrarySource(db, new LocalFolderResolver(db), factory),
            new PlexLibrarySource(plexService, db, factory),
            new ApiKeyStore(db));

        controller.SaveRadarr(new SettingsController.RadarrPayload("radarr", "http://radarr:7878", "movie-key"));
        Assert.Equal("sonarr", db.GetShowLibrarySource());

        controller.SaveSonarr(new SettingsController.SonarrPayload("plex", null, null));
        Assert.Equal("radarr", db.GetMovieLibrarySource());
    }
}
