using System.Diagnostics;
using System.Text;

namespace Themearr.API.Services;

public sealed record ExternalProcessRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout,
    Action<string>? OnStandardOutput = null,
    Action<string>? OnStandardError = null);

public sealed record ExternalProcessResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Cancelled);

public interface IExternalProcessRunner
{
    Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken ct = default);
}

public sealed class ExternalProcessStartException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Runs only explicitly named executables. It never invokes a command shell.</summary>
public sealed class ExternalProcessRunner : IExternalProcessRunner
{
    private const int MaxCapturedCharacters = 64 * 1024;

    internal static ProcessStartInfo CreateStartInfo(ExternalProcessRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);

        // Do not give a downloader process access to the API token, database path, or
        // any other application environment variable. Callers supply a minimal list.
        startInfo.Environment.Clear();
        foreach (var (name, value) in request.Environment)
            startInfo.Environment[name] = value;

        return startInfo;
    }

    public async Task<ExternalProcessResult> RunAsync(
        ExternalProcessRequest request, CancellationToken ct = default)
    {
        if (request.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "Process timeout must be positive.");

        using var process = new Process { StartInfo = CreateStartInfo(request) };
        try
        {
            if (!process.Start())
                throw new ExternalProcessStartException("The external process could not be started.");
        }
        catch (ExternalProcessStartException)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ExternalProcessStartException("The configured executable could not be started.", ex);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutTask = ReadLinesAsync(process.StandardOutput, stdout, request.OnStandardOutput);
        var stderrTask = ReadLinesAsync(process.StandardError, stderr, request.OnStandardError);

        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var timedOut = false;
        var cancelled = false;

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = ct.IsCancellationRequested;
            timedOut = !cancelled && timeoutCts.IsCancellationRequested;
            TryKillTree(process);
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { /* best effort */ }
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        return new ExternalProcessResult(
            process.HasExited ? process.ExitCode : null,
            stdout.ToString(), stderr.ToString(), timedOut, cancelled);
    }

    private static async Task ReadLinesAsync(
        StreamReader reader, StringBuilder capture, Action<string>? callback)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            lock (capture)
            {
                if (capture.Length < MaxCapturedCharacters)
                {
                    var remaining = MaxCapturedCharacters - capture.Length;
                    var value = line.Length <= remaining ? line : line[..remaining];
                    capture.AppendLine(value);
                }
            }
            // A progress consumer must never stop either redirected pipe from being
            // drained; otherwise a chatty child can block forever on a full buffer.
            try { callback?.Invoke(line); } catch { /* output draining takes precedence */ }
        }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process may have exited between HasExited and Kill. WaitForExit below
            // still drains both redirected streams.
        }
    }
}
