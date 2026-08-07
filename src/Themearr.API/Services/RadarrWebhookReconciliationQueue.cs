using System.Collections.Concurrent;
using System.Text.Json;
using Themearr.API.Data;

namespace Themearr.API.Services;

internal sealed record RadarrWebhookMovieIdentity(
    string? TmdbId,
    string? ImdbId,
    string? InstanceId,
    string? RemoteItemId,
    string? SourcePath,
    string? Title,
    int? Year,
    bool MatchAll = false)
{
    public bool Matches(MovieRecord movie)
    {
        if (MatchAll) return true;
        if (!string.IsNullOrWhiteSpace(TmdbId))
            return string.Equals(TmdbId, movie.TmdbId, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ImdbId))
            return string.Equals(ImdbId, movie.ImdbId, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(InstanceId) && !string.IsNullOrWhiteSpace(RemoteItemId))
            return string.Equals(InstanceId, movie.InstanceId, StringComparison.Ordinal)
                && string.Equals(RemoteItemId, movie.RemoteItemId, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(SourcePath))
            return SameSourcePath(SourcePath, movie.SourcePath);
        return Year.HasValue && movie.Year == Year
            && !string.IsNullOrWhiteSpace(Title)
            && MediaGrouping.NormalizeTitle(Title) == MediaGrouping.NormalizeTitle(movie.Title);
    }

    private static bool SameSourcePath(string left, string right) =>
        string.Equals(left.TrimEnd('/', '\\'), right.TrimEnd('/', '\\'),
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Coalesces the identities carried by Radarr's final-state webhooks. The existing
/// library refresh still owns path resolution/upsert, while reconciliation can limit
/// filesystem checks to the affected movie groups.
/// </summary>
public sealed class RadarrWebhookReconciliationQueue(Database db)
{
    private readonly ConcurrentDictionary<string, RadarrWebhookMovieIdentity> _pending =
        new(StringComparer.Ordinal);

    public void Enqueue(JsonElement payload)
    {
        var movie = payload.TryGetProperty("movie", out var value)
            && value.ValueKind == JsonValueKind.Object ? value : default;
        if (movie.ValueKind != JsonValueKind.Object)
        {
            _pending["all"] = new RadarrWebhookMovieIdentity(null, null, null, null, null, null, null, true);
            return;
        }

        var tmdbId = ReadId(movie, "tmdbId");
        var imdbId = ReadString(movie, "imdbId");
        var remoteId = ReadId(movie, "id");
        var path = ReadString(movie, "path") ?? ReadString(movie, "folderPath");
        var title = ReadString(movie, "title");
        var year = movie.TryGetProperty("year", out var yearElement)
            && yearElement.TryGetInt32(out var parsedYear) ? parsedYear : (int?)null;
        var instanceName = payload.TryGetProperty("instanceName", out var instanceElement)
            && instanceElement.ValueKind == JsonValueKind.String ? instanceElement.GetString() : null;
        var instanceId = string.IsNullOrWhiteSpace(instanceName) ? null : db.GetArrInstances("radarr")
            .FirstOrDefault(instance => string.Equals(instance.Name, instanceName,
                StringComparison.OrdinalIgnoreCase))?.Id;

        var identity = new RadarrWebhookMovieIdentity(
            tmdbId, imdbId, instanceId, remoteId, path, title, year);
        var key = !string.IsNullOrWhiteSpace(tmdbId) ? $"tmdb:{tmdbId}"
            : !string.IsNullOrWhiteSpace(imdbId) ? $"imdb:{imdbId.ToLowerInvariant()}"
            : !string.IsNullOrWhiteSpace(instanceId) && !string.IsNullOrWhiteSpace(remoteId)
                ? $"radarr:{instanceId}:{remoteId}"
            : !string.IsNullOrWhiteSpace(path) ? $"path:{path.TrimEnd('/', '\\').ToLowerInvariant()}"
            : year.HasValue && !string.IsNullOrWhiteSpace(title)
                ? $"fallback:{MediaGrouping.NormalizeTitle(title)}:{year}"
                : "all";
        _pending[key] = key == "all" ? identity with { MatchAll = true } : identity;
    }

    internal IReadOnlyList<RadarrWebhookMovieIdentity> Drain()
    {
        var result = new List<RadarrWebhookMovieIdentity>();
        foreach (var entry in _pending)
            if (_pending.TryRemove(entry.Key, out var identity)) result.Add(identity);
        return result;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string? ReadId(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }
}
