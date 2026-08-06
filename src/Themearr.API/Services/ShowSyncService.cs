using Themearr.API.Data;
using Themearr.API.Services.Sources;

namespace Themearr.API.Services;

/// <summary>
/// Syncs TV shows from the operator's selected Plex show libraries into the `shows`
/// table. Opt-in: when no show libraries are selected it fetches nothing and prunes
/// nothing. Mirrors <see cref="SyncService"/>'s fetch → upsert → prune-except shape,
/// with the same "only prune after a non-empty, fully-resolved sync" safety.
/// </summary>
public class ShowSyncService(
    Database db, ShowSourceResolver sources, ILogger<ShowSyncService> log,
    LibraryPathRepairService? pathRepair = null)
{
    private readonly LibraryPathRepairService _pathRepair = pathRepair
        ?? new LibraryPathRepairService(db, new LocalFolderResolver(db));
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var source = sources.Active;
        if (source.Name == "disabled") return 0;
        if (source.Name == "plex" && db.GetSelectedShowLibraries().Values.Sum(v => v.Count) == 0)
            return 0; // Plex TV remains opt-in.
        if (source.SyncBlockedReason is { } blocked)
            throw new InvalidOperationException(blocked);

        var shows = await source.FetchAsync(msg => log.LogInformation("{Msg}", msg), ct);

        // A source switch while a network fetch is in flight must not import or prune
        // against the now-inactive source. The next scheduled/manual run handles it.
        if (!string.Equals(sources.Active.Name, source.Name, StringComparison.Ordinal))
        {
            log.LogInformation("Show source changed during sync; discarding the {Source} result", source.Name);
            return 0;
        }
        var repair = _pathRepair.RepairAll(msg => log.LogInformation("{Msg}", msg));
        log.LogInformation("Path repair examined {Examined}, repaired {Repaired}, unresolved {Unresolved}",
            repair.Examined, repair.Repaired, repair.Unresolved);
        db.UpsertShows(shows);

        var unresolved = int.TryParse(db.GetSetting("last_show_sync_unresolved_count", "0"), out var n) ? n : 0;
        if (source is SonarrShowLibrarySource sonarr && db.GetArrInstances("sonarr", enabledOnly: true).Count > 0)
        {
            foreach (var instanceId in sonarr.LastSuccessfulInstanceIds)
            {
                var instanceShows = shows.Where(s => s.InstanceId == instanceId).ToList();
                var instance = db.GetArrInstance(instanceId);
                if (instanceShows.Count == 0 || instance?.UnresolvedPathCount > 0) continue;
                var removed = db.PruneShowsExcept(instanceShows.Select(s => s.Folder), "sonarr", instanceId);
                if (removed > 0) log.LogInformation(
                    "Removed {Count} stale show location(s) from {Instance}", removed, instance!.Name);
            }
        }
        else if (shows.Count > 0 && unresolved == 0)
        {
            var removed = db.PruneShowsExcept(shows.Select(s => s.Folder), source.Name);
            if (removed > 0) log.LogInformation("Removed {N} shows no longer in the library", removed);
        }
        return shows.Count;
    }
}
