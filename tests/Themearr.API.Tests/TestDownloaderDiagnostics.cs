using Themearr.API.Services;

namespace Themearr.API.Tests;

internal static class TestDownloaderDiagnostics
{
    public static Task<DownloaderDiagnostics> Ready() => Task.FromResult(
        new DownloaderDiagnostics(true, false, "healthy", "Ready",
            new(true, "available", "test"), new(true, "available", "test"),
            new(true, "available", "test"), new(true, "available", "test"),
            new(false, "none", false, true, false, false, 0, 0, null),
            new("auto", "notConfigured", true, false, null),
            "192K", 300, 1, false, false, false));
}
