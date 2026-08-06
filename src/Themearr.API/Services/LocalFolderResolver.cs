using Themearr.API.Data;

namespace Themearr.API.Services;

public sealed record PathResolutionResult(
    string SourceFilePath,
    string SourceFolderPath,
    Dictionary<string, string>? MatchedMapping,
    string? MappedCandidate,
    bool CandidateExists,
    bool CandidateWithinRoots,
    string ResolutionMode,
    string? ResolvedFolderPath,
    string? FailureReason);

public sealed record PathConfigurationValidation(
    IReadOnlyList<string> LibraryRoots,
    IReadOnlyList<Dictionary<string, string>> Mappings,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Translates source-side Plex/Radarr paths into existing local folders authorized by
/// ThemeForge's configured library roots. An unresolved source path is never returned as
/// a writable folder.
/// </summary>
public class LocalFolderResolver(Database db)
{
    private static readonly StringComparer LocalComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public (string folder, string mode) Resolve(string sourceFilePath)
    {
        var result = ResolveDetailed(sourceFilePath);
        return (result.ResolvedFolderPath ?? "", result.ResolutionMode);
    }

    public PathResolutionResult ResolveDetailed(
        string sourceFilePath,
        IEnumerable<Dictionary<string, string>>? mappings = null,
        IEnumerable<string>? libraryRoots = null,
        bool sourceIsFolder = false)
    {
        var normalizedSource = PlexPath.Normalize(sourceFilePath).Trim();
        var sourceParent = sourceIsFolder
            ? PlexPath.NormalizeRoot(normalizedSource)
            : PlexPath.ParentDir(normalizedSource);
        var roots = NormalizeExistingRoots(libraryRoots ?? db.GetLibraryPaths());

        Dictionary<string, string>? matched = null;
        string? mappedCandidate = null;
        var candidateExists = false;
        var candidateWithinRoots = false;

        // Explicit configuration wins. Most-specific prefix first, with segment-aware
        // matching performed by PlexPath.ApplyMapping.
        foreach (var mapping in (mappings ?? db.GetPathMappings())
                     .OrderByDescending(m => PlexPath.Segments(m.GetValueOrDefault("source", "")).Length)
                     .ThenByDescending(m => PlexPath.NormalizeRoot(m.GetValueOrDefault("source", "")).Length))
        {
            var mapped = PlexPath.ApplyMapping(sourceParent,
                mapping.GetValueOrDefault("source", ""),
                mapping.GetValueOrDefault("target", ""));
            if (string.IsNullOrEmpty(mapped)) continue;

            matched = new Dictionary<string, string>
            {
                ["source"] = PlexPath.NormalizeRoot(mapping.GetValueOrDefault("source", "")),
                ["target"] = NormalizeLocal(mapping.GetValueOrDefault("target", "")),
            };
            mappedCandidate = TryCanonicalLocal(mapped);
            if (mappedCandidate is not null)
            {
                candidateExists = Directory.Exists(mappedCandidate);
                candidateWithinRoots = ThemeFiles.IsWithinRoots(mappedCandidate, roots)
                    && ThemeFiles.IsWithinRoots(mappedCandidate, [matched["target"]]);
                if (candidateExists && candidateWithinRoots)
                    return Result("mapping", mappedCandidate, null);
            }
            break;
        }

        // Direct is allowed only inside an explicitly configured local root.
        var direct = TryCanonicalLocal(sourceParent);
        if (direct is not null && Directory.Exists(direct) && ThemeFiles.IsWithinRoots(direct, roots))
            return Result("direct", direct, null);

        var suffix = FindBySuffix(normalizedSource, roots);
        if (!string.IsNullOrEmpty(suffix)) return Result("suffix", suffix, null);

        string reason;
        if (roots.Count == 0)
            reason = "No existing local library roots are configured.";
        else if (matched is not null && mappedCandidate is null)
            reason = "The mapped candidate is not a valid absolute local path.";
        else if (matched is not null && !candidateWithinRoots)
            reason = "The mapped candidate is outside the configured local library roots.";
        else if (matched is not null && !candidateExists)
            reason = "The mapped candidate does not exist inside the ThemeForge container. Check the Docker bind mount.";
        else
            reason = "No configured mapping, direct local directory, or bounded suffix match resolved this source path.";

        return Result("unresolved", null, reason);

        PathResolutionResult Result(string mode, string? folder, string? failure) => new(
            normalizedSource, sourceParent, matched, mappedCandidate, candidateExists,
            candidateWithinRoots, mode, folder, failure);
    }

    public PathResolutionResult ResolveFolderDetailed(string sourceFolderPath) =>
        ResolveDetailed(sourceFolderPath, sourceIsFolder: true);

    public PathResolutionResult ResolveFolderDetailed(string sourceFolderPath, string? instanceId, string? serviceType)
    {
        var all = db.GetPathMappings();
        var scoped = all.Where(m =>
        {
            var mappingInstance = m.GetValueOrDefault("instanceId", "");
            var mappingService = m.GetValueOrDefault("serviceType", "");
            return string.IsNullOrEmpty(mappingInstance) && string.IsNullOrEmpty(mappingService)
                || !string.IsNullOrEmpty(instanceId) && mappingInstance == instanceId
                || string.IsNullOrEmpty(mappingInstance) && mappingService == serviceType;
        }).OrderByDescending(m => m.GetValueOrDefault("instanceId", "") == instanceId ? 2
            : m.GetValueOrDefault("serviceType", "") == serviceType ? 1 : 0);
        return ResolveDetailed(sourceFolderPath, scoped, sourceIsFolder: true);
    }

    public PathResolutionResult ResolveStoredSource(
        string sourcePath, string? source, bool isShow, string? instanceId = null) =>
        source is "radarr" or "sonarr"
            ? ResolveFolderDetailed(sourcePath, instanceId, source)
            : ResolveDetailed(sourcePath, sourceIsFolder: isShow || source == "radarr");

    /// <summary>
    /// Verifies that a persisted folder is both authorized by the current local roots
    /// and still the result of resolving its retained source path. Legacy records with
    /// no retained source path may be used only when their local folder is independently
    /// contained by an existing configured root.
    /// </summary>
    public bool IsStoredFolderAuthorized(
        IReadOnlyDictionary<string, object?> row,
        bool isShow,
        out PathResolutionResult? resolution)
    {
        resolution = null;
        var folder = row.GetValueOrDefault("folderName")?.ToString() ?? "";
        var roots = db.GetTrustedLibraryRoots();
        if (roots.Count == 0 || !Directory.Exists(folder) || !ThemeFiles.IsWithinRoots(folder, roots))
            return false;

        var sourcePath = row.GetValueOrDefault("sourcePath")?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(sourcePath)) return true;

        resolution = ResolveStoredSource(sourcePath, row.GetValueOrDefault("source")?.ToString(), isShow,
            row.GetValueOrDefault("instanceId")?.ToString());
        return resolution.ResolvedFolderPath is not null
            && SameLocalPath(folder, resolution.ResolvedFolderPath);
    }

    public PathConfigurationValidation ValidateConfiguration(
        IEnumerable<Dictionary<string, string>> mappings,
        IEnumerable<string> libraryRoots)
    {
        var errors = new List<string>();
        var roots = new List<string>();
        foreach (var raw in libraryRoots)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                errors.Add("Library roots cannot be empty.");
                continue;
            }
            var root = TryCanonicalLocal(raw);
            if (root is null)
            {
                errors.Add($"Library root '{raw}' must be an absolute local path.");
                continue;
            }
            if (!Directory.Exists(root))
            {
                errors.Add($"Library root '{root}' does not exist inside the ThemeForge container. Check the Docker mount.");
                continue;
            }
            if (!roots.Contains(root, LocalComparer)) roots.Add(root);
        }

        var normalized = new List<Dictionary<string, string>>();
        foreach (var mapping in mappings)
        {
            var sourceRaw = mapping.GetValueOrDefault("source", "");
            var targetRaw = mapping.GetValueOrDefault("target", "");
            if (string.IsNullOrWhiteSpace(sourceRaw) || string.IsNullOrWhiteSpace(targetRaw))
            {
                errors.Add("Every path mapping requires both a source and a target.");
                continue;
            }

            var source = PlexPath.NormalizeRoot(sourceRaw);
            if (!PlexPath.IsAbsoluteSourcePath(source)
                || PlexPath.Segments(source).Contains("..", StringComparer.Ordinal))
            {
                errors.Add($"Mapping source '{sourceRaw}' must be an absolute Plex/Radarr path without '..' traversal.");
                continue;
            }

            var target = TryCanonicalLocal(targetRaw);
            if (target is null)
            {
                errors.Add($"Mapping target '{targetRaw}' must be an absolute local path.");
                continue;
            }
            if (!Directory.Exists(target))
            {
                errors.Add($"Mapping target '{target}' does not exist inside the ThemeForge container. Mount it before saving.");
                continue;
            }
            if (!ThemeFiles.IsWithinRoots(target, roots))
            {
                errors.Add($"Mapping target '{target}' must be equal to or beneath a configured local library root.");
                continue;
            }
            var instanceId = mapping.GetValueOrDefault("instanceId", "").Trim();
            var serviceType = mapping.GetValueOrDefault("serviceType", "").Trim().ToLowerInvariant();
            if (serviceType.Length > 0 && serviceType is not ("radarr" or "sonarr"))
            {
                errors.Add("A mapping serviceType must be 'radarr' or 'sonarr'.");
                continue;
            }
            if (normalized.Any(m => SameSource(m["source"], source)
                && m.GetValueOrDefault("instanceId", "") == instanceId
                && m.GetValueOrDefault("serviceType", "") == serviceType))
            {
                errors.Add($"Duplicate mapping source '{source}' in the same mapping scope.");
                continue;
            }
            var normalizedMapping = new Dictionary<string, string> { ["source"] = source, ["target"] = target };
            if (instanceId.Length > 0) normalizedMapping["instanceId"] = instanceId;
            if (serviceType.Length > 0) normalizedMapping["serviceType"] = serviceType;
            normalized.Add(normalizedMapping);
        }

        return new PathConfigurationValidation(roots, normalized,
            errors.Distinct(StringComparer.Ordinal).ToList());
    }

    private static bool SameLocalPath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private string FindBySuffix(string sourceFilePath, IReadOnlyList<string> roots)
    {
        if (roots.Count == 0) return "";
        var sourceParts = PlexPath.Segments(PlexPath.ParentDir(sourceFilePath));
        if (sourceParts.Length == 0) return "";

        var maxSuffix = Math.Min(6, sourceParts.Length);
        foreach (var root in roots)
            for (var size = maxSuffix; size > 0; size--)
            {
                var candidate = Path.Combine(new[] { root }.Concat(sourceParts[^size..]).ToArray());
                if (Directory.Exists(candidate) && ThemeFiles.IsWithinRoots(candidate, [root]))
                    return Path.GetFullPath(candidate);
            }

        var target = sourceParts[^1];
        var maxDirs = int.Parse(db.GetSetting("max_search_dirs", "20000"));
        var maxDepth = int.Parse(db.GetSetting("search_depth", "4"));
        var visited = 0;
        foreach (var root in roots)
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    if (++visited > maxDirs) return "";
                    var depth = Path.GetRelativePath(root, dir).Split(Path.DirectorySeparatorChar).Length;
                    if (depth > maxDepth) continue;
                    if (string.Equals(Path.GetFileName(dir), target, StringComparison.OrdinalIgnoreCase)
                        && ThemeFiles.IsWithinRoots(dir, [root])) return Path.GetFullPath(dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return "";
    }

    private static bool SameSource(string left, string right) =>
        string.Equals(left, right, PlexPath.IsWindowsPath(left)
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static List<string> NormalizeExistingRoots(IEnumerable<string> roots) => roots
        .Select(TryCanonicalLocal)
        .Where(r => r is not null && Directory.Exists(r))
        .Cast<string>()
        .Distinct(LocalComparer)
        .ToList();

    private static string NormalizeLocal(string value) =>
        TryCanonicalLocal(value) ?? PlexPath.NormalizeRoot(value);

    private static string? TryCanonicalLocal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            if (!Path.IsPathFullyQualified(value)) return null;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
