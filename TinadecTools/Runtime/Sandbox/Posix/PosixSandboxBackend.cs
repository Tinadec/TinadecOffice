using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecTools.Runtime.Sandbox.Posix;

/// <summary>
/// Confines <c>command_run</c> / <c>shell</c> on Linux and macOS.
///
/// <para>
/// <b>Linux</b> — Landlock is installed by the sandboxed process itself, which is why this
/// platform needs a launcher: restrictions can only be attached between fork and exec, and
/// .NET exposes no hook there. The parent re-execs this same binary with a reserved first
/// argument (<see cref="LinuxSandboxLauncher.ModeArg"/>); the launcher child sets its own
/// process group, asks to die with its parent, installs Landlock, and only then execve's the
/// commanded binary. Nothing about the command can run before the confinement is in place.
/// </para>
/// <para>
/// <b>macOS</b> — <c>/usr/bin/sandbox-exec</c> already is that launcher: it takes a seatbelt
/// profile and execs the target under it, so the parent spawns it directly.
/// </para>
/// <para>
/// The confined surface is writes (create/remove/rename/truncate outside the granted paths),
/// matching what the Windows backend guarantees. Read and execute stay unconfined on both
/// platforms; <see cref="LandlockApi"/> records why.
/// </para>
/// </summary>
internal sealed class PosixSandboxBackend : ISandboxBackend
{
    private const int StreamCapChars = 65_536;
    private const string SandboxExecPath = "/usr/bin/sandbox-exec";

    internal PosixSandboxBackend()
    {
    }

    public bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>No account, no elevation, no setup step: confinement is per-process.</summary>
    public bool IsInitialized => true;

    public Task EnsureSetupAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task<SandboxRunnerResponse> ExecuteAsync(
        SandboxRunnerRequest request,
        SandboxPermissions permissions,
        bool persistGrants,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var launch = BuildLaunch(request, permissions);

        using var process = new Process { StartInfo = launch };
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
                Error = ex.Message,
                DurationMs = stopwatch.ElapsedMilliseconds
            };
        }

        var stdoutTask = ReadLimitedAsync(process.StandardOutput);
        var stderrTask = ReadLimitedAsync(process.StandardError);

        if (request.Stdin is not null)
            await process.StandardInput.WriteAsync(request.Stdin.AsMemory(), ct).ConfigureAwait(false);
        process.StandardInput.Close();

        var timedOut = false;
        using (var timeoutCts = new CancellationTokenSource(request.TimeoutMs))
        using (timeoutCts.Token.Register(() => Terminate(process)))
        {
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                Terminate(process);
            }
        }

        // Wait for the pipes to drain after the exit is observed, or the last chunk of a
        // killed child's output is dropped.
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return new SandboxRunnerResponse
        {
            Success = !timedOut && process.HasExited && process.ExitCode == 0,
            ExitCode = timedOut ? -1 : (process.HasExited ? process.ExitCode : -1),
            Stdout = stdout.Text,
            Stderr = stderr.Text,
            TimedOut = timedOut,
            StdoutTruncated = stdout.Truncated,
            StderrTruncated = stderr.Truncated,
            DurationMs = stopwatch.ElapsedMilliseconds,
            Error = timedOut ? $"Command timed out after {request.TimeoutMs}ms and its process group was terminated." : null
        };
    }

    public Task<SandboxStreamingProcess> StartStreamingAsync(
        SandboxRunnerRequest request,
        SandboxPermissions permissions,
        CancellationToken ct)
    {
        var launch = BuildLaunch(request, permissions);
        var process = new Process { StartInfo = launch };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The sandboxed process did not start.");
        }

        return Task.FromResult(new SandboxStreamingProcess(process, new ProcessTerminator(process)));
    }

    public Task ResetAsync(SandboxResetScope scope, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Turns a request into a ProcessStartInfo: on macOS by prefixing sandbox-exec with a
    /// generated profile, on Linux by handing the payload to this binary's launcher mode.
    /// Internal for tests — the profile and the payload are the two things worth pinning.
    /// </summary>
    internal static ProcessStartInfo BuildLaunch(SandboxRunnerRequest request, SandboxPermissions permissions)
    {
        // The raw tail is a Windows contract (cmd.exe reparses its own command
        // line); POSIX launches every command as execve argv, so a payload
        // carrying one is a protocol error, not something to silently drop.
        if (request.ArgumentString is not null)
            throw new NotSupportedException("argument_string is Windows-only; POSIX launches commands as argv.");

        var environment = request.Environment ?? SandboxEnvironment.Build(null, permissions.EnvironmentVariableNames);
        var writePaths = WriteTargets(request, permissions, environment);

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsMacOS())
        {
            psi.FileName = SandboxExecPath;
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(SeatbeltProfile.Build(writePaths));
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(request.Executable);
            foreach (var arg in request.Arguments)
                psi.ArgumentList.Add(arg);
            ApplyEnvironment(psi, environment);
            return psi;
        }

        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The POSIX sandbox backend runs on Linux and macOS only.");

        // The launcher builds its own argv/envp for execve, so the payload carries them.
        var payload = new LinuxSandboxPayload
        {
            Executable = request.Executable,
            Arguments = request.Arguments,
            WorkingDirectory = request.WorkingDirectory,
            Environment = environment,
            WritePaths = writePaths
        };
        psi.FileName = LauncherExecutable()
            ?? throw new InvalidOperationException("Cannot resolve this process's executable path for the sandbox launcher.");
        psi.ArgumentList.Add(LinuxSandboxLauncher.ModeArg);
        psi.ArgumentList.Add(Convert.ToBase64String(
            JsonSerializer.SerializeToUtf8Bytes(payload, SandboxJsonContext.Default.LinuxSandboxPayload)));
        return psi;
    }

    /// <summary>
    /// The executable that carries launcher mode. In production that is the tool host itself —
    /// the payload is handed back to this same binary — which is why the default is
    /// <see cref="Environment.ProcessPath"/>. Tests repoint it at the TinadecTools apphost beside
    /// the test runner, because the test host's own entry point belongs to xunit and does not
    /// recognise <see cref="LinuxSandboxLauncher.ModeArg"/>.
    /// </summary>
    internal static Func<string?> LauncherExecutable { get; set; } = static () => Environment.ProcessPath;

    /// <summary>
    /// Where the command may write: the declared grants plus the temporary directory and
    /// /dev/null. A shell that cannot redirect to /dev/null is broken in a way the user
    /// would read as a defect in the sandbox, and the same argument covers its scratch files.
    /// </summary>
    internal static List<string> WriteTargets(
        SandboxRunnerRequest request,
        SandboxPermissions permissions,
        IReadOnlyDictionary<string, string> environment)
    {
        var targets = new List<string>(permissions.WritePaths);
        // The cache dirs Build() just pointed NPM_CONFIG_CACHE and friends at have to be
        // writable, or the redirect becomes a command that fails for a reason nobody can see.
        targets.Add(SandboxEnvironment.SandboxCacheDirectory(environment));
        foreach (var key in new[] { "TMPDIR", "TEMP", "TMP" })
        {
            if (environment.TryGetValue(key, out var temp) && !string.IsNullOrWhiteSpace(temp) && Directory.Exists(temp))
                targets.Add(Path.TrimEndingDirectorySeparator(temp));
        }
        if (File.Exists("/dev/null")) targets.Add("/dev/null");
        return targets
            .Select(Path.TrimEndingDirectorySeparator)
            .Distinct(PosixPathComparer)
            .ToList();
    }

    internal static readonly StringComparer PosixPathComparer = StringComparer.Ordinal;

    private static void ApplyEnvironment(ProcessStartInfo psi, IReadOnlyDictionary<string, string> environment)
    {
        psi.Environment.Clear();
        foreach (var (key, value) in environment)
            psi.Environment[key] = value;
    }

    /// <summary>
    /// Kills the confined process tree. On Linux the launcher put the command in a group of
    /// its own, so one signal reaches every descendant — but only after confirming the child
    /// really leads that group: signalling <c>-pid</c> while the group still equals the tool
    /// host's own would take the host down with it. Anything else falls back to a tree sweep.
    /// </summary>
    private static void Terminate(Process process)
    {
        try
        {
            if (process.HasExited) return;

            var pid = process.Id;
            if (OperatingSystem.IsLinux())
            {
                var pgid = PosixSysCalls.GetPgid(pid);
                if (pgid == pid)
                {
                    PosixSysCalls.Kill(-pgid, PosixSysCalls.SIGTERM);
                    if (!process.WaitForExit(2_000))
                        PosixSysCalls.Kill(-pgid, PosixSysCalls.SIGKILL);
                    return;
                }
            }

            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Termination is best effort; the caller has already decided the command is over.
        }
    }

    private static async Task<CapturedText> ReadLimitedAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            var available = StreamCapChars - builder.Length;
            if (available > 0)
                builder.Append(buffer, 0, Math.Min(available, read));
            if (read > available)
                truncated = true;
        }
        return new CapturedText(builder.ToString(), truncated);
    }

    private sealed record CapturedText(string Text, bool Truncated);

    private sealed class ProcessTerminator(Process process) : IDisposable
    {
        public void Dispose()
        {
            Terminate(process);
            try { process.WaitForExit(2_000); } catch { }
            process.Dispose();
        }
    }
}

/// <summary>The request a Linux launcher child needs before it execve's the command.</summary>
internal sealed class LinuxSandboxPayload
{
    [JsonPropertyName("executable")] public string Executable { get; set; } = string.Empty;
    [JsonPropertyName("arguments")] public List<string> Arguments { get; set; } = new();
    [JsonPropertyName("working_directory")] public string WorkingDirectory { get; set; } = string.Empty;
    [JsonPropertyName("environment")] public Dictionary<string, string> Environment { get; set; } = new();
    [JsonPropertyName("write_paths")] public List<string> WritePaths { get; set; } = new();
}
