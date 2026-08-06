namespace Themearr.API.Services;

/// <summary>Resolves persistent application-owned files from the configured database path.</summary>
public sealed class ApplicationDataDirectory
{
    public ApplicationDataDirectory(string databasePath)
    {
        var fullDatabasePath = Path.GetFullPath(databasePath);
        Root = Path.TrimEndingDirectorySeparator(
            Path.GetDirectoryName(fullDatabasePath)
            ?? throw new ArgumentException("DB_PATH must include a parent directory.", nameof(databasePath)));
    }

    public string Root { get; }

    public string ResolveContained(params string[] segments)
    {
        var candidate = Path.GetFullPath(Path.Combine([Root, .. segments]));
        var relative = Path.GetRelativePath(Root, candidate);
        if (Path.IsPathFullyQualified(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Application data path escaped its configured directory.");
        return candidate;
    }
}
