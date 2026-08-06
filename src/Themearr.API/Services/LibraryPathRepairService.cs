using Themearr.API.Data;

namespace Themearr.API.Services;

public sealed record PathRepairResult(int Examined, int Unchanged, int Repaired, int Unresolved);

/// <summary>
/// Re-resolves persisted source paths with the current roots and mappings. Rows that
/// can no longer be resolved are removed from the writable media tables; the source
/// will offer them again on a later full sync after configuration is fixed.
/// </summary>
public class LibraryPathRepairService(Database db, LocalFolderResolver folders)
{
    public PathRepairResult RepairAll(Action<string>? log = null)
    {
        var examined = 0;
        var unchanged = 0;
        var repaired = 0;
        var unresolved = 0;
        var examples = 0;

        foreach (var row in db.GetStoredMovies())
        {
            examined++;
            var sourcePath = row["sourcePath"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(sourcePath) && StoredFolderIsSafe(row))
            {
                unchanged++;
                continue;
            }
            var result = folders.ResolveStoredSource(sourcePath, row["source"]?.ToString(), isShow: false,
                row.GetValueOrDefault("instanceId")?.ToString());
            if (result.ResolvedFolderPath is null)
            {
                unresolved++;
                db.RemoveMovie((string)row["id"]!);
                LogExample("movie", row, result);
                continue;
            }

            if (SameLocalPath(row["folderName"]?.ToString(), result.ResolvedFolderPath))
            {
                unchanged++;
                continue;
            }

            db.UpsertMovies([new MovieRecord(
                result.ResolvedFolderPath,
                row["source"]?.ToString() ?? "plex",
                row["sourceRef"]?.ToString() ?? "",
                row["title"]?.ToString() ?? "",
                row["year"] as int?,
                sourcePath,
                row.GetValueOrDefault("instanceId")?.ToString(),
                row.GetValueOrDefault("remoteItemId")?.ToString(),
                row.GetValueOrDefault("qualityLabel")?.ToString(),
                row.GetValueOrDefault("tmdbId")?.ToString(),
                row.GetValueOrDefault("imdbId")?.ToString())]);
            repaired++;
        }

        foreach (var row in db.GetStoredShows())
        {
            examined++;
            var sourcePath = row["sourcePath"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(sourcePath) && StoredFolderIsSafe(row))
            {
                unchanged++;
                continue;
            }
            var result = folders.ResolveStoredSource(sourcePath, row["source"]?.ToString(), isShow: true,
                row.GetValueOrDefault("instanceId")?.ToString());
            if (result.ResolvedFolderPath is null)
            {
                unresolved++;
                db.RemoveShow((string)row["id"]!);
                LogExample("show", row, result);
                continue;
            }

            if (SameLocalPath(row["folderName"]?.ToString(), result.ResolvedFolderPath))
            {
                unchanged++;
                continue;
            }

            db.UpsertShows([new ShowRecord(
                result.ResolvedFolderPath,
                row["source"]?.ToString() ?? "plex",
                row["sourceRef"]?.ToString() ?? "",
                row["title"]?.ToString() ?? "",
                row["year"] as int?,
                sourcePath,
                row["plexHasTheme"] is true,
                true,
                row.GetValueOrDefault("instanceId")?.ToString(),
                row.GetValueOrDefault("remoteItemId")?.ToString(),
                row.GetValueOrDefault("qualityLabel")?.ToString(),
                row.GetValueOrDefault("tmdbId")?.ToString(),
                row.GetValueOrDefault("tvdbId")?.ToString(),
                row.GetValueOrDefault("imdbId")?.ToString())]);
            repaired++;
        }

        var resultSummary = new PathRepairResult(examined, unchanged, repaired, unresolved);
        db.SetSetting("last_path_repair_result", System.Text.Json.JsonSerializer.Serialize(resultSummary));
        return resultSummary;

        void LogExample(string mediaType, Dictionary<string, object?> row, PathResolutionResult result)
        {
            if (examples++ >= 5) return;
            log?.Invoke($"Path repair quarantined unresolved {mediaType} '{LogSanitizer.Clean(row["title"]?.ToString() ?? "")}'. " +
                $"Source: {LogSanitizer.Clean(result.SourceFolderPath)}. Reason: {result.FailureReason}");
        }
    }

    private static bool SameLocalPath(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private bool StoredFolderIsSafe(Dictionary<string, object?> row)
    {
        var folder = row["folderName"]?.ToString() ?? "";
        var roots = db.GetTrustedLibraryRoots();
        return Directory.Exists(folder) && roots.Count > 0 && ThemeFiles.IsWithinRoots(folder, roots);
    }
}
