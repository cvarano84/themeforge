using Themearr.API.Services;

namespace Themearr.API.Tests;

public sealed class ExternalProcessRunnerTests
{
    [Fact]
    public void Start_info_uses_argument_list_redirects_and_never_uses_a_shell()
    {
        var request = new ExternalProcessRequest("tool", ["one value", "; unsafe-looking text"],
            Path.GetTempPath(), new Dictionary<string, string> { ["ONLY"] = "value" }, TimeSpan.FromSeconds(1));

        var info = ExternalProcessRunner.CreateStartInfo(request);

        Assert.False(info.UseShellExecute);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.Equal(request.Arguments, info.ArgumentList.ToArray());
        Assert.Equal("value", info.Environment["ONLY"]);
        Assert.DoesNotContain(info.Environment.Keys, key => key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reads_stdout_and_stderr_asynchronously()
    {
        var dotnet = ExecutableLocator.Resolve("dotnet", "dotnet");
        if (dotnet is null) return;
        var seen = new List<string>();
        var result = await new ExternalProcessRunner().RunAsync(new ExternalProcessRequest(
            dotnet, ["--info"], Path.GetTempPath(),
            DownloaderConfiguration.MinimalProcessEnvironment(Path.GetTempPath()),
            TimeSpan.FromSeconds(10), line => seen.Add(line), line => seen.Add(line)));

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.NotEmpty(result.StandardOutput);
        Assert.NotEmpty(seen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_and_cancellation_terminate_the_process(bool cancel)
    {
        var dotnet = ExecutableLocator.Resolve("dotnet", "dotnet");
        if (dotnet is null) return;
        using var dir = new TempDir();
        var environment = new Dictionary<string, string>(
            DownloaderConfiguration.MinimalProcessEnvironment(dir.Path), StringComparer.OrdinalIgnoreCase)
        {
            ["THEMEARR_AUTH_TOKEN"] = "test-token-at-least-sixteen-characters",
            ["DB_PATH"] = Path.Combine(dir.Path, "process.db"),
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
        };
        using var cts = new CancellationTokenSource();
        if (cancel) cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        var result = await new ExternalProcessRunner().RunAsync(new ExternalProcessRequest(
            dotnet, [typeof(IThemeAudioProvider).Assembly.Location], dir.Path, environment,
            cancel ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(500)), cts.Token);

        Assert.Equal(cancel, result.Cancelled);
        Assert.Equal(!cancel, result.TimedOut);
        Assert.NotNull(result.ExitCode);
    }
}
