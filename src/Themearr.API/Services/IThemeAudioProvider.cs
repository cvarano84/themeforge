namespace Themearr.API.Services;

/// <summary>
/// Source of downloadable theme audio for a given YouTube video id. Implementations
/// encapsulate exactly one download backend,
/// so swapping providers later is a new implementation + a DI registration change
/// rather than a rewrite of <see cref="DownloadService"/>.
/// </summary>
public interface IThemeAudioProvider
{
    /// <summary>
    /// Returns local-only readiness diagnostics. Implementations must not download
    /// media or contact YouTube during this check.
    /// </summary>
    Task<DownloaderDiagnostics> CheckConfigurationAsync(
        bool forceRefresh = false, CancellationToken ct = default);

    /// <summary>
    /// Downloads the theme audio for <paramref name="videoId"/> to
    /// <paramref name="outputPath"/>, reporting human-readable progress through
    /// <paramref name="progress"/>. Returns the provider-reported track title, if
    /// any. Throws on failure (<see cref="ProviderNotConfiguredException"/> when the
    /// provider is not configured).
    /// </summary>
    Task<string?> DownloadAsync(
        string videoId, string outputPath, Action<string> progress, CancellationToken ct = default);
}

/// <summary>
/// Retained for integrations which distinguish a pre-flight configuration failure.
/// </summary>
public class ProviderNotConfiguredException(string message) : Exception(message);
