namespace Themearr.API.Services;

/// <summary>
/// Filesystem helpers for the per-media <c>theme.*</c> file (movies and shows alike):
/// detecting a usable theme, locating and typing it for playback, deleting it, checking
/// the target folder is writable, and writing the download atomically so a failed/killed
/// download can never leave a corrupt theme behind.
/// </summary>
public static class ThemeFiles
{
    // Working extensions that are NOT a finished theme: in-flight download (.part)
    // and yt-dlp's sidecar (.ytdl). Mirrors the read-time status filter.
    private static readonly string[] NonThemeExtensions = [".part", ".ytdl"];

    private static bool IsNonTheme(string path) =>
        NonThemeExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="folder"/> contains a finished, non-empty
    /// <c>theme.*</c> file. A zero-byte file (truncated/interrupted download) is
    /// treated as NOT usable so it gets retried instead of being marked downloaded.
    /// </summary>
    public static bool HasUsableTheme(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;
        return HasUsableThemeInExistingFolder(folder);
    }

    /// <summary>
    /// As <see cref="HasUsableTheme"/> but WITHOUT the <c>Directory.Exists</c> guard, for
    /// callers that have already confirmed the folder exists (e.g. per-movie status
    /// derivation over a whole library). Skipping the redundant stat halves the filesystem
    /// round-trips per movie — it matters on network-mounted libraries. Throws if the
    /// folder does not exist, so only call it once existence is established.
    /// </summary>
    internal static bool HasUsableThemeInExistingFolder(string folder) =>
        Directory.EnumerateFiles(folder, "theme.*")
            .Any(f => !IsNonTheme(f) && new FileInfo(f).Length > 0);

    /// <summary>
    /// Returns the exact completed <c>theme.mp3</c> when it is a readable, regular,
    /// non-empty file within the configured theme size ceiling. Reconciliation uses
    /// this stricter check because its contract is to restore theme.mp3 specifically,
    /// not merely discover a playable alternate extension.
    /// </summary>
    public static string? FindUsableThemeMp3(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        var path = Path.Combine(folder, "theme.mp3");
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || info.Length <= 0 || info.Length > StreamLimits.MaxThemeBytes)
                return null;
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.CanRead ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// True if the service user can actually create a file in <paramref name="folder"/>.
    /// Used to surface a clear error up front instead of failing every download
    /// silently — the typical Proxmox case where the <c>themearr</c> user lacks write
    /// permission on a bind-mounted media folder. Probes by creating and deleting a
    /// uniquely-named temp file (so it never collides with theme.*).
    /// </summary>
    public static bool IsDirectoryWritable(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;
        var probe = Path.Combine(folder, $".themearr-write-probe-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { /* best effort */ }
            return false;
        }
    }

    /// <summary>
    /// True if <paramref name="folder"/>, once canonicalized, is equal to or nested
    /// under one of <paramref name="roots"/>. Used to confine theme writes/deletes to
    /// the configured library roots so a malicious Plex-reported path (absolute, or
    /// containing <c>..</c>) can't target an arbitrary directory. Empty roots → false.
    /// </summary>
    public static bool IsWithinRoots(string folder, IEnumerable<string> roots)
    {
        if (string.IsNullOrEmpty(folder)) return false;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                if (!Contained(full, fullRoot)) continue;
                if (ExistingPathEscapesThroughLink(full, fullRoot)) continue;
                return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
        return false;
    }

    private static bool Contained(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison)
            || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    // A lexical child can still escape through a symlink/junction below the root. Walk
    // only the already-existing directory chain and reject any resolved link target that
    // leaves the physical root. Missing final children are handled by the caller's
    // existence check before writes/deletes.
    private static bool ExistingPathEscapesThroughLink(string fullPath, string fullRoot)
    {
        if (!Directory.Exists(fullRoot) || !Directory.Exists(fullPath)) return false;
        var rootInfo = new DirectoryInfo(fullRoot);
        var physicalRoot = rootInfo.LinkTarget is null
            ? rootInfo.FullName
            : rootInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? rootInfo.FullName;
        var current = physicalRoot;
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative == ".") return false;

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            if (!Directory.Exists(next)) return false;
            var info = new DirectoryInfo(next);
            current = info.LinkTarget is null
                ? info.FullName
                : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;
            if (!Contained(Path.GetFullPath(current), Path.GetFullPath(physicalRoot))) return true;
        }
        return false;
    }

    /// <summary>
    /// Streams <paramref name="source"/> into <paramref name="finalPath"/> atomically:
    /// the bytes are written to a sibling <c>.part</c> file (bounded by
    /// <paramref name="maxBytes"/>), and only on success is that file moved into place,
    /// replacing any existing theme. A failed, oversized, killed, or empty download
    /// therefore never clobbers a previously-good theme and never leaves a truncated
    /// <c>theme.mp3</c> on disk. An empty (0-byte) body is rejected. Returns the number
    /// of bytes written.
    /// </summary>
    public static async Task<long> WriteAtomicAsync(
        Stream source, string finalPath, long maxBytes, CancellationToken ct = default)
    {
        var tempPath = finalPath + ".part";
        try
        {
            long written;
            await using (var fileStream = File.Create(tempPath))
            {
                written = await StreamLimits.CopyWithLimitAsync(source, fileStream, maxBytes, ct);
                await fileStream.FlushAsync(ct);
            }

            if (written == 0)
                throw new InvalidOperationException(
                    "Downloaded theme was empty (0 bytes) — refusing to save a corrupt theme.");

            File.Move(tempPath, finalPath, overwrite: true);
            return written;
        }
        catch
        {
            // Never leave the partial file behind; any existing finalPath is untouched
            // because we only Move on success.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Validates and atomically copies a completed local theme to another
    /// quality location without ever exposing a partial final file.</summary>
    public static async Task<long> CopyAtomicAsync(
        string sourcePath, string finalPath, bool replace = false, CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Source theme is unavailable.");
        var info = new FileInfo(sourcePath);
        if (info.Length <= 0 || info.Length > StreamLimits.MaxThemeBytes)
            throw new InvalidOperationException("Source theme failed audio size validation.");
        if (!replace && File.Exists(finalPath) && new FileInfo(finalPath).Length > 0) return 0;
        await using var source = File.OpenRead(sourcePath);
        return await WriteAtomicAsync(source, finalPath, StreamLimits.MaxThemeBytes, ct);
    }

    /// <summary>
    /// The playable theme file in <paramref name="folder"/>, or null when there isn't one.
    /// Shared by the movie and show theme-audio endpoints so the two can never disagree
    /// about which file is "the theme".
    /// </summary>
    public static string? FindThemeFile(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "theme.*").FirstOrDefault(f => !IsNonTheme(f))
            : null;

    /// <summary>Content type for a theme file, by extension. Falls back to audio/mpeg.</summary>
    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".ogg" => "audio/ogg",
        ".opus" => "audio/opus",
        ".webm" => "audio/webm",
        ".flac" => "audio/flac",
        _ => "audio/mpeg",
    };

    /// <summary>
    /// Deletes every theme file in <paramref name="folder"/>, leaving in-flight downloads
    /// (<c>.part</c>/<c>.ytdl</c>) alone. Returns true if anything was deleted. Callers MUST
    /// have already confirmed the folder is inside the configured library roots — see
    /// <see cref="IsWithinRoots"/>. This is a delete path shared by movies and shows;
    /// keeping it in one place is what stops the two containment checks from drifting.
    /// </summary>
    public static bool DeleteThemes(string folder)
    {
        var deleted = false;
        foreach (var f in Directory.EnumerateFiles(folder, "theme.*"))
        {
            if (IsNonTheme(f)) continue;
            File.Delete(f);
            deleted = true;
        }
        return deleted;
    }
}
