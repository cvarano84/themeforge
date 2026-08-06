using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentCompatibilityCollection
{
    public const string Name = "Environment compatibility";
}

[Collection(EnvironmentCompatibilityCollection.Name)]
public sealed class BrandingCompatibilityTests
{
    [Fact]
    public void Product_brand_has_the_official_identity()
    {
        Assert.Equal("ThemeForge", ProductBrand.Name);
        Assert.Equal("ChrisFlix Labs", ProductBrand.Organization);
        Assert.Equal("Movie and TV theme automation by ChrisFlix Labs", ProductBrand.Tagline);
    }

    [Fact]
    public void Legacy_environment_alias_still_works_and_warns_without_disclosing_its_value()
    {
        var currentName = "THEMEFORGE_TEST_" + Guid.NewGuid().ToString("N");
        var legacyName = "THEMEARR_TEST_" + Guid.NewGuid().ToString("N");
        const string secret = "legacy-secret-value";
        string? warning = null;
        Environment.SetEnvironmentVariable(legacyName, secret);
        try
        {
            var result = CompatibilityConfiguration.EnvironmentValue(currentName, legacyName, message => warning = message);
            Assert.Equal(secret, result);
            Assert.Contains(legacyName, warning);
            Assert.Contains(currentName, warning);
            Assert.DoesNotContain(secret, warning);
        }
        finally
        {
            Environment.SetEnvironmentVariable(legacyName, null);
        }
    }

    [Fact]
    public void New_environment_name_takes_precedence_over_legacy_name()
    {
        var currentName = "THEMEFORGE_TEST_" + Guid.NewGuid().ToString("N");
        var legacyName = "THEMEARR_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(currentName, "new-value");
        Environment.SetEnvironmentVariable(legacyName, "old-value");
        try
        {
            Assert.Equal("new-value", CompatibilityConfiguration.EnvironmentValue(currentName, legacyName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(currentName, null);
            Environment.SetEnvironmentVariable(legacyName, null);
        }
    }

    [Fact]
    public void Legacy_auth_environment_variable_authenticates_existing_installations()
    {
        const string legacyToken = "legacy-auth-token-at-least-16";
        var previousNew = Environment.GetEnvironmentVariable("THEMEFORGE_AUTH_TOKEN");
        var previousLegacy = Environment.GetEnvironmentVariable("THEMEARR_AUTH_TOKEN");
        Environment.SetEnvironmentVariable("THEMEFORGE_AUTH_TOKEN", null);
        Environment.SetEnvironmentVariable("THEMEARR_AUTH_TOKEN", legacyToken);
        try
        {
            var config = new ConfigurationBuilder().Build();
            Assert.Equal(legacyToken, Encoding.UTF8.GetString(
                ApiAuthMiddleware.LoadToken(config, NullLogger.Instance)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("THEMEFORGE_AUTH_TOKEN", previousNew);
            Environment.SetEnvironmentVariable("THEMEARR_AUTH_TOKEN", previousLegacy);
        }
    }

    [Fact]
    public void New_auth_environment_variable_wins_when_both_names_are_set()
    {
        const string currentToken = "current-auth-token-at-least-16";
        var previousNew = Environment.GetEnvironmentVariable("THEMEFORGE_AUTH_TOKEN");
        var previousLegacy = Environment.GetEnvironmentVariable("THEMEARR_AUTH_TOKEN");
        Environment.SetEnvironmentVariable("THEMEFORGE_AUTH_TOKEN", currentToken);
        Environment.SetEnvironmentVariable("THEMEARR_AUTH_TOKEN", "legacy-auth-token-at-least-16");
        try
        {
            var config = new ConfigurationBuilder().Build();
            Assert.Equal(currentToken, Encoding.UTF8.GetString(
                ApiAuthMiddleware.LoadToken(config, NullLogger.Instance)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("THEMEFORGE_AUTH_TOKEN", previousNew);
            Environment.SetEnvironmentVariable("THEMEARR_AUTH_TOKEN", previousLegacy);
        }
    }

    [Fact]
    public void Legacy_configuration_section_remains_a_fallback()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Themearr:VersionFile"] = "/legacy/VERSION",
        }).Build();

        Assert.Equal("/legacy/VERSION", CompatibilityConfiguration.Setting(config, "VersionFile"));
    }

    [Fact]
    public void ThemeForge_configuration_section_takes_precedence()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ThemeForge:VersionFile"] = "/new/VERSION",
            ["Themearr:VersionFile"] = "/legacy/VERSION",
        }).Build();

        Assert.Equal("/new/VERSION", CompatibilityConfiguration.Setting(config, "VersionFile"));
    }

    [Fact]
    public void Existing_db_path_continues_to_load_persistent_data()
    {
        using var dir = new TempDir();
        var databasePath = Path.Combine(dir.Path, "themearr.db");
        var previousNew = Environment.GetEnvironmentVariable("THEMEFORGE_DB_PATH");
        var previousLegacy = Environment.GetEnvironmentVariable("DB_PATH");
        Environment.SetEnvironmentVariable("THEMEFORGE_DB_PATH", null);
        Environment.SetEnvironmentVariable("DB_PATH", databasePath);
        try
        {
            var config = new ConfigurationBuilder().Build();
            var resolved = CompatibilityConfiguration.DatabasePath(config);
            var database = new Database(resolved);
            database.Init();
            database.SetSetting("compatibility_probe", "preserved");

            var reopened = new Database(resolved);
            reopened.Init();
            Assert.Equal("preserved", reopened.GetSetting("compatibility_probe"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("THEMEFORGE_DB_PATH", previousNew);
            Environment.SetEnvironmentVariable("DB_PATH", previousLegacy);
        }
    }
}
