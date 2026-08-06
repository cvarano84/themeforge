using System.Collections.Concurrent;

namespace Themearr.API.Services;

/// <summary>Single source of truth for display branding shared by server responses and logs.</summary>
public static class ProductBrand
{
    public const string Name = "ThemeForge";
    public const string ShortName = "ThemeForge";
    public const string Tagline = "Movie and TV theme automation by ChrisFlix Labs";
    public const string Organization = "ChrisFlix Labs";
    public const string Description = "Automatically discover, download, organize, and manage movie and TV theme music.";
}

/// <summary>
/// Resolves renamed environment variables without breaking existing installations.
/// New names always win; legacy names remain supported and produce a value-free warning.
/// </summary>
internal static class CompatibilityConfiguration
{
    private static readonly ConcurrentDictionary<string, byte> WarnedLegacyNames = new();

    public static string? EnvironmentValue(
        string currentName,
        string legacyName,
        Action<string>? warning = null)
    {
        var current = Environment.GetEnvironmentVariable(currentName);
        var legacy = Environment.GetEnvironmentVariable(legacyName);

        if (!string.IsNullOrWhiteSpace(legacy) && WarnedLegacyNames.TryAdd(legacyName, 0))
        {
            var message = $"{legacyName} is deprecated; use {currentName}. The legacy alias remains supported for upgrades.";
            (warning ?? Console.Error.WriteLine)(message);
        }

        return !string.IsNullOrWhiteSpace(current) ? current : legacy;
    }

    public static string? Setting(IConfiguration configuration, string name) =>
        configuration[$"ThemeForge:{name}"] ?? configuration[$"Themearr:{name}"];

    public static string DatabasePath(IConfiguration configuration) =>
        Environment.GetEnvironmentVariable("THEMEFORGE_DB_PATH")
        ?? Environment.GetEnvironmentVariable("DB_PATH")
        ?? Setting(configuration, "DbPath")
        ?? "/opt/themearr/data/themearr.db";
}
