using System.Text.Json;
using TinadecTools.Abstractions;
using TinadecTools.Runtime;
using TinadecTools.Runtime.Sandbox;
using TinadecTools.Runtime.Sandbox.Windows;
using TinadecTools.Tools.Command;

namespace TinadecTools.Tests;

/// <summary>
/// The shell path has no argv to prepend: the user's command line belongs to cmd.exe or bash.
/// Suppression travels as GIT_CONFIG_* instead, through SandboxPermissions.EnvironmentOverrides.
/// These tests pin that the dictionary reaches the backend on both the standalone and the governed
/// path, and that a real git actually obeys it.
/// </summary>
public sealed class GitHookShellSuppressionTests
{
    private const string FailingHook = "#!/bin/sh\necho HOOK_RAN >&2\nexit 1\n";
    private static long _nextCallId = 70_000;

    private static JsonElement Context(string settings) => JsonDocument.Parse(
        "{\"schema_version\":1,\"settings_hash\":\"test\",\"settings\":" + settings
        + ",\"allowed_tool_ids\":[\"shell\"],\"read_roots\":[],\"mcp_servers\":[]}").RootElement;

    // Dispatch enters request.ExecutionContext, replacing whatever AsyncLocal context the caller
    // held. A governed call must therefore carry its settings on the request; entering a scope
    // around DispatchAsync is silently discarded and would make the allow_hooks test false-green.
    private static ToolCallRequest<JsonElement> ShellRequest(JsonElement? context = null)
    {
        ShellToolRegistration.Register();
        return new ToolCallRequest<JsonElement>
        {
            ToolId = "shell",
            SessionId = "hook-shell-test",
            ToolCallId = Interlocked.Increment(ref _nextCallId),
            Approved = true,
            ExecutionContext = context,
            Params = JsonDocument.Parse("{\"command\":\"echo ok\"}").RootElement.Clone()
        };
    }

    private static void AssertSuppression(Dictionary<string, string> overrides)
    {
        Assert.Equal("1", overrides["GIT_CONFIG_COUNT"]);
        Assert.Equal("core.hooksPath", overrides["GIT_CONFIG_KEY_0"]);
        var directory = overrides["GIT_CONFIG_VALUE_0"];
        Assert.True(Directory.Exists(directory), $"hooksPath must exist: {directory}");
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Fact]
    public async Task StandaloneShell_ReachesTheBackendWithHookSuppression()
    {
        var backend = new CapturingBackend();
        using var _ = CommandSandboxRuntime.OverrideBackendForTests(backend);

        var response = await ToolRegistry.DispatchAsync(ShellRequest());

        Assert.True(response.IsSuccess, response.Response.ToString());
        Assert.NotNull(backend.Permissions);
        AssertSuppression(backend.Permissions!.EnvironmentOverrides);
    }

    [Fact]
    public async Task GovernedShell_ReachesTheBackendWithHookSuppression()
    {
        var backend = new CapturingBackend();
        using var _ = CommandSandboxRuntime.OverrideBackendForTests(backend);

        var response = await ToolRegistry.DispatchAsync(ShellRequest(Context("{\"git\":{\"allow_hooks\":false}}")));

        Assert.True(response.IsSuccess, response.Response.ToString());
        Assert.NotNull(backend.Permissions);
        AssertSuppression(backend.Permissions!.EnvironmentOverrides);
    }

    [Fact]
    public async Task AllowHooksTrue_SendsNoSuppressionAtAll()
    {
        var backend = new CapturingBackend();
        using var _ = CommandSandboxRuntime.OverrideBackendForTests(backend);

        var response = await ToolRegistry.DispatchAsync(ShellRequest(Context("{\"git\":{\"allow_hooks\":true}}")));

        Assert.True(response.IsSuccess, response.Response.ToString());
        Assert.NotNull(backend.Permissions);
        Assert.Empty(backend.Permissions!.EnvironmentOverrides);
    }

    /// <summary>
    /// MergeWithPolicy rebuilds the permissions object field by field on the standalone path.
    /// It used to omit EnvironmentOverrides, which was invisible while nothing populated the
    /// field and silently drops the suppression now that something does.
    /// </summary>
    [Fact]
    public void MergeWithPolicy_StandalonePath_KeepsTheOverrides()
    {
        var requested = CommandSandboxRuntime.BuildPermissions(null, null, null);
        AssertSuppression(requested.EnvironmentOverrides);

        var merged = CommandSandboxRuntime.MergeWithPolicy(requested);

        AssertSuppression(merged.EnvironmentOverrides);
    }

    [Fact]
    public void MergeWithPolicy_GovernedPath_KeepsTheOverrides()
    {
        using var scope = ToolExecutionContext.Enter(Context("{}"), "shell");
        var requested = CommandSandboxRuntime.BuildPermissions(null, null, null);

        var merged = CommandSandboxRuntime.MergeWithPolicy(requested);

        AssertSuppression(merged.EnvironmentOverrides);
    }

    // The dispatch tests above prove the dictionary is produced; only a real git proves it is
    // obeyed. The repository configures its own core.hooksPath, so this also pins that GIT_CONFIG_*
    // outranks repository-level config rather than only working where none was set.
    [Fact]
    public void RealGitCommit_WithTheDispatchedOverrides_SkipsThePlantedHook()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var repo = new TempGitRepo("shell-hook-suppressed");
        repo.SeedInitialCommit();
        PlantFailingPreCommit(repo);

        var overrides = DispatchedOverrides(Context("{\"git\":{\"allow_hooks\":false}}"));
        var response = RunRealCmd("git commit --allow-empty -m \"agent commit\"", repo.Path, overrides);

        Assert.True(response.Success, $"stdout=[{response.Stdout}] stderr=[{response.Stderr}] error=[{response.Error}]");
        Assert.DoesNotContain("HOOK_RAN", response.Stderr, StringComparison.Ordinal);
        Assert.Equal("agent commit", repo.CaptureGit("log", "-1", "--format=%s").Trim());
    }

    [Fact]
    public void RealGitCommit_WithAllowHooksTrue_LetsThePlantedHookBlock()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var repo = new TempGitRepo("shell-hook-allowed");
        repo.SeedInitialCommit();
        PlantFailingPreCommit(repo);

        var overrides = DispatchedOverrides(Context("{\"git\":{\"allow_hooks\":true}}"));
        Assert.Empty(overrides);
        var response = RunRealCmd("git commit --allow-empty -m \"agent commit\"", repo.Path, overrides);

        Assert.False(response.Success);
        Assert.Contains("HOOK_RAN", response.Stderr, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> DispatchedOverrides(JsonElement context)
    {
        using var scope = ToolExecutionContext.Enter(context, "shell");
        return CommandSandboxRuntime.MergeWithPolicy(CommandSandboxRuntime.BuildPermissions(null, null, null)).EnvironmentOverrides;
    }

    private static void PlantFailingPreCommit(TempGitRepo repo)
    {
        var hooks = Path.Combine(repo.Path, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        File.WriteAllText(Path.Combine(hooks, "pre-commit"), FailingHook);
        repo.RunGit("config", "core.hooksPath", ".git/hooks");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static SandboxRunnerResponse RunRealCmd(string command, string workingDirectory, Dictionary<string, string> overrides)
    {
        var (executable, arguments, argumentString) = ShellToolRegistration.ResolveSandboxCommand(command);
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var temp = Environment.GetEnvironmentVariable("TEMP") ?? systemRoot;
        var environment = new Dictionary<string, string>
        {
            ["SystemRoot"] = systemRoot,
            ["TEMP"] = temp,
            ["TMP"] = temp,
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty
        };
        foreach (var entry in overrides) environment[entry.Key] = entry.Value;
        return WindowsSandboxRunner.RunSandboxedProcess(new SandboxRunnerRequest
        {
            Executable = executable,
            Arguments = arguments,
            ArgumentString = argumentString,
            WorkingDirectory = workingDirectory,
            TimeoutMs = 30_000,
            Environment = environment
        });
    }

    private sealed class CapturingBackend : ISandboxBackend
    {
        public SandboxPermissions? Permissions { get; private set; }
        public bool IsSupported => true;
        public bool IsInitialized => true;
        public Task EnsureSetupAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ResetAsync(SandboxResetScope scope, CancellationToken ct) => Task.CompletedTask;

        public Task<SandboxRunnerResponse> ExecuteAsync(SandboxRunnerRequest request, SandboxPermissions permissions,
            bool persistGrants, CancellationToken ct)
        {
            Permissions = permissions;
            return Task.FromResult(new SandboxRunnerResponse { Success = true, ExitCode = 0, Stdout = "ok\n" });
        }
    }
}