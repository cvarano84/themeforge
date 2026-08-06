using Themearr.API.Data;

namespace Themearr.API.Services.Sources;

/// <summary>A TV-series library source, kept separate from movie sources.</summary>
public interface IShowLibrarySource
{
    string Name { get; }
    TimeSpan SyncInterval { get; }
    Task<IReadOnlyList<ShowRecord>> FetchAsync(Action<string> log, CancellationToken ct);
    Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct);
    Task<string?> CheckAsync(CancellationToken ct);
    string? SyncBlockedReason { get; }
}
