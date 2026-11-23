using TinadecTools.Runtime.Sandbox;
using TinadecTools.Runtime.Sandbox.Posix;
using TinadecTools.Tools.FileRW;
using TinadecTools.Runtime;
using System.Text.Json;

namespace TinadecTools.Tests;

/// <summary>
/// Real-kernel evidence for the POSIX command sandbox: every case here spawns an actual confined
/// child and asks the kernel what it allowed. <see cref="PosixSandboxTests"/> pins the shape of
/// the seatbelt profile, the launcher payload and the Landlock right set — all of which can be
/// green while the mechanism itself does nothing. These are the cases that fail if Landlock stops
/// denying, if sandbox-exec stops accepting the generated profile, or if termination stops
/// reaching the process group.
///
/// They only execute on Linux and macOS (posix-core CI); on Windows they return immediately, and
/// the Windows backend keeps its own real-process coverage.
/// </summary>
[Collection("CommandSandbox")]
public sealed class PosixSandboxIntegrationTests : IDisposable
{
    private const string KeptVariable = "TINADEC_SANDBOX_IT_KEPT";
    private const string DroppedVariable = "TINADEC_SANDBOX_IT_DROPPED";
    private const string LauncherFailurePrefix = "tinadec-sandbox:";

    private static readonly Func<string?> DefaultLauncher = PosixSandboxBackend.LauncherExecutable;

    /// <summary>Write-granted directory: a temp subdir, which the backend grants anyway via TMPDIR.</summary>
    private readonly string _granted = Directory.CreateTempSubdirectory("tinadec-sbx-it-").FullName;

    /// <summary>
    /// The out-of-bounds target. It is deliberately NOT under the temp directory: the backend adds
    /// TMPDIR (and the sandbox cache under it) to every grant, so a temp path would be denied
    /// nothing. This directory sits beside the test binaries, where the runner user could write if
    /// the sandbox let it.
    /// </summary>
    private readonly string _outside = Path.Combine(AppContext.BaseDirectory, "sandbox-integration-outside-" + Guid.NewGuid().ToString("N"));

    /// <summary>What the last confined run actually was pointed at: the grant list, the launcher
    /// binary and TMPDIR. Both ubuntu readings so far said "exit 0, no file" or "exit 0, no
    /// output", which is only explainable by what the child could write and what it exec'd.</summary>
    private string _lastRun = "(no run yet)";

    public PosixSandboxIntegrationTests()
    {
        Directory.CreateDirectory(_outside);
        if (OnPosix)
        {
            // The launcher re-execs "this binary" in production. Under a test host that binary is
            // xunit's — on macOS the host answers to /Users/runner/.dotnet/dotnet, which takes the
            // reserved argument as a project path, exits 0 and confines nothing. Point it at the
            // TinadecTools apphost the project reference copies beside the test runner instead.
            PosixSandboxBackend.LauncherExecutable = static () => Path.Combine(AppContext.BaseDirectory, "TinadecTools");
        }
    }

    public void Dispose()
    {
        PosixSandboxBackend.LauncherExecutable = DefaultLauncher;
        Environment.SetEnvironmentVariable(KeptVariable, null);
        Environment.SetEnvironmentVariable(DroppedVariable, null);
        TryDelete(_granted);
        TryDelete(_outside);
    }

    [Fact]
    public void TheConfinementMechanismThisPlatformReliesOnIsPresent()
    {
        if (OperatingSystem.IsLinux())
        {
            // ABI 0 means landlock_create_ruleset is unavailable (kernel < 5.13, or the LSM is not
            // enabled), and the launcher then fails every command closed with exit 126. If this
            // goes red on a CI runner, the runner cannot verify Landlock at all — that is the
            // reading, not a flake.
            var abi = LandlockApi.QueryAbiVersion();
            Assert.True(abi >= 1, $"Landlock is unavailable on this runner (landlock ABI {abi}); the denial cases below prove nothing here.");
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            Assert.True(File.Exists("/usr/bin/sandbox-exec"), "/usr/bin/sandbox-exec is missing; the macOS backend has no launcher.");
            return;
        }

        // Windows: the WindowsSandboxBackend owns this surface.
    }

    [Fact]
    public async Task FrozenMultiFolderGrantsAllowBothSourcesAndDenyOutsideWrites()
    {
        if (!OnPosix) return;
        // Keep real sources outside the test apphost directory, which the launcher
        // deliberately rebinds read-only, and outside implicitly granted temp.
        var primary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "tinadec-primary-" + Guid.NewGuid().ToString("N"));
        var secondary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "tinadec-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(primary);
        Directory.CreateDirectory(secondary);
        try
        {
            using var context = ToolExecutionContext.Enter(JsonSerializer.SerializeToElement(new
            {
                schema_version = 1, storage_id = "multi-folder-posix", settings = new { }, allowed_tool_ids = Array.Empty<string>(),
                workspace_roots = new[] { new { id = "primary", path = primary }, new { id = "second", path = secondary } },
                primary_root_id = "primary"
            }));
            var permissions = CommandSandboxRuntime.BuildPermissions(null, null, null);
            var backend = new PosixSandboxBackend();
            var request = new SandboxRunnerRequest
            {
                Executable = "/bin/sh", Arguments = ["-c", $"set -e; echo started; pwd; printf first > '{primary}/same.txt'; printf second > '{secondary}/same.txt'"],
                WorkingDirectory = secondary, TimeoutMs = 10_000
            };
            // The extra source is outside both the primary root and implicit temp grants.
            var response = await backend.ExecuteAsync(request, permissions, false, CancellationToken.None);
            Assert.True(response.Success, $"{response.Error} {response.Stderr}");
            Assert.Contains(secondary, response.Stdout);
            Assert.Equal("first", File.ReadAllText(Path.Combine(primary, "same.txt")));
            Assert.Equal("second", File.ReadAllText(Path.Combine(secondary, "same.txt")));
            // Use a sibling outside every source, never an implicitly granted temp path.
            var outsideRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "tinadec-workspace-outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outsideRoot);
            try
            {
                request.Arguments = ["-c", $"echo started; printf denied > '{outsideRoot}/denied.txt'"];
                response = await backend.ExecuteAsync(request, permissions, false, CancellationToken.None);
                Assert.Contains("started", response.Stdout); Assert.False(response.Success);
                Assert.False(File.Exists(Path.Combine(outsideRoot, "denied.txt")));
            }
            finally { Directory.Delete(outsideRoot, true); }
        }
        finally { Directory.Delete(secondary, true); Directory.Delete(primary, true); }
    }

    [Fact]
    public async Task AWriteInsideTheGrantedDirectorySucceeds()
    {
        if (!OnPosix) return;

        var target = Path.Combine(_granted, "inside.txt");
        var response = await RunAsync($"echo started; printf written > '{target}'");

        // On macOS this doubles as "sandbox-exec accepted the generated profile": a profile it
        // cannot parse makes it exit non-zero without running anything, so the marker would be
        // missing and the file would not exist. The profile is quoted back on failure because the
        // first two readings showed a denial here that only the clause list can explain.
        Assert.True(
            response.Success,
            $"exit={response.ExitCode} stdout={response.Stdout} stderr={response.Stderr} error={response.Error} profile={SeatbeltProfile.Build([_granted])}");
        Assert.Contains("started", response.Stdout);
        Assert.Equal("written", File.ReadAllText(target));
    }

    [Fact]
    public async Task TheSandboxOutlivesItsRequestThreadAndNullDeviceRemainsUsable()
    {
        if (!OperatingSystem.IsLinux()) return;
        var backend = new PosixSandboxBackend();
        await backend.EnsureSetupAsync(CancellationToken.None);
        var target = Path.Combine(_granted, "after-request.txt");
        var started = new TaskCompletionSource<SandboxStreamingProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            try
            {
                started.SetResult(backend.StartStreamingAsync(new SandboxRunnerRequest
                {
                    Executable = "/bin/sh", Arguments = ["-c", $"sleep 1; cat < /dev/null; printf kept > '{target}'; echo written > /dev/null"],
                    WorkingDirectory = WorkspacePathResolver.WorkspaceRoot
                }, new SandboxPermissions { WritePaths = [_granted] }, CancellationToken.None).GetAwaiter().GetResult());
            }
            catch (Exception error) { started.SetException(error); }
        });
        caller.Start();
        using var sandbox = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(caller.Join(TimeSpan.FromSeconds(5)));
        var stdout = sandbox.Process.StandardOutput.ReadToEndAsync();
        var stderr = sandbox.Process.StandardError.ReadToEndAsync();
        sandbox.Process.StandardInput.Close();
        await sandbox.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(sandbox.Process.ExitCode == 0, $"exit={sandbox.Process.ExitCode} stdout={await stdout} stderr={await stderr}");
        Assert.Equal("kept", File.ReadAllText(target));
    }

    [Fact]
    public void SeatbeltProfile_CarriesTheCanonicalFormOfEveryGrant()
    {
        if (!OperatingSystem.IsMacOS()) return; // /var is only a symlink there

        var resolved = PosixSysCalls.RealPath(_granted);
        Assert.NotNull(resolved);

        var profile = SeatbeltProfile.Build([_granted]);

        // Both forms, because the command writes the declared path while seatbelt matches the
        // resolved one; the first runner reading proved .NET's resolver leaves the profile without
        // the second, and the write is then refused inside the directory it grants.
        Assert.Contains(_granted, profile);
        Assert.Contains(resolved!, profile);
    }

    [Fact]
    public async Task AWriteOutsideEveryGrantedDirectoryIsDeniedByTheKernel()
    {
        if (!OnPosix) return;

        var target = Path.Combine(_outside, "denied.txt");

        // Control first: prove this location is writable by an unconstrained process, otherwise
        // "the file is not there afterwards" could mean the directory was missing rather than the
        // kernel refusing. The ubuntu leg failed exactly this way the first time it ran.
        File.WriteAllText(target, "unconstrained");
        Assert.Equal("unconstrained", File.ReadAllText(target));
        File.Delete(target);

        var response = await RunAsync($"echo started; printf denied > '{target}'");

        Assert.Contains("started", response.Stdout);
        Assert.False(File.Exists(target), $"the sandbox let a command write outside its grants: target={target} run=[{_lastRun}]");
        Assert.False(
            response.Success,
            $"exit={response.ExitCode} stdout={response.Stdout} stderr={response.Stderr} error={response.Error} " +
                $"target={target} fileNowExists={File.Exists(target)} run=[{_lastRun}]");
        // A launcher that could not install Landlock also produces no file — but it never runs the
        // command, so "started" would be missing too. Naming the prefix keeps the two readings apart.
        Assert.DoesNotContain(LauncherFailurePrefix, response.Stderr);
    }

    [Fact]
    public async Task TheChildSeesTheRetainedEnvironmentAndNothingElse()
    {
        if (!OnPosix) return;

        Environment.SetEnvironmentVariable(KeptVariable, "kept-value");
        Environment.SetEnvironmentVariable(DroppedVariable, "dropped-value");

        // The probe is the control: without it, an empty stdout can be read as "the environment was
        // dropped" when the real reading is "the command never ran at all".
        var response = await RunAsync(
            $"printf '__tinadec_probe__\\n'; printf '%s|%s\\n' \"${KeptVariable}\" \"${DroppedVariable}\"; env",
            [KeptVariable]);

        Assert.True(
            response.Success,
            $"exit={response.ExitCode} stdout={response.Stdout} stderr={response.Stderr} error={response.Error} run=[{_lastRun}]");
        Assert.True(
            response.Stdout.Contains("__tinadec_probe__"),
            $"the confined child never ran the script; stdout={response.Stdout} stderr={response.Stderr} run=[{_lastRun}]");
        Assert.True(
            response.Stdout.Contains("kept-value"),
            $"stdout={response.Stdout} exit={response.ExitCode} stderr={response.Stderr} run=[{_lastRun}]");
        // env(1) lists the whole environment, so the dropped value appearing anywhere is a leak.
        Assert.DoesNotContain("dropped-value", response.Stdout);
        Assert.Contains("PATH=", response.Stdout);
    }

    [Fact]
    public async Task TerminationReachesTheWholeProcessGroup()
    {
        if (!OperatingSystem.IsLinux()) return; // macOS has no setpgid hook; see the class note in AGENTS.md.

        var pidFile = Path.Combine(_granted, "grandchild.pid");
        var running = RunAsync($"sleep 30 & echo $! > '{pidFile}'; wait", timeoutMs: 8_000);
        HostProcessIdentity? grandchild = null;
        var startedDeadline = DateTime.UtcNow.AddSeconds(5);
        while (grandchild is null && DateTime.UtcNow < startedDeadline)
        {
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var namespacePid))
                grandchild = FindHostGrandchild(pidFile, namespacePid);
            if (grandchild is null) await Task.Delay(50);
        }
        var response = await running;

        Assert.True(response.TimedOut, $"exit={response.ExitCode} stdout={response.Stdout} stderr={response.Stderr}");
        Assert.True(File.Exists(pidFile), $"the command never backgrounded its child; stdout={response.Stdout} stderr={response.Stderr}");
        Assert.NotNull(grandchild);

        // bwrap has a PID namespace: $! is not a host PID. Identify this exact sleep
        // through NSpid and its parent shell's unique script before termination.
        // A zombie is already terminated; kill(pid, 0) also succeeds for zombies.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && IsRunning(grandchild.Value))
            await Task.Delay(100);

        Assert.False(IsRunning(grandchild.Value), $"owned host grandchild {grandchild.Value.Id} is still running after timeout");
    }

    private readonly record struct HostProcessIdentity(int Id, string StartedAt);

    private static HostProcessIdentity? FindHostGrandchild(string scriptMarker, int namespacePid)
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("sleep"))
        {
            using (process)
            {
                try
                {
                    var stat = ReadHostStat(process.Id);
                    var nspids = File.ReadAllLines($"/proc/{process.Id}/status").Single(line => line.StartsWith("NSpid:", StringComparison.Ordinal))
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    var parentCommand = File.ReadAllText($"/proc/{stat.ParentId}/cmdline");
                    if (int.Parse(nspids[^1]) == namespacePid && parentCommand.Contains(scriptMarker, StringComparison.Ordinal)
                        && stat.State != "Z") return new(process.Id, stat.StartedAt);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        return null;
    }

    private static bool IsRunning(HostProcessIdentity process)
    {
        try
        {
            var stat = ReadHostStat(process.Id);
            return stat.StartedAt == process.StartedAt && stat.State != "Z";
        }
        catch (DirectoryNotFoundException) { return false; }
        catch (FileNotFoundException) { return false; }
    }

    private static (string State, int ParentId, string StartedAt) ReadHostStat(int pid)
    {
        var text = File.ReadAllText($"/proc/{pid}/stat");
        var fields = text[(text.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (fields[0], int.Parse(fields[1]), fields[19]);
    }

    private static bool OnPosix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private async Task<SandboxRunnerResponse> RunAsync(string script, string[]? extraEnvironmentNames = null, int timeoutMs = 30_000)
    {
        // Grants are built by hand rather than through CommandSandboxRuntime.BuildPermissions,
        // which would add the workspace root as a writable path and make the denial case meaningless.
        var permissions = new SandboxPermissions
        {
            ReadPaths = [_granted],
            WritePaths = [_granted],
            EnvironmentVariableNames = [.. extraEnvironmentNames ?? []]
        };

        _lastRun = string.Join(", ", permissions.WritePaths)
            + $" | TMPDIR={environmentProbe()} | launcher={PosixSandboxBackend.LauncherExecutable()}";

        return await CommandSandboxRuntime.ExecuteSandboxedAsync(
            "/bin/sh",
            ["-c", script],
            WorkspacePathResolver.WorkspaceRoot,
            stdin: null,
            timeoutMs,
            permissions,
            persistGrants: false,
            CancellationToken.None);
    }

    private static string environmentProbe()
    {
        var tmpdir = Environment.GetEnvironmentVariable("TMPDIR");
        return string.IsNullOrEmpty(tmpdir) ? Path.GetTempPath() : tmpdir;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
