using System.Diagnostics;
using System.Text;

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

        var stdoutTask = ReadOutputAsync(process.StandardOutput, maxOutputChars);
        var stderrTask = ReadOutputAsync(process.StandardError, maxOutputChars);

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

        return new TerminalResult(
            Success: !timedOut && process.ExitCode == 0,
            ExitCode: timedOut ? -1 : process.ExitCode,
            Stdout: stdout.Content,
            Stderr: stderr.Content,
            StdoutTruncated: stdout.Truncated,
            StderrTruncated: stderr.Truncated,
            TimedOut: timedOut,
            DurationMs: stopwatch.ElapsedMilliseconds);
    }

    private static void KillSafe(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }

    private static async Task<CapturedOutput> ReadOutputAsync(StreamReader reader, int maxOutputChars)
    {
        var limit = Math.Max(0, maxOutputChars);
        var builder = new StringBuilder(Math.Min(limit, 4_096));
        var buffer = new char[4_096];
        var truncated = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
                break;

            var remaining = limit - builder.Length;
            if (remaining <= 0)
            {
                truncated = true;
                continue;
            }

            var captured = Math.Min(remaining, read);
            builder.Append(buffer, 0, captured);
            if (captured < read)
                truncated = true;
        }

        return new CapturedOutput(builder.ToString(), truncated);
    }

    private sealed record CapturedOutput(string Content, bool Truncated);
}
