using System.Diagnostics;
using System.Text.Json;
using TinadecTools.Abstractions;
using TinadecTools.Runtime.Sandbox;
using TinadecTools.Tools.Command;

namespace TinadecTools.Tests;

public sealed class ShellToolTests
{
    private static long _nextCallId = 50_000;

    private static ToolCallRequest<JsonElement> ShellRequest(string paramsJson, bool approved = true)
    {
        ShellToolRegistration.Register();
        using var doc = JsonDocument.Parse(paramsJson);
        return new ToolCallRequest<JsonElement>
        {
            ToolId = "shell",
            SessionId = "shell-test",
            ToolCallId = Interlocked.Increment(ref _nextCallId),
            Approved = approved,
            Params = doc.RootElement.Clone()
        };
    }

    // ── A1: cmd quoting ────────────────────────────────────────────────────────

    [Fact]
    public void ResolveShell_Windows_WrapsCommandWithoutBackslashEscapes()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (fileName, arguments) = ShellToolRegistration.ResolveShell("echo \"hi\"");

        Assert.Equal("cmd.exe", fileName);
        // cmd /d /s /c strips exactly the outer quote pair; inner quotes pass through.
        Assert.Equal("/d /s /c \"echo \"hi\"\"", arguments);
        Assert.DoesNotContain("\\\"", arguments);
    }

    [Fact]
    public async Task Shell_EchoWithDoubleQuotes_CmdParsesInnerQuotes()
    {
        if (!OperatingSystem.IsWindows()) return;

        var response = await DispatchSandboxedAsync(ShellRequest("{\"command\":\"echo \\\"hi\\\"\"}"));

        Assert.True(response.IsSuccess);
        var result = response.Response;
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(0, result.GetProperty("exit_code").GetInt32());
        var stdout = result.GetProperty("stdout").GetString()!;
        Assert.Contains("\"hi\"", stdout);
        Assert.DoesNotContain("\\", stdout); // no stray backslashes from mis-escaping
    }

    [Fact]
    public async Task Shell_GitCommitStyleQuoting_QuotesSurviveToCmd()
    {
        if (!OperatingSystem.IsWindows()) return;

        var response = await DispatchSandboxedAsync(
            ShellRequest("{\"command\":\"echo git commit -m \\\"initial commit\\\"\"}"));

        Assert.True(response.IsSuccess);
        var stdout = response.Response.GetProperty("stdout").GetString()!;
        Assert.Contains("git commit -m \"initial commit\"", stdout);
    }

    // ── A2: wire-level failure vs embedded failure ─────────────────────────────

    [Fact]
    public async Task Shell_MissingCommand_IsWireFailure()
    {
        var response = await DispatchSandboxedAsync(ShellRequest("{}"));

        Assert.False(response.IsSuccess);
        Assert.Contains("command", response.Response.GetString());
    }

    [Fact]
    public async Task Shell_ProtectedBranchPush_IsWireFailure()
    {
        var response = await DispatchSandboxedAsync(ShellRequest("{\"command\":\"git push origin main\"}"));

        Assert.False(response.IsSuccess);
        Assert.Contains("protected_branch_push", response.Response.GetString());
    }

    [Fact]
    public async Task Shell_MissingWorkingDirectory_IsWireFailure()
    {
        var response = await DispatchSandboxedAsync(
            ShellRequest("{\"command\":\"echo hi\",\"cwd\":\"no-such-dir-xyz-abc\"}"));

        Assert.False(response.IsSuccess);
        Assert.Contains("does not exist", response.Response.GetString());
    }

    [Fact]
    public async Task Shell_Timeout_IsWireFailure()
    {
        var command = OperatingSystem.IsWindows()
            ? "ping -n 30 127.0.0.1 >nul"
            : "sleep 30";
        var response = await DispatchSandboxedAsync(
            ShellRequest($"{{\"command\":\"{command}\",\"timeout_ms\":500}}"));

        Assert.False(response.IsSuccess);
        Assert.Contains("timed out", response.Response.GetString());
    }

    [Fact]
    public async Task Shell_NotApproved_IsWireFailure()
    {
        var response = await DispatchSandboxedAsync(
            ShellRequest("{\"command\":\"echo hi\"}", approved: false));

        Assert.False(response.IsSuccess);
        Assert.Equal(NotApprovedResponse.MESSAGE, response.Response.GetString());
    }

    [Fact]
    public async Task Shell_NonZeroExit_IsWireSuccessWithEmbeddedFailure()
    {
        var response = await DispatchSandboxedAsync(ShellRequest("{\"command\":\"exit 3\"}"));

        // The command really executed: wire success, embedded failure + exit code,
        // snake_case fields unchanged (Core deserializes ShellToolResult and
        // registers terminal_session_id).
        Assert.True(response.IsSuccess);
        var result = response.Response;
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(3, result.GetProperty("exit_code").GetInt32());
        Assert.Equal("completed", result.GetProperty("status").GetString());
        Assert.Equal("exit 3", result.GetProperty("command").GetString());
        Assert.True(result.TryGetProperty("terminal_session_id", out var sessionId));
        Assert.False(string.IsNullOrEmpty(sessionId.GetString()));
    }

    [Fact]
    public async Task Shell_SuccessfulCommand_IsWireSuccess()
    {
        var response = await DispatchSandboxedAsync(ShellRequest("{\"command\":\"exit 0\"}"));

        Assert.True(response.IsSuccess);
        var result = response.Response;
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(0, result.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task Shell_LongLived_UsesTheStreamingSandboxBackend_AndCanBeKilled()
    {
        ShellToolRegistration.Register();
        var backend = new ShellTestSandboxBackend();
        using var _ = CommandSandboxRuntime.OverrideBackendForTests(backend);
        var response = await ToolRegistry.DispatchAsync(ShellRequest(
            OperatingSystem.IsWindows()
                ? "{\"command\":\"ping -n 30 127.0.0.1 >nul\",\"long_lived\":true}"
                : "{\"command\":\"sleep 30\",\"long_lived\":true}"));

        Assert.True(response.IsSuccess, response.Response.ToString());
        Assert.True(backend.StreamingStartRequested);
        var result = response.Response;
        Assert.Equal("long_lived", result.GetProperty("status").GetString());
        var terminalSessionId = result.GetProperty("terminal_session_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(terminalSessionId));

        var killed = await ToolRegistry.DispatchAsync(new ToolCallRequest<JsonElement>
        {
            ToolId = "#terminal",
            SessionId = "shell-test",
            ToolCallId = Interlocked.Increment(ref _nextCallId),
            Params = JsonSerializer.SerializeToElement(new { action = "kill", terminal_session_id = terminalSessionId })
        });
        Assert.True(killed.IsSuccess);
    }

    private static async Task<ToolCallResponse<JsonElement>> DispatchSandboxedAsync(ToolCallRequest<JsonElement> request)
    {
        using var backend = CommandSandboxRuntime.OverrideBackendForTests(new ShellTestSandboxBackend());
        return await ToolRegistry.DispatchAsync(request);
    }

    private sealed class ShellTestSandboxBackend : ISandboxBackend
    {
        public bool IsSupported => true;
        public bool IsInitialized => true;
        public bool StreamingStartRequested { get; private set; }
        public Task EnsureSetupAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ResetAsync(SandboxResetScope scope, CancellationToken ct) => Task.CompletedTask;

        public Task<SandboxRunnerResponse> ExecuteAsync(SandboxRunnerRequest request, SandboxPermissions permissions,
            bool persistGrants, CancellationToken ct)
        {
            var command = request.Arguments.LastOrDefault() ?? string.Empty;
            if (command.Contains("ping", StringComparison.OrdinalIgnoreCase) || command.Contains("sleep", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new SandboxRunnerResponse { Success = false, TimedOut = true, ExitCode = -1, Error = $"Command timed out after {request.TimeoutMs}ms." });
            if (command.Contains("exit 3", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new SandboxRunnerResponse { Success = false, ExitCode = 3, Stderr = "exit 3" });
            var stdout = command.Contains("echo", StringComparison.OrdinalIgnoreCase)
                ? command[(command.IndexOf("echo", StringComparison.OrdinalIgnoreCase) + 4)..].Trim() + "\n"
                : string.Empty;
            return Task.FromResult(new SandboxRunnerResponse { Success = true, ExitCode = 0, Stdout = stdout });
        }

        public Task<SandboxStreamingProcess> StartStreamingAsync(
            SandboxRunnerRequest request,
            SandboxPermissions permissions,
            CancellationToken ct)
        {
            StreamingStartRequested = true;
            var psi = new ProcessStartInfo(request.Executable)
            {
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };
            foreach (var argument in request.Arguments) psi.ArgumentList.Add(argument);
            var process = Process.Start(psi) ?? throw new InvalidOperationException("test process did not start");
            return Task.FromResult(new SandboxStreamingProcess(process, new NoopCleanup()));
        }

        private sealed class NoopCleanup : IDisposable
        {
            public void Dispose() { }
        }
    }
}
