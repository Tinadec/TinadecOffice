using System.Diagnostics;

namespace TinadecTools.Runtime;

internal sealed record TerminalResult(
    bool Success,
    int ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated,
    bool TimedOut,
    long DurationMs);

internal static class TerminalRunner
{
    public static async Task<TerminalResult> RunAsync(
        string executable,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        string? stdin = null,
        int timeoutMs = 30_000,
        CancellationToken cancellationToken = default,
        int maxOutputChars = 65_536)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            CreateNoWindow = true
        };

        if (arguments is not null)
        {
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new TerminalResult(
                Success: false,
                ExitCode: -1,
                Stdout: string.Empty,
                Stderr: ex.Message,
                StdoutTruncated: false,
                StderrTruncated: false,
                TimedOut: false,
                DurationMs: 0);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        using var timeoutCts = timeoutMs > 0
            ? new CancellationTokenSource(timeoutMs)
            : null;
        using var linkedCts = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            KillSafe(process);
        }
        catch (OperationCanceledException)
        {
            KillSafe(process);
            throw;
        }

        stopwatch.Stop();

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        var stdoutTruncated = stdout.Length > maxOutputChars;
        var stderrTruncated = stderr.Length > maxOutputChars;
        if (stdoutTruncated) stdout = stdout[..maxOutputChars];
        if (stderrTruncated) stderr = stderr[..maxOutputChars];

        return new TerminalResult(
            Success: !timedOut && process.ExitCode == 0,
            ExitCode: timedOut ? -1 : process.ExitCode,
            Stdout: stdout,
            Stderr: stderr,
            StdoutTruncated: stdoutTruncated,
            StderrTruncated: stderrTruncated,
            TimedOut: timedOut,
            DurationMs: stopwatch.ElapsedMilliseconds);
    }

    private static void KillSafe(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }
}
