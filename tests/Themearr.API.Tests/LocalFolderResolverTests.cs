using System.Globalization;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class LocalFolderResolverTests
{
    private static (LocalFolderResolver Resolver, Database Db) New(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        return (new LocalFolderResolver(db), db);
    }

    [Fact]
    public void A_path_that_exists_resolves_directly()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var movieDir = Path.Combine(dir.Path, "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        db.SetLibraryPaths([dir.Path]);

        var (folder, mode) = resolver.Resolve(Path.Combine(movieDir, "heat.mkv"));

        Assert.Equal(movieDir, folder);
        Assert.Equal("direct", mode);
    }

    [Fact]
    public void A_configured_mapping_is_applied_when_the_reported_path_does_not_exist()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var movieDir = Path.Combine(dir.Path, "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        db.SetLibraryPaths([dir.Path]);
        db.SetPathMappings([new Dictionary<string, string>
        {
            ["source"] = "/mnt/plex/Movies",
            ["target"] = dir.Path,
        }]);

        var (folder, mode) = resolver.Resolve("/mnt/plex/Movies/Heat (1995)/heat.mkv");

        Assert.Equal(movieDir, folder);
        Assert.Equal("mapping", mode);
    }

    [Fact]
    public void A_windows_style_path_is_mapped_despite_backslashes()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var movieDir = Path.Combine(dir.Path, "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        db.SetLibraryPaths([dir.Path]);
        db.SetPathMappings([new Dictionary<string, string>
        {
            ["source"] = @"P:\Movies",
            ["target"] = dir.Path,
        }]);

        var (folder, mode) = resolver.Resolve(@"P:\Movies\Heat (1995)\heat.mkv");

        Assert.Equal(movieDir, folder);
        Assert.Equal("mapping", mode);
    }

    [Fact]
    public void With_no_mapping_the_folder_is_found_by_suffix_under_a_library_path()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var movieDir = Path.Combine(dir.Path, "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        db.SetLibraryPaths([dir.Path]);

        var (folder, mode) = resolver.Resolve("/somewhere/else/Heat (1995)/heat.mkv");

        Assert.Equal(movieDir, folder);
        Assert.Equal("suffix", mode);
    }

    [Fact]
    public void The_longest_matching_suffix_wins_over_a_shorter_one()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var topLevelDir = Path.Combine(dir.Path, "Heat (1995)");
        var nestedDir = Path.Combine(dir.Path, "Movies", "Heat (1995)");
        Directory.CreateDirectory(topLevelDir);
        Directory.CreateDirectory(nestedDir);
        db.SetLibraryPaths([dir.Path]);

        var (folder, mode) = resolver.Resolve("/somewhere/Movies/Heat (1995)/heat.mkv");

        Assert.Equal(nestedDir, folder);
        Assert.Equal("suffix", mode);
    }

    [Fact]
    public void A_deeply_nested_folder_is_found_by_the_directory_scan()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var movieDir = Path.Combine(dir.Path, "a", "b", "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        db.SetLibraryPaths([dir.Path]);

        var (folder, mode) = resolver.Resolve("/somewhere/Heat (1995)/heat.mkv");

        Assert.Equal(movieDir, folder);
        Assert.Equal("suffix", mode);
    }

    [Fact]
    public void An_unknown_path_with_nothing_configured_is_unresolved()
    {
        using var dir = new TempDir();
        var (resolver, _) = New(dir);

        var (folder, mode) = resolver.Resolve("/mnt/nowhere/Heat (1995)/heat.mkv");

        Assert.Equal("", folder);
        Assert.Equal("unresolved", mode);
    }

    [Fact]
    public void An_empty_path_is_unresolved_rather_than_throwing()
    {
        using var dir = new TempDir();
        var (resolver, _) = New(dir);

        var (folder, mode) = resolver.Resolve("");

        Assert.Equal("", folder);
        Assert.Equal("unresolved", mode);
    }

    [Fact]
    public void The_depth_limit_is_measured_the_same_whether_the_library_path_has_a_trailing_slash()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        // A movie two directory levels below the library root.
        var movieDir = Path.Combine(dir.Path, "sub", "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        // Depth budget of 1: a folder two levels down must NOT be reachable by the scan.
        db.SetSetting("search_depth", "1");
        // Stored WITH a trailing slash, exactly as a user might type the path.
        db.SetLibraryPaths([dir.Path + Path.DirectorySeparatorChar]);

        // A source path whose suffix can't locate the folder (it's under 'sub'),
        // forcing the depth-limited directory scan.
        var (folder, mode) = resolver.Resolve("/plex/Heat (1995)/heat.mkv");

        // The folder is genuinely 2 levels deep, past the depth-1 budget, so it
        // must be unresolved regardless of the trailing slash. The bug lets the
        // slash swallow a separator so it counts as depth 1 and wrongly matches.
        Assert.Equal("", folder);
        Assert.Equal("unresolved", mode);
    }

    [Fact]
    public void Folder_name_matching_is_case_insensitive_even_under_a_non_invariant_culture()
    {
        var original = CultureInfo.CurrentCulture;
        // Turkish lowercases ASCII 'I' to dotless 'ı', so a culture-sensitive
        // ToLower() would fail to match a lowercase source segment.
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            using var dir = new TempDir();
            var (resolver, db) = New(dir);
            var movieDir = Path.Combine(dir.Path, "sub", "TITANIC (1997)");
            Directory.CreateDirectory(movieDir);
            db.SetLibraryPaths([dir.Path]);

            // Under 'sub' so suffix can't find it -> the name-matching scan runs.
            var (folder, mode) = resolver.Resolve("/plex/titanic (1997)/movie.mkv");

            Assert.Equal(movieDir, folder);
            Assert.Equal("suffix", mode);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void Docker_host_path_maps_to_container_root_and_preserves_unicode()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var movie = Path.Combine(dir.Path, "“Wuthering Heights” (2026) [tmdb-1316092]");
        Directory.CreateDirectory(movie);
        db.SetLibraryPaths([dir.Path]);
        db.SetPathMappings([new() { ["source"] = "/mnt/plex/Movies", ["target"] = dir.Path }]);

        var result = resolver.ResolveDetailed(
            "/mnt/plex/Movies/“Wuthering Heights” (2026) [tmdb-1316092]/movie.mkv");

        Assert.Equal("mapping", result.ResolutionMode);
        Assert.Equal(movie, result.ResolvedFolderPath);
        Assert.Contains("“Wuthering Heights”", result.ResolvedFolderPath);
    }

    [Fact]
    public void Longest_segment_aware_mapping_prefix_wins()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var general = Path.Combine(dir.Path, "general");
        var specific = Path.Combine(dir.Path, "specific");
        Directory.CreateDirectory(Path.Combine(general, "Movies", "Film"));
        Directory.CreateDirectory(Path.Combine(specific, "Film"));
        db.SetLibraryPaths([dir.Path]);
        db.SetPathMappings([
            new() { ["source"] = "/mnt/plex", ["target"] = general },
            new() { ["source"] = "/mnt/plex/Movies", ["target"] = specific },
        ]);

        var (folder, mode) = resolver.Resolve("/mnt/plex/Movies/Film/file.mkv");

        Assert.Equal(Path.Combine(specific, "Film"), folder);
        Assert.Equal("mapping", mode);
    }

    [Fact]
    public void Mapping_prefix_does_not_match_a_sibling_name()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        db.SetLibraryPaths([dir.Path]);
        db.SetPathMappings([new() { ["source"] = "/mnt/plex/Movies", ["target"] = dir.Path }]);

        var result = resolver.ResolveDetailed("/mnt/plex/MoviesBackup/Film/file.mkv");

        Assert.Equal("unresolved", result.ResolutionMode);
        Assert.Null(result.MatchedMapping);
    }

    [Fact]
    public void Source_and_target_trailing_separators_are_normalized()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var movie = Path.Combine(dir.Path, "Film"); Directory.CreateDirectory(movie);
        db.SetLibraryPaths([dir.Path + Path.DirectorySeparatorChar]);
        db.SetPathMappings([new()
        {
            ["source"] = "/mnt/plex/Movies/",
            ["target"] = dir.Path + Path.DirectorySeparatorChar,
        }]);

        Assert.Equal(movie, resolver.Resolve("/mnt/plex/Movies/Film/file.mkv").folder);
    }

    [Fact]
    public void A_valid_mapping_is_preferred_even_when_the_source_folder_exists_directly()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var sourceRoot = Path.Combine(dir.Path, "source");
        var targetRoot = Path.Combine(dir.Path, "target");
        var sourceMovie = Path.Combine(sourceRoot, "Film"); Directory.CreateDirectory(sourceMovie);
        var targetMovie = Path.Combine(targetRoot, "Film"); Directory.CreateDirectory(targetMovie);
        db.SetLibraryPaths([sourceRoot, targetRoot]);
        db.SetPathMappings([new() { ["source"] = sourceRoot, ["target"] = targetRoot }]);

        var result = resolver.ResolveDetailed(Path.Combine(sourceMovie, "file.mkv"));

        Assert.Equal("mapping", result.ResolutionMode);
        Assert.Equal(targetMovie, result.ResolvedFolderPath);
    }

    [Fact]
    public void Dot_dot_in_a_mapped_suffix_cannot_escape_the_mapping_target()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var target = Path.Combine(dir.Path, "movies"); Directory.CreateDirectory(target);
        db.SetLibraryPaths([dir.Path]);
        db.SetPathMappings([new() { ["source"] = "/mnt/plex/Movies", ["target"] = target }]);

        var result = resolver.ResolveDetailed("/mnt/plex/Movies/../../escape/file.mkv");

        Assert.Equal("unresolved", result.ResolutionMode);
        Assert.False(result.CandidateWithinRoots);
    }

    [Fact]
    public void Mapping_target_outside_roots_is_rejected_by_validation()
    {
        using var root = new TempDir();
        using var outside = new TempDir();
        var (resolver, _) = New(root);

        var validation = resolver.ValidateConfiguration(
            [new() { ["source"] = "/mnt/plex/Movies", ["target"] = outside.Path }], [root.Path]);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("beneath", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_mapping_target_and_missing_root_are_reported_clearly()
    {
        using var dir = new TempDir();
        var (resolver, _) = New(dir);
        var missingRoot = Path.Combine(dir.Path, "missing-root");
        var missingTarget = Path.Combine(dir.Path, "missing-target");

        var validation = resolver.ValidateConfiguration(
            [new() { ["source"] = "/mnt/plex/Movies", ["target"] = missingTarget }], [missingRoot]);

        Assert.Contains(validation.Errors, e => e.Contains("Library root", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, e => e.Contains("Mapping target", StringComparison.Ordinal));
        Assert.All(validation.Errors, e => Assert.Contains("exist", e, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Duplicate_sources_are_rejected_after_separator_normalization()
    {
        using var dir = new TempDir();
        var (resolver, _) = New(dir);
        var validation = resolver.ValidateConfiguration([
            new() { ["source"] = "/mnt/plex/Movies", ["target"] = dir.Path },
            new() { ["source"] = "/mnt/plex/Movies/", ["target"] = dir.Path },
        ], [dir.Path]);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("Duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void A_matched_mapping_with_a_missing_movie_folder_reports_the_candidate_without_resolving()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        db.SetLibraryPaths([dir.Path]);
        db.SetPathMappings([new() { ["source"] = "/mnt/plex/Movies", ["target"] = dir.Path }]);

        var result = resolver.ResolveDetailed("/mnt/plex/Movies/Missing/file.mkv");

        Assert.Equal("unresolved", result.ResolutionMode);
        Assert.False(result.CandidateExists);
        Assert.Equal(Path.Combine(dir.Path, "Missing"), result.MappedCandidate);
        Assert.Contains("does not exist", result.FailureReason);
    }
}
