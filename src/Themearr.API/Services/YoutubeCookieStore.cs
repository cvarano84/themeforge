using System.Text;

namespace Themearr.API.Services;

public sealed record YoutubeCookieStatus(
    bool Configured,
    string Source,
    bool ManagedByEnvironment,
    bool CanUpload,
    bool CanDelete,
    bool Valid,
    int RecordCount,
    int YoutubeRecordCount,
    DateTime? UploadedAtUtc,
    string? Detail = null);

public sealed record YoutubeCookieResolution(
    YoutubeCookieStatus Status,
    string? ActivePath);

public sealed class YoutubeCookieValidationException(string message) : Exception(message);

public interface IYoutubeCookieStore
{
    string ManagedCookiePath { get; }
    YoutubeCookieResolution Resolve();
    Task<YoutubeCookieStatus> UploadAsync(Stream content, long declaredLength, CancellationToken ct = default);
    Task<YoutubeCookieStatus> DeleteAsync(CancellationToken ct = default);
}

public sealed class YoutubeCookieStore(
    ApplicationDataDirectory dataDirectory,
    ILogger<YoutubeCookieStore> log) : IYoutubeCookieStore
{
    public const long MaximumBytes = 1024 * 1024;
    private const string NetscapeHeader = "# Netscape HTTP Cookie File";
    private const string HttpHeader = "# HTTP Cookie File";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string ManagedCookiePath { get; } =
        dataDirectory.ResolveContained("secrets", "youtube-cookies.txt");

    public YoutubeCookieResolution Resolve()
    {
        var environmentPath = Environment.GetEnvironmentVariable("YTDLP_COOKIES_FILE")?.Trim();
        if (!string.IsNullOrEmpty(environmentPath))
            return ResolveFile(environmentPath, "environment", managedByEnvironment: true);

        return ResolveFile(ManagedCookiePath, "managed", managedByEnvironment: false);
    }

    public async Task<YoutubeCookieStatus> UploadAsync(
        Stream content, long declaredLength, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YTDLP_COOKIES_FILE")))
            throw new InvalidOperationException(
                "Cookies are managed by YTDLP_COOKIES_FILE and cannot be replaced from Settings.");
        if (declaredLength <= 0)
            throw new YoutubeCookieValidationException("Select a non-empty Netscape cookies.txt file.");
        if (declaredLength > MaximumBytes)
            throw new YoutubeCookieValidationException("The cookies file exceeds the 1 MiB limit.");

        var raw = await ReadBoundedAsync(content, ct);
        var validation = Validate(raw);
        var normalized = StrictUtf8.GetBytes(validation.NormalizedText);

        await _writeLock.WaitAsync(ct);
        string? temporaryPath = null;
        try
        {
            EnsureManagedDestinationIsSafe(createDirectory: true);
            temporaryPath = Path.Combine(Path.GetDirectoryName(ManagedCookiePath)!,
                $".youtube-cookies.{Guid.NewGuid():N}.tmp");
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(normalized, ct);
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            RestrictPermissions(temporaryPath);

            // Re-open the actual sibling file before replacement so validation covers what
            // will be renamed, not only the request buffer.
            _ = Validate(await File.ReadAllBytesAsync(temporaryPath, ct));
            EnsureManagedDestinationIsSafe(createDirectory: false);
            File.Move(temporaryPath, ManagedCookiePath, overwrite: true);
            temporaryPath = null;
            RestrictPermissions(ManagedCookiePath);
            log.LogInformation("YouTube cookies were securely stored in the application data directory.");
            return Resolve().Status;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.LogWarning("Could not remove a rejected cookie upload temporary file: {Reason}",
                        LogSanitizer.Clean(ex.Message));
                }
            }
            _writeLock.Release();
        }
    }

    public async Task<YoutubeCookieStatus> DeleteAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YTDLP_COOKIES_FILE")))
            throw new InvalidOperationException(
                "Cookies are managed by YTDLP_COOKIES_FILE and cannot be deleted from Settings.");

        await _writeLock.WaitAsync(ct);
        try
        {
            EnsureManagedDestinationIsSafe(createDirectory: false);
            if (File.Exists(ManagedCookiePath)) File.Delete(ManagedCookiePath);
            log.LogInformation("The application-managed YouTube cookies file was deleted.");
            return Resolve().Status;
        }
        finally { _writeLock.Release(); }
    }

    private YoutubeCookieResolution ResolveFile(string path, string source, bool managedByEnvironment)
    {
        var canManage = !managedByEnvironment;
        if (!FileValidation.IsRegularNonSymlink(path))
        {
            var configured = managedByEnvironment;
            return new(new YoutubeCookieStatus(
                configured, configured ? source : "none", managedByEnvironment,
                canManage, false, false, 0, 0, null,
                configured ? "The environment-managed cookies file is missing or unreadable." : null), null);
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumBytes)
                throw new YoutubeCookieValidationException("The cookies file is empty or exceeds the 1 MiB limit.");
            var validation = Validate(File.ReadAllBytes(path));
            DateTime? uploaded = managedByEnvironment
                ? null
                : new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).UtcDateTime;
            return new(new YoutubeCookieStatus(
                true, source, managedByEnvironment, canManage, canManage,
                true, validation.RecordCount, validation.RelevantRecordCount, uploaded), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YoutubeCookieValidationException)
        {
            return new(new YoutubeCookieStatus(
                true, source, managedByEnvironment, canManage, canManage,
                false, 0, 0, null,
                managedByEnvironment
                    ? "The environment-managed cookies file is invalid or unreadable."
                    : "The uploaded cookies file is invalid or unreadable; replace it with a Netscape cookies.txt file."), null);
        }
    }

    private void EnsureManagedDestinationIsSafe(bool createDirectory)
    {
        var directory = Path.GetDirectoryName(ManagedCookiePath)!;
        var expected = dataDirectory.ResolveContained("secrets", "youtube-cookies.txt");
        if (!string.Equals(Path.GetFullPath(ManagedCookiePath), expected,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("The managed cookie destination is outside application data.");

        if (Directory.Exists(directory))
        {
            var dirInfo = new DirectoryInfo(directory);
            if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0 || dirInfo.LinkTarget is not null)
                throw new InvalidOperationException("The managed cookie directory cannot be a symbolic link.");
        }
        else if (createDirectory)
        {
            Directory.CreateDirectory(directory);
            RestrictDirectoryPermissions(directory);
        }

        if (File.Exists(ManagedCookiePath) && !FileValidation.IsRegularNonSymlink(ManagedCookiePath))
            throw new InvalidOperationException("The managed cookie destination cannot be a symbolic link.");
        if (!File.Exists(ManagedCookiePath) && new FileInfo(ManagedCookiePath).LinkTarget is not null)
            throw new InvalidOperationException("The managed cookie destination cannot be a symbolic link.");
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > MaximumBytes)
                throw new YoutubeCookieValidationException("The cookies file exceeds the 1 MiB limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        if (buffer.Length == 0)
            throw new YoutubeCookieValidationException("Select a non-empty Netscape cookies.txt file.");
        return buffer.ToArray();
    }

    internal static CookieValidation Validate(byte[] bytes)
    {
        if (bytes.Length == 0)
            throw new YoutubeCookieValidationException("The cookies file is empty.");
        if (bytes.Length > MaximumBytes)
            throw new YoutubeCookieValidationException("The cookies file exceeds the 1 MiB limit.");
        if (bytes.Contains((byte)0))
            throw new YoutubeCookieValidationException("Upload a text Netscape cookies file, not a binary file.");
        if (bytes.AsSpan().StartsWith("PK"u8) || bytes.AsSpan().StartsWith("SQLite format 3"u8))
            throw new YoutubeCookieValidationException("Upload the exported Netscape cookies.txt file, not an archive or browser database.");

        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException)
        {
            throw new YoutubeCookieValidationException("The cookies file must be valid UTF-8 or ASCII text.");
        }
        text = text.TrimStart('\uFEFF');
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('<') || trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith('{') || trimmed.StartsWith('['))
            throw new YoutubeCookieValidationException("Upload a Netscape cookies.txt export, not HTML or JSON.");

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var firstMeaningful = lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim();
        if (firstMeaningful is not (NetscapeHeader or HttpHeader))
            throw new YoutubeCookieValidationException(
                "The first meaningful line must be '# Netscape HTTP Cookie File' or '# HTTP Cookie File'.");

        var records = 0;
        var relevant = 0;
        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            var line = rawLine;
            if (line.StartsWith('#') && !line.StartsWith("#HttpOnly_", StringComparison.Ordinal)) continue;
            var fields = line.Split('\t');
            if (fields.Length < 7)
                throw new YoutubeCookieValidationException("A cookie record is not valid Netscape tab-delimited format.");
            var domain = fields[0].StartsWith("#HttpOnly_", StringComparison.Ordinal)
                ? fields[0]["#HttpOnly_".Length..]
                : fields[0];
            domain = domain.Trim().TrimStart('.');
            if (domain.Length == 0)
                throw new YoutubeCookieValidationException("A cookie record has an empty domain.");
            records++;
            if (IsRelevantDomain(domain)) relevant++;
        }
        if (records == 0)
            throw new YoutubeCookieValidationException("The cookies file does not contain any cookie records.");
        if (relevant == 0)
            throw new YoutubeCookieValidationException("The cookies file contains no YouTube or Google authentication domains.");

        var normalized = string.Join('\n', lines).TrimEnd('\n') + "\n";
        return new(normalized, records, relevant);
    }

    private static bool IsRelevantDomain(string domain) =>
        domain.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        domain.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
        domain.Equals("google.com", StringComparison.OrdinalIgnoreCase) ||
        domain.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase);

    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (PlatformNotSupportedException) { }
    }

    private static void RestrictDirectoryPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch (PlatformNotSupportedException) { }
    }

    internal sealed record CookieValidation(string NormalizedText, int RecordCount, int RelevantRecordCount);
}
