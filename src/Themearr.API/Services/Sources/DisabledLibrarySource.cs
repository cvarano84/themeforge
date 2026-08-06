using Themearr.API.Data;

namespace Themearr.API.Services.Sources;

/// <summary>Explicitly disables movie discovery without deleting stored movies or themes.</summary>
public sealed class DisabledLibrarySource : ILibrarySource
{
    public string Name => "disabled";
    public TimeSpan SyncInterval => TimeSpan.FromHours(24);
    public string? SyncBlockedReason => "Movie library sync is disabled in Settings.";
    public Task<IReadOnlyList<MovieRecord>> FetchAsync(Action<string> log, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MovieRecord>>([]);
    public Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct) =>
        Task.FromResult<Stream?>(null);
    public Task<string?> CheckAsync(CancellationToken ct) => Task.FromResult<string?>(null);
}
