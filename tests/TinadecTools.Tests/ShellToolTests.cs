using System.Text.Json;
using TinadecTools.Abstractions;
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

        var response = await ToolRegistry.DispatchAsync(ShellRequest("{\"command\":\"echo \\\"hi\\\"\"}"));

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

        var response = await ToolRegistry.DispatchAsync(
            ShellRequest("{\"command\":\"echo git commit -m \\\"initial commit\\\"\"}"));

        Assert.True(response.IsSuccess);
        var stdout = response.Response.GetProperty("stdout").GetString()!;
        Assert.Contains("git commit -m \"initial commit\"", stdout);
    }

    // ── A2: wire-level failure vs embedded failure ─────────────────────────────

    [Fact]
    public async Task Shell_MissingCommand_IsWireFailure()
    {
        var response = await ToolRegistry.DispatchAsync(ShellRequest("{}"));

        Assert.False(response.IsSuccess);
        Assert.Contains("command", response.Response.GetString());
    }

    [Fact]
    public async Task Shell_ProtectedBranchPush_IsWireFailure()
    {
        var response = await ToolRegistry.DispatchAsync(ShellRequest("{\"command\":\"git push origin main\"}"));

        Assert.False(response.IsSuccess);
        Assert.Contains("protected_branch_push", response.Response.GetString());
    }

    [Fact]
    public async Task Shell_MissingWorkingDirectory_IsWireFailure()
    {
        var response = await ToolRegistry.DispatchAsync(
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
        var response = await ToolRegistry.DispatchAsync(
            ShellRequest($"{{\"command\":\"{command}\",\"timeout_ms\":500}}"));

        Assert.False(response.IsSuccess);
        Assert.Contains("timed out", response.Response.GetString());
    }

    [Fact]
    public async Task Shell_NotApproved_IsWireFailure()
    {
        var response = await ToolRegistry.DispatchAsync(
            ShellRequest("{\"command\":\"echo hi\"}", approved: false));

        Assert.False(response.IsSuccess);
        Assert.Equal(NotApprovedResponse.MESSAGE, response.Response.GetString());
    }

    [Fact]
    public async Task Shell_NonZeroExit_IsWireSuccessWithEmbeddedFailure()
    {
        var response = await ToolRegistry.DispatchAsync(ShellRequest("{\"command\":\"exit 3\"}"));

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
        var response = await ToolRegistry.DispatchAsync(ShellRequest("{\"command\":\"exit 0\"}"));

        Assert.True(response.IsSuccess);
        var result = response.Response;
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(0, result.GetProperty("exit_code").GetInt32());
    }
}
