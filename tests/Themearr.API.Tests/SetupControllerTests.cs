using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Exercises <see cref="SetupController"/>'s status and non-Plex completion endpoints.
/// A Radarr install never selects Plex libraries, so <c>setupComplete</c> must not
/// depend on the Plex library count for that source — see the regression this guards
/// against: a Radarr install could complete setup server-side but the UI would send it
/// back to the wizard forever because /api/setup/status kept reporting incomplete.
/// </summary>
public class SetupControllerTests
{
    private static (SetupController Controller, Database Db) New(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        var plex = new PlexService(new HttpClient(), db, new LocalFolderResolver(db));
        return (new SetupController(db, plex), db);
    }

    private static bool SetupComplete(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return (bool)ok.Value!.GetType().GetProperty("setupComplete")!.GetValue(ok.Value)!;
    }

    // ── Status: setupComplete ────────────────────────────────────────────────

    [Fact]
    public void Status_for_plex_source_with_selected_libraries_is_complete()
    {
        using var dir = new TempDir();
        var (controller, db) = New(dir);

        db.SetSetting("movie_library_source", "plex");
        db.MarkSetupComplete();
        db.SetSelectedLibraries(new Dictionary<string, List<string>> { ["server1"] = ["lib1"] });

        Assert.True(SetupComplete(controller.Status()));
    }

    [Fact]
    public void Status_for_plex_source_with_no_selected_libraries_is_incomplete()
    {
        using var dir = new TempDir();
        var (controller, db) = New(dir);

        db.SetSetting("movie_library_source", "plex");
        db.MarkSetupComplete();
        // No selected libraries at all — the Plex branch must still require them.

        Assert.False(SetupComplete(controller.Status()));
    }

    [Fact]
    public void Status_for_radarr_source_with_flag_set_and_no_plex_libraries_is_complete()
    {
        using var dir = new TempDir();
        var (controller, db) = New(dir);

        db.SetSetting("movie_library_source", "radarr");
        db.MarkSetupComplete();
        // Deliberately no selected Plex libraries — a Radarr install never has any.

        Assert.True(SetupComplete(controller.Status()));
    }

    [Fact]
    public void Status_for_radarr_source_without_the_flag_is_incomplete()
    {
        using var dir = new TempDir();
        var (controller, db) = New(dir);

        db.SetSetting("movie_library_source", "radarr");
        // setup_complete never marked.

        Assert.False(SetupComplete(controller.Status()));
    }

    // ── POST /api/setup/complete ─────────────────────────────────────────────

    [Fact]
    public void Complete_is_rejected_when_source_is_plex()
    {
        using var dir = new TempDir();
        var (controller, db) = New(dir);

        db.SetSetting("movie_library_source", "plex");
        db.SetSetting("radarr_url", "http://localhost:7878");
        db.SetSetting("radarr_api_key", "key");

        var result = Assert.IsType<BadRequestObjectResult>(controller.Complete());

        Assert.False(db.IsSetupComplete());
        var detail = (string)result.Value!.GetType().GetProperty("detail")!.GetValue(result.Value)!;
        Assert.Contains("non-Plex", detail);
    }

    [Fact]
    public void Complete_is_rejected_when_radarr_is_unconfigured()
    {
        using var dir = new TempDir();
        var (controller, db) = New(dir);

        db.SetSetting("movie_library_source", "radarr");
        // radarr_url / radarr_api_key left unset.

        Assert.IsType<BadRequestObjectResult>(controller.Complete());
        Assert.False(db.IsSetupComplete());
    }

    [Fact]
    public void Complete_succeeds_when_radarr_is_configured()
    {
        using var dir = new TempDir();
        var (controller, db) = New(dir);

        db.SetSetting("movie_library_source", "radarr");
        db.SetSetting("radarr_url", "http://localhost:7878");
        db.SetSetting("radarr_api_key", "key");
        db.SetLibraryPaths([dir.Path]);

        var result = Assert.IsType<OkObjectResult>(controller.Complete());

        Assert.True(db.IsSetupComplete());
        var complete = (bool)result.Value!.GetType().GetProperty("setupComplete")!.GetValue(result.Value)!;
        Assert.True(complete);

        // And the status endpoint now agrees — this is the end-to-end proof that a
        // Radarr install stops bouncing back to /setup after completing it.
        Assert.True(SetupComplete(controller.Status()));
    }

    [Fact]
    public void Plex_TV_only_selection_persists_and_completes_setup()
    {
        using var dir = new TempDir();
        var (controller, db) = New(dir);
        var request = new PlexSelectionRequest
        {
            Servers = [new Dictionary<string, object?>
            {
                ["id"] = "server1", ["name"] = "Plex", ["url"] = "http://plex:32400",
                ["token"] = "write-only-token",
            }],
            SelectedLibraries = [],
            SelectedShowLibraries = new() { ["server1"] = ["tv"] },
            LibraryPaths = [dir.Path],
        };

        var result = controller.SaveSelection(request);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(db.IsSetupComplete());
        Assert.Equal("disabled", db.GetMovieLibrarySource());
        Assert.Equal("plex", db.GetShowLibrarySource());
        Assert.Equal(["tv"], db.GetSelectedShowLibraries()["server1"]);
        Assert.True(SetupComplete(controller.Status()));
    }

    [Fact]
    public void Sonarr_only_installation_can_complete_without_Plex()
    {
        using var dir = new TempDir();
        var (controller, db) = New(dir);
        db.SetSetting("movie_library_source", "disabled");
        db.SetSetting("show_library_source", "sonarr");
        db.SetSetting("sonarr_url", "http://sonarr:8989");
        db.SetSetting("sonarr_api_key", "write-only-key");
        db.SetLibraryPaths([dir.Path]);

        Assert.IsType<OkObjectResult>(controller.Complete());
        Assert.True(SetupComplete(controller.Status()));
    }
}
