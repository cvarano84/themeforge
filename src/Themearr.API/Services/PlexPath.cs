namespace Themearr.API.Services;

/// <summary>
/// Separator-agnostic parsing of Plex-reported media paths. A Plex server on Windows
/// reports paths with '\' separators (e.g. <c>M:\Movies\Red One (2024)\file.mkv</c>),
/// but ThemeForge often runs in a Linux container where <see cref="System.IO.Path"/>
/// only understands '/'. Without normalizing, the parent directory comes back empty and
/// every movie fails to resolve ("unresolved path"). These helpers normalize both
/// separators so path mappings and suffix search work regardless of the Plex host OS.
/// </summary>
public static class PlexPath
{
    public static string Normalize(string? path) => (path ?? "").Replace('\\', '/');

    public static string NormalizeRoot(string? path) => Normalize(path).Trim().TrimEnd('/');

    public static bool IsWindowsPath(string? path)
    {
        var normalized = Normalize(path);
        return (normalized.Length >= 3 && char.IsAsciiLetter(normalized[0])
            && normalized[1] == ':' && normalized[2] == '/')
            || normalized.StartsWith("//", StringComparison.Ordinal);
    }

    public static bool IsAbsoluteSourcePath(string? path)
    {
        var normalized = NormalizeRoot(path);
        return normalized.StartsWith("/", StringComparison.Ordinal) || IsWindowsPath(normalized);
    }

    private static StringComparison ComparisonFor(string sourceRoot) =>
        IsWindowsPath(sourceRoot) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Parent directory of a (possibly Windows) file path, normalized to '/'.</summary>
    public static string ParentDir(string filePath)
    {
        var p = Normalize(filePath).TrimEnd('/');
        var idx = p.LastIndexOf('/');
        return idx < 0 ? "" : p[..idx];
    }

    /// <summary>
    /// Translates <paramref name="sourceParent"/> under a <c>src → tgt</c> mapping,
    /// normalizing separators and matching the source case-insensitively (Windows paths
    /// are case-insensitive). Returns "" when the mapping doesn't apply.
    /// </summary>
    public static string ApplyMapping(string sourceParent, string src, string tgt)
    {
        sourceParent = NormalizeRoot(sourceParent);
        src = NormalizeRoot(src);
        tgt = NormalizeRoot(tgt);
        if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tgt)) return "";

        var comparison = ComparisonFor(src);
        if (sourceParent.Equals(src, comparison))
            return tgt;
        if (sourceParent.StartsWith(src + "/", comparison))
            return tgt + sourceParent[src.Length..];   // preserve the real case of the suffix
        return "";
    }

    /// <summary>Path segments, split on either separator (for suffix search).</summary>
    public static string[] Segments(string path) =>
        Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
}
