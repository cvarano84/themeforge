using System.Collections.Concurrent;
using Themearr.API.Data;

namespace Themearr.API.Services;

public sealed record ThemeReconciliationResult(
    int Satisfied,
    int Copied,
    int Missing,
    int Failed);

/// <summary>
/// Reconciles the physical theme state of every Radarr location for one logical movie.
/// Disk is authoritative: a stored <c>downloaded</c> status never suppresses repair of a
/// location whose <c>theme.mp3</c> has disappeared.
/// </summary>
public sealed class ThemeReconciliationService
{
    private readonly Database _db;
    private readonly ILogger<ThemeReconciliationService> _log;
    private readonly Func<string, string, CancellationToken, Task<long>> _copy;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _groupLocks =
        new(StringComparer.Ordinal);

    public ThemeReconciliationService(Database db, ILogger<ThemeReconciliationService> log)
        : this(db, log, (source, destination, ct) =>
            ThemeFiles.CopyAtomicAsync(source, destination, replace: false, ct)) { }

    internal ThemeReconciliationService(
        Database db,
        ILogger<ThemeReconciliationService> log,
        Func<string, string, CancellationToken, Task<long>> copy)
    {
        _db = db;
        _log = log;
        _copy = copy;
    }

    /// <summary>
    /// Reconciles only logical movies touched by the supplied Radarr result. A full
    /// scheduled sync supplies the whole library; a per-instance sync supplies only
    /// groups represented on that instance.
    /// </summary>
    public async Task<ThemeReconciliationResult> ReconcileMoviesAsync(
        IEnumerable<MovieRecord> movies,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        var ids = movies
            .Where(movie => !string.IsNullOrWhiteSpace(movie.Folder))
            .Select(movie => MediaFolderId.For(movie.Folder))
            .ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return new ThemeReconciliationResult(0, 0, 0, 0);

        var stored = _db.GetStoredMovies();
        var keys = stored
            .Where(row => ids.Contains(Value(row, "id")) && Value(row, "source") == "radarr")
            .Select(row => MediaGrouping.GroupKey(row, shows: false))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var total = new ThemeReconciliationResult(0, 0, 0, 0);
        foreach (var key in keys)
        {
            var seed = stored.First(row => MediaGrouping.GroupKey(row, shows: false) == key);
            var result = await ReconcileMovieAsync(seed, progress, ct);
            total = new ThemeReconciliationResult(
                total.Satisfied + result.Satisfied,
                total.Copied + result.Copied,
                total.Missing + result.Missing,
                total.Failed + result.Failed);
        }
        return total;
    }

    public async Task<ThemeReconciliationResult> ReconcileMovieAsync(
        IReadOnlyDictionary<string, object?> movie,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        var groupKey = MediaGrouping.GroupKey(movie, shows: false);
        var gate = _groupLocks.GetOrAdd(groupKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-read after taking the lock. A duplicate webhook or a concurrent download
            // may have satisfied the group while this caller was waiting.
            var locations = _db.GetStoredMovies()
                .Where(row => Value(row, "source") == "radarr"
                    && MediaGrouping.GroupKey(row, shows: false) == groupKey
                    && IsWatched(row)
                    && row.GetValueOrDefault("ignored") is not true)
                .OrderBy(InstancePriority)
                .ThenBy(row => Value(row, "id"), StringComparer.Ordinal)
                .ToList();
            if (locations.Count == 0) return new ThemeReconciliationResult(0, 0, 0, 0);

            var missing = locations
                .Where(location => ThemeFiles.FindUsableThemeMp3(Value(location, "folderName")) is null)
                .ToList();
            foreach (var healthy in locations.Except(missing))
            {
                _db.SetMovieStatus(Value(healthy, "id"), "downloaded");
                Debug(progress, $"Destination already satisfied: {Describe(healthy)}");
            }

            if (missing.Count == 0)
                return new ThemeReconciliationResult(locations.Count, 0, 0, 0);

            var representative = locations[0];
            Info(progress,
                $"Movie: {Value(representative, "title")} ({Value(representative, "year")}) " +
                $"tmdb={Value(representative, "tmdbId")}");

            foreach (var destination in missing)
            {
                if (Value(destination, "status") == "downloaded")
                    Info(progress,
                        $"Stored theme state is stale; filesystem theme.mp3 is missing at {Describe(destination)}");
                else
                    Info(progress, $"theme.mp3 missing at {Describe(destination)}");
                _db.SetMovieStatus(Value(destination, "id"), "pending");
            }

            var source = locations
                .Select(location => (Location: location,
                    Path: ThemeFiles.FindUsableThemeMp3(Value(location, "folderName"))))
                .FirstOrDefault(candidate => candidate.Path is not null);
            if (source.Path is null)
            {
                Info(progress, "No existing theme found across watched copies; provider acquisition remains pending");
                return new ThemeReconciliationResult(0, 0, missing.Count, 0);
            }

            var copied = 0;
            var failed = 0;
            foreach (var destination in missing)
            {
                var destinationFolder = Value(destination, "folderName");
                var destinationPath = Path.Combine(destinationFolder, "theme.mp3");
                Debug(progress,
                    $"Searching {locations.Count - 1} alternate Radarr copies for {Describe(destination)}");
                Info(progress, $"Found theme source: {Describe(source.Location)} {source.Path}");
                try
                {
                    EnsureWritableDestination(destination, destinationFolder);
                    await _copy(source.Path, destinationPath, ct);
                    if (ThemeFiles.FindUsableThemeMp3(destinationFolder) is null)
                        throw new IOException("atomic copy completed without a valid destination theme.mp3");

                    _db.SetMovieStatus(Value(destination, "id"), "downloaded");
                    copied++;
                    Info(progress, $"Copied theme successfully: {source.Path} -> {destinationPath}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    _db.SetMovieStatus(Value(destination, "id"), "pending");
                    failed++;
                    _log.LogWarning(ex,
                        "[Theme Reconcile] Copy failed for {Destination}; destination remains pending",
                        LogSanitizer.Clean(destinationPath));
                    progress?.Invoke($"[Theme Reconcile] Copy failed for {Describe(destination)}; destination remains pending");
                }
            }

            return new ThemeReconciliationResult(
                locations.Count - missing.Count,
                copied,
                missing.Count - copied,
                failed);
        }
        finally
        {
            gate.Release();
        }
    }

    private void EnsureWritableDestination(
        IReadOnlyDictionary<string, object?> destination,
        string folder)
    {
        var roots = _db.GetTrustedLibraryRoots();
        if (!Directory.Exists(folder) || roots.Count == 0 || !ThemeFiles.IsWithinRoots(folder, roots))
            throw new UnauthorizedAccessException("destination is unavailable or outside configured library roots");
        if (!new LocalFolderResolver(_db).IsStoredFolderAuthorized(destination, isShow: false, out _))
            throw new UnauthorizedAccessException("destination no longer resolves from its Radarr source path");
        if (!ThemeFiles.IsDirectoryWritable(folder))
            throw new UnauthorizedAccessException("destination directory is not writable");
    }

    private int InstancePriority(IReadOnlyDictionary<string, object?> location)
    {
        var instanceId = Value(location, "instanceId");
        return instanceId.Length == 0 ? int.MaxValue : _db.GetArrInstance(instanceId)?.Priority ?? int.MaxValue;
    }

    private bool IsWatched(IReadOnlyDictionary<string, object?> location)
    {
        var instanceId = Value(location, "instanceId");
        // Legacy single-Radarr configuration has no arr_instances row and remains valid.
        return instanceId.Length == 0 || _db.GetArrInstance(instanceId)?.Enabled == true;
    }

    private string Describe(IReadOnlyDictionary<string, object?> location)
    {
        var instanceId = Value(location, "instanceId");
        var instanceName = _db.GetArrInstance(instanceId)?.Name ?? "Radarr";
        return $"{instanceName} {LogSanitizer.Clean(Value(location, "folderName"))}";
    }

    private void Info(Action<string>? progress, string message)
    {
        _log.LogInformation("[Theme Reconcile] {Message}", LogSanitizer.Clean(message));
        progress?.Invoke($"[Theme Reconcile] {message}");
    }

    private void Debug(Action<string>? _, string message)
    {
        _log.LogDebug("[Theme Reconcile] {Message}", LogSanitizer.Clean(message));
    }

    private static string Value(IReadOnlyDictionary<string, object?> row, string key) =>
        row.GetValueOrDefault(key)?.ToString()?.Trim() ?? "";
}
