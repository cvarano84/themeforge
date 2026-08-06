using Themearr.API.Data;

namespace Themearr.API.Services.Sources;

public sealed class DisabledShowLibrarySource : IShowLibrarySource
{
    public string Name => "disabled";
    public TimeSpan SyncInterval => TimeSpan.FromHours(24);
    public string? SyncBlockedReason => "TV show sync is disabled in Settings.";
    public Task<IReadOnlyList<ShowRecord>> FetchAsync(Action<string> log, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ShowRecord>>([]);
    public Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct) =>
        Task.FromResult<Stream?>(null);
    public Task<string?> CheckAsync(CancellationToken ct) => Task.FromResult<string?>(null);
}
