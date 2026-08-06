using Themearr.API.Data;

namespace Themearr.API.Services.Sources;

public sealed class PlexShowLibrarySource(
    PlexService plex, PlexLibrarySource posterSource, Database db) : IShowLibrarySource
{
    public string Name => "plex";
    public TimeSpan SyncInterval => TimeSpan.FromHours(24);

    public Task<IReadOnlyList<ShowRecord>> FetchAsync(Action<string> log, CancellationToken ct) =>
        FetchCoreAsync(log);

    private async Task<IReadOnlyList<ShowRecord>> FetchCoreAsync(Action<string> log) =>
        await plex.FetchShowsAsync(log);

    public Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct) =>
        posterSource.FetchPosterAsync(sourceRef, width, ct);

    public Task<string?> CheckAsync(CancellationToken ct) => posterSource.CheckAsync(ct);

    public string? SyncBlockedReason
    {
        get
        {
            if (db.GetPlexServers().Count == 0)
                return "No Plex server is selected for TV shows.";
            return db.GetSelectedShowLibraries().Values.Sum(v => v.Count) == 0
                ? "No Plex TV libraries are selected."
                : null;
        }
    }
}
