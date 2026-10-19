using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using TinadecTools.Tools.FileRW;

namespace TinadecTools.Runtime.Sandbox.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WindowsSandboxRunner
{
    internal const string RunnerModeArg = "--sandbox-runner";

    internal static bool IsRunnerMode(string[] args)
        => args.Length > 0 && args[0] == RunnerModeArg;

    internal static int RunRunner(string[] args)
    {
        using var authorization = EnterSpawnAuthorization(args);
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

    // These arguments are written by the ACL-owning parent at spawn time. They
    // cannot be changed by the command JSON arriving through the runner pipe.
    internal static IDisposable EnterSpawnAuthorization(string[] args)
    {
        if (args.Length < 1 || args[0] != RunnerModeArg || args.Length % 2 != 1)
            throw new InvalidDataException("Invalid sandbox runner authorization arguments.");
        var roots = new List<string>();
        for (var index = 1; index < args.Length; index += 2)
        {
            if (args[index] != "--workspace-root" || !Path.IsPathFullyQualified(args[index + 1]))
                throw new InvalidDataException("Sandbox runner source roots must be absolute host arguments.");
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[index + 1]));
            if (!Directory.Exists(root) || SandboxPaths.IsDiskRoot(root))
                throw new InvalidDataException("Invalid sandbox runner source folder.");
            roots.Add(root);
        }
        if (roots.Count == 0) roots.Add(WorkspacePathResolver.WorkspaceRoot);
        if (roots.Count > 32 || roots.Distinct(SandboxPaths.PathComparer).Count() != roots.Count)
            throw new InvalidDataException("Invalid sandbox runner source folder set.");
        var primary = roots.FindIndex(root => WorkspaceRootSet.IsWithin(root, WorkspacePathResolver.WorkspaceRoot));
        if (primary < 0) throw new InvalidDataException("The sandbox runner working directory has no host authorization.");
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteNumber("schema_version", 1);
            writer.WriteStartObject("settings"); writer.WriteEndObject();
            writer.WriteStartArray("allowed_tool_ids"); writer.WriteEndArray();
            writer.WriteString("working_directory", WorkspacePathResolver.WorkspaceRoot);
            writer.WriteStartArray("workspace_roots");
            for (var index = 0; index < roots.Count; index++)
            {
                writer.WriteStartObject(); writer.WriteString("id", "source-" + index);
                writer.WriteString("path", roots[index]); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteString("primary_root_id", "source-" + primary); writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return ToolExecutionContext.Enter(document.RootElement);
    }

    private static SandboxRunnerResponse ExecuteRunnerCommand(string json)
    {
        var request = System.Text.Json.JsonSerializer.Deserialize(
            json, SandboxJsonContext.Default.SandboxRunnerRequest)
            ?? throw new InvalidDataException("Sandbox runner request was empty.");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            SandboxRequestValidator.Validate(
                request.Executable,
                request.Arguments,
                request.WorkingDirectory,
                request.TimeoutMs,
                request.Environment,
                request.ArgumentString);
            if (request.Environment is null)
                throw new InvalidDataException("Sandbox runner environment is required.");
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

    // Internal for tests: the cmd.exe quoting rule can only be proven by a real
    // process, and this is the spawn site production uses. It runs as the current
    // user with a job object, so no sandbox-account setup is involved.
    internal static SandboxRunnerResponse RunSandboxedProcess(SandboxRunnerRequest request)
    {
        SandboxRequestValidator.Validate(
            request.Executable,
            request.Arguments,
            request.WorkingDirectory,
            request.TimeoutMs,
            request.Environment,
            request.ArgumentString);
        if (request.Environment is null)
            throw new InvalidDataException("Sandbox runner environment is required.");

        var psi = new ProcessStartInfo
        {
            FileName = request.Executable,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // The runner's own stdin carries the serialized request, so a sandboxed
            // child must never inherit it: an inherited live handle both leaks the
            // request pipe and can block the child's exit.
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        request.ApplyCommandLine(psi);

        psi.Environment.Clear();
        foreach (var kv in request.Environment)
            psi.Environment[kv.Key] = kv.Value;

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
        }
        process.StandardInput.Close();

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

        // A command such as `node --test` may spawn a test runner that inherits
        // the redirected stdout/stderr handles. Waiting for the parent process to
        // exit is necessary but not sufficient: the pipes stay open until every
        // descendant releases them, and one background/runner child can otherwise
        // hold the whole tool call until its outer timeout. Give the well-behaved
        // case a short drain window, then close the sandbox job before reading the
        // captured output.
        var output = Task.WhenAll(stdoutTask, stderrTask);
        if (!output.Wait(TimeSpan.FromSeconds(3)))
        {
            job.Kill();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            if (!output.Wait(TimeSpan.FromSeconds(2)))
            {
                return new SandboxRunnerResponse
                {
                    Success = false,
                    ExitCode = -1,
                    TimedOut = timedOut,
                    Error = "Sandboxed child processes kept output open after the command exited."
                };
            }
        }
        var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : new CapturedText(string.Empty, false);
        var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : new CapturedText(string.Empty, false);

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
}
