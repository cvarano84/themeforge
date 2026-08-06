using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;

namespace Themearr.API.Services.Health;

/// <summary>
/// Catches the misconfiguration that silently breaks every download: a library path
/// that is missing, read-only, or unreachable from the paths Plex reports.
/// </summary>
public sealed class LibraryPathsCheck(Database db) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Before setup there is nothing configured yet; a fresh install is not broken.
        if (!db.IsSetupComplete())
            return Task.FromResult(HealthCheckResult.Healthy("Setup not complete"));

        var paths = db.GetTrustedLibraryRoots();
        if (paths.Count == 0)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"No library paths are configured — {ProductBrand.Name} has nowhere to write theme.mp3. " +
                "Add one under Settings → Local Library Paths."));

        foreach (var path in paths)
        {
            if (!Directory.Exists(path))
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Library path {path} does not exist. Check the mount is present inside {ProductBrand.Name}."));

            if (!ThemeFiles.IsDirectoryWritable(path))
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Library path {path} is not writable — every download will fail silently. " +
                    "Check the mount is not read-only and that the themearr user can write to it."));
        }

        var resolver = new LocalFolderResolver(db);
        var mappingValidation = resolver.ValidateConfiguration(db.GetPathMappings(), paths);
        if (!mappingValidation.IsValid)
            return Task.FromResult(HealthCheckResult.Unhealthy(mappingValidation.Errors[0]));

        var movies = db.GetStoredMovies();
        var shows = db.GetStoredShows();
        var invalidStored = movies.Concat(shows).Take(1000)
            .FirstOrDefault(row =>
            {
                var folder = row["folderName"]?.ToString() ?? "";
                return string.IsNullOrEmpty(folder) || !Directory.Exists(folder)
                    || !ThemeFiles.IsWithinRoots(folder, paths);
            });
        if (invalidStored is not null)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "A recently synchronized media record has an invalid or outside-root local folder. " +
                "Run a full sync or the authenticated path repair operation."));

        var staleMapped = movies.Select(row => (row, folderSource: row["source"]?.ToString() == "radarr"))
            .Concat(shows.Select(row => (row, folderSource: true)))
            .Take(100)
            .FirstOrDefault(entry =>
            {
                var sourcePath = entry.row["sourcePath"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(sourcePath)) return false;
                var sourceFolder = entry.folderSource
                    ? PlexPath.NormalizeRoot(sourcePath) : PlexPath.ParentDir(sourcePath);
                if (!db.GetPathMappings().Any(mapping => !string.IsNullOrEmpty(PlexPath.ApplyMapping(
                        sourceFolder, mapping.GetValueOrDefault("source", ""),
                        mapping.GetValueOrDefault("target", ""))))) return false;
                var resolved = resolver.ResolveStoredSource(sourcePath,
                    entry.row["source"]?.ToString(), entry.folderSource,
                    entry.row.GetValueOrDefault("instanceId")?.ToString());
                return resolved.ResolvedFolderPath is not null
                    && !string.Equals(Path.GetFullPath(entry.row["folderName"]?.ToString() ?? ""),
                        resolved.ResolvedFolderPath,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            });
        if (staleMapped.row is not null)
            return Task.FromResult(HealthCheckResult.Degraded(
                "A mapped source path is still stored as a stale local folder. Run a full sync or path repair."));

        var unresolved = int.TryParse(db.GetSetting("last_sync_unresolved_count", "0"), out var n) ? n : 0;
        if (unresolved > 0)
        {
            var sample = db.GetSetting("last_sync_unresolved_sample", "");
            var message = $"{unresolved} movies could not be resolved to a local path — check Path Mappings.";
            if (!string.IsNullOrEmpty(sample)) message += $" Example: {sample}";
            return Task.FromResult(HealthCheckResult.Degraded(message));
        }


        var unresolvedShows = int.TryParse(db.GetSetting("last_show_sync_unresolved_count", "0"), out var sn) ? sn : 0;
        if (unresolvedShows > 0)
            return Task.FromResult(HealthCheckResult.Degraded(
                $"{unresolvedShows} shows could not be resolved to a local path — check Path Mappings."));

        return Task.FromResult(HealthCheckResult.Healthy(
            $"{paths.Count} library path(s) present and writable"));
    }
}
