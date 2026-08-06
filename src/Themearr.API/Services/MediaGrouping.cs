using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Themearr.API.Data;

namespace Themearr.API.Services;

/// <summary>
/// Builds logical media groups over the existing physical-row schema. Keeping grouping
/// here (rather than baking it into folder identity) prevents Arr numeric-id collisions
/// today and allows a later normalized media_locations table without changing the API.
/// </summary>
public static partial class MediaGrouping
{
    public static List<Dictionary<string, object?>> Group(
        IEnumerable<Dictionary<string, object?>> physicalRows,
        IEnumerable<ArrInstance> instances,
        bool shows)
    {
        var priorities = instances.ToDictionary(i => i.Id, i => i, StringComparer.Ordinal);
        return physicalRows
            .GroupBy(row => GroupKey(row, shows), StringComparer.Ordinal)
            .Select(group => BuildGroup(group.ToList(), priorities, shows))
            .OrderBy(row => row.GetValueOrDefault("title")?.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string GroupKey(IReadOnlyDictionary<string, object?> row, bool shows)
    {
        var source = row.GetValueOrDefault("source")?.ToString() ?? "";
        // Plex stays exactly as it was: each folder is independent unless a future Plex
        // metadata migration explicitly opts it into logical grouping.
        if (source is not ("radarr" or "sonarr"))
            return $"physical:{row.GetValueOrDefault("id")}";

        string Value(string key) => row.GetValueOrDefault(key)?.ToString()?.Trim() ?? "";
        if (shows)
        {
            if (Value("tvdbId") is { Length: > 0 } tvdb) return $"show:tvdb:{tvdb}";
            if (Value("tmdbId") is { Length: > 0 } tmdb) return $"show:tmdb:{tmdb}";
            if (Value("imdbId") is { Length: > 0 } imdb) return $"show:imdb:{imdb.ToLowerInvariant()}";
        }
        else
        {
            if (Value("tmdbId") is { Length: > 0 } tmdb) return $"movie:tmdb:{tmdb}";
            if (Value("imdbId") is { Length: > 0 } imdb) return $"movie:imdb:{imdb.ToLowerInvariant()}";
        }

        var title = NormalizeTitle(Value("title"));
        var year = Value("year");
        // Missing years are intentionally not merged: title-only matching is too broad
        // for remakes and different adaptations.
        return title.Length > 0 && year.Length > 0
            ? $"{(shows ? "show" : "movie")}:fallback:{title}:{year}"
            : $"physical:{Value("id")}";
    }

    public static string LogicalId(string groupKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(groupKey));
        return Convert.ToHexString(hash[..16]).ToLowerInvariant();
    }

    private static Dictionary<string, object?> BuildGroup(
        List<Dictionary<string, object?>> locations,
        IReadOnlyDictionary<string, ArrInstance> instances,
        bool shows)
    {
        var ordered = locations.OrderBy(row =>
        {
            var id = row.GetValueOrDefault("instanceId")?.ToString() ?? "";
            return instances.TryGetValue(id, out var instance) ? instance.Priority : int.MaxValue;
        }).ThenBy(row => row.GetValueOrDefault("id")?.ToString(), StringComparer.Ordinal).ToList();

        var representative = new Dictionary<string, object?>(ordered[0]);
        var statuses = ordered.Select(row => row.GetValueOrDefault("status")?.ToString() ?? "pending").ToList();
        var ignored = statuses.All(s => s == "ignored");
        var available = statuses.Where(s => s is not "unresolved").ToList();
        var downloaded = available.Count(s => s == "downloaded");
        var aggregate = ignored ? "ignored"
            : available.Count == 0 ? "unavailable"
            : downloaded == 0 ? "missing"
            : downloaded == available.Count ? "downloaded"
            : "partial";

        var key = GroupKey(representative, shows);
        representative["logicalId"] = LogicalId(key);
        representative["groupKey"] = key;
        representative["aggregateStatus"] = aggregate;
        representative["locationCount"] = ordered.Count;
        representative["qualityLabels"] = ordered
            .Select(row => row.GetValueOrDefault("qualityLabel")?.ToString())
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        representative["locations"] = ordered.Select(row =>
        {
            var copy = new Dictionary<string, object?>(row);
            var instanceId = row.GetValueOrDefault("instanceId")?.ToString() ?? "";
            copy["instanceName"] = instances.TryGetValue(instanceId, out var instance)
                ? instance.Name : null;
            copy["priority"] = instances.TryGetValue(instanceId, out instance)
                ? instance.Priority : int.MaxValue;
            return copy;
        }).ToList();
        return representative;
    }

    // Conservative: Unicode letters/digits are retained, punctuation becomes a single
    // space, and articles/words are not removed. This avoids fuzzy title-only merges.
    internal static string NormalizeTitle(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        normalized = NonWord().Replace(normalized, " ");
        return Spaces().Replace(normalized, " ").Trim();
    }

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonWord();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}
