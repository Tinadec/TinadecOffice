using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TinadecTools.Runtime.Sandbox.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WindowsSandboxRunner
{
    internal const string RunnerModeArg = "--sandbox-runner";

    internal static bool IsRunnerMode(string[] args)
        => args.Length > 0 && args[0] == RunnerModeArg;

    internal static int RunRunner()
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        using var reader = new StreamReader(stdin);
        using var writer = new StreamWriter(stdout) { AutoFlush = true };

        try
        {
            writer.Write("READY\n");

            string line;
            while ((line = reader.ReadLine()!) != null)
            {
                if (line == "EXIT")
                    break;

                var result = ExecuteRunnerCommand(line);
                var json = System.Text.Json.JsonSerializer.Serialize(
                    result, SandboxJsonContext.Default.SandboxRunnerResponse);
                writer.Write(json + "\n");
                writer.Write("READY\n");
            }

            return 0;
        }
        catch (Exception ex)
        {
            var err = new SandboxRunnerResponse { Success = false, Error = ex.Message };
            var json = System.Text.Json.JsonSerializer.Serialize(
                err, SandboxJsonContext.Default.SandboxRunnerResponse);
            writer.Write(json + "\n");
            return 1;
        }
    }

    private static SandboxRunnerResponse ExecuteRunnerCommand(string json)
    {
        var request = System.Text.Json.JsonSerializer.Deserialize(
            json, SandboxJsonContext.Default.SandboxRunnerRequest)!;

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var response = RunSandboxedProcess(request);
            response.DurationMs = stopwatch.ElapsedMilliseconds;
            return response;
        }
        catch (Exception ex)
        {
            return new SandboxRunnerResponse
            {
                Success = false,
                Error = ex.Message,
                DurationMs = stopwatch.ElapsedMilliseconds
            };
        }
    }

    private static SandboxRunnerResponse RunSandboxedProcess(SandboxRunnerRequest request)
    {
        var psi = new ProcessStartInfo
        {
            FileName = request.Executable,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.Stdin is not null,
            CreateNoWindow = true
        };

        foreach (var arg in request.Arguments)
            psi.ArgumentList.Add(arg);

        if (request.Environment is not null)
        {
            psi.Environment.Clear();
            foreach (var kv in request.Environment)
                psi.Environment[kv.Key] = kv.Value;
        }

        using var job = new JobObjectManager();

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new SandboxRunnerResponse
            {
                Success = false,
                ExitCode = -1,
                Stderr = ex.Message
            };
        }

        job.Assign(process.Handle);

        var stdoutTask = ReadLimitedAsync(process.StandardOutput);
        var stderrTask = ReadLimitedAsync(process.StandardError);

        if (request.Stdin is not null)
        {
            process.StandardInput.Write(request.Stdin);
            process.StandardInput.Close();
        }

        using var timeoutCts = new CancellationTokenSource(request.TimeoutMs);

        var timedOut = false;
        try
        {
            process.WaitForExitAsync(timeoutCts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            job.Kill();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        return new SandboxRunnerResponse
        {
            Success = !timedOut && process.ExitCode == 0,
            ExitCode = timedOut ? -1 : process.ExitCode,
            Stdout = stdout.Text,
            Stderr = stderr.Text,
            TimedOut = timedOut,
            StdoutTruncated = stdout.Truncated,
            StderrTruncated = stderr.Truncated
        };
    }

    private static async Task<CapturedText> ReadLimitedAsync(StreamReader reader)
    {
        var builder = new System.Text.StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            var available = MaxStreamChars - builder.Length;
            if (available > 0)
                builder.Append(buffer, 0, Math.Min(available, read));
            if (read > available)
                truncated = true;
        }
        return new CapturedText(builder.ToString(), truncated);
    }

    private sealed record CapturedText(string Text, bool Truncated);

    private const int MaxStreamChars = 65_536;
    private const int MaxTimeoutMs = 1_800_000;
}
