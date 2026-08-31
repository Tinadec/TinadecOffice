using System.Text.Json;
using System.Text.Json.Serialization;
using TinadecTools.Abstractions;
using TinadecTools.Runtime;

namespace TinadecTools.Tools.Command;

// ── shell 工具：agent 终端执行入口 ────────────────────────────────────────────

/// <summary>
/// Registers the agent-facing <c>shell</c> tool and the reserved
/// <c>#terminal</c> session-control tool (stdin / kill / status).
///
/// The shell tool streams output to Core as wire events while the call is in
/// flight (the Core read loop routes them by call id), which is why it needs
/// the raw request instead of the source-generated typed wrapper.
/// </summary>
internal static class ShellToolRegistration
{
    internal const string ShellToolId = "shell";
    internal const string TerminalControlToolId = "#terminal";

    private const string ShellInputSchema =
        "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"}," +
        "\"cwd\":{\"type\":\"string\"},\"timeout_ms\":{\"type\":\"integer\"}," +
        "\"long_lived\":{\"type\":\"boolean\"}},\"required\":[\"command\"],\"additionalProperties\":false}";

    private const string TerminalControlInputSchema =
        "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"enum\":[\"write\",\"kill\",\"status\",\"replay\"]}," +
        "\"terminal_session_id\":{\"type\":\"string\"},\"data\":{\"type\":\"string\"}}," +
        "\"required\":[\"action\"],\"additionalProperties\":false}";

    public static void Register()
    {
        ToolRegistry.Register(
            ShellToolId,
            HandleShellAsync,
            requiresApproval: true,
            description: "Run a shell command in the workspace. Output streams to the conversation terminal; pass long_lived=true for dev-server style commands that must keep running.",
            inputSchemaJson: ShellInputSchema,
            risk: "high",
            mutatesWorkspace: true,
            retrySafety: "unsafe");

        ToolRegistry.Register(
            TerminalControlToolId,
            HandleTerminalControlAsync,
            requiresApproval: false,
            description: "Reserved transport control for terminal sessions (stdin, kill, status, replay).",
            inputSchemaJson: TerminalControlInputSchema,
            risk: "low",
            mutatesWorkspace: false,
            retrySafety: "safe");
    }

    private static (string FileName, string Arguments) ResolveShell(string command)
    {
        if (OperatingSystem.IsWindows())
            return ("cmd.exe", $"/d /s /c \"{command.Replace("\"", "\\\"")}\"");
        return ("/bin/bash", $"-lc {EscapeSingleQuoted(command)}");
    }

    private static string EscapeSingleQuoted(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleShellAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved)
        {
            return NotApproved(request.ToolCallId);
        }

        string command;
        string? cwd = null;
        var timeoutMs = 120_000;
        var longLived = false;
        try
        {
            if (request.Params is { ValueKind: JsonValueKind.Object } parameters)
            {
                command = parameters.TryGetProperty("command", out var commandElement) && commandElement.ValueKind == JsonValueKind.String
                    ? commandElement.GetString() ?? string.Empty
                    : string.Empty;
                if (parameters.TryGetProperty("cwd", out var cwdElement) && cwdElement.ValueKind == JsonValueKind.String)
                    cwd = cwdElement.GetString();
                if (parameters.TryGetProperty("timeout_ms", out var timeoutElement) && timeoutElement.TryGetInt32(out var parsedTimeout))
                    timeoutMs = Math.Clamp(parsedTimeout, 1, 1_800_000);
                if (parameters.TryGetProperty("long_lived", out var longLivedElement) && longLivedElement.ValueKind == JsonValueKind.True)
                    longLived = true;
            }
            else
            {
                command = string.Empty;
            }
        }
        catch (JsonException)
        {
            command = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return Fail(request.ToolCallId, "Shell tool requires a non-empty 'command' parameter.");
        }

        var workingDirectory = ResolveWorkingDirectory(cwd);
        if (workingDirectory is null || !Directory.Exists(workingDirectory))
        {
            return Fail(request.ToolCallId, $"Working directory '{cwd}' does not exist.");
        }

        var (fileName, arguments) = ResolveShell(command);
        try
        {
            var result = await TerminalSessionRunner.RunAsync(
                fileName, arguments, workingDirectory, command,
                timeoutMs, longLived, request.ToolCallId, cancellationToken).ConfigureAwait(false);
            return Ok(request.ToolCallId, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(request.ToolCallId, ex.Message);
        }
    }

    private static string? ResolveWorkingDirectory(string? requested)
    {
        var root = TinadecTools.Tools.FileRW.WorkspacePathResolver.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(requested)) return root;
        // Constrain explicit cwd values under the workspace root when it is known.
        if (!string.IsNullOrWhiteSpace(root))
        {
            var full = Path.GetFullPath(Path.Combine(root, requested));
            var normalizedRoot = Path.GetFullPath(root);
            return full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        return Directory.Exists(requested) ? requested : null;
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleTerminalControlAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        string action = "status";
        string? terminalSessionId = null;
        string? data = null;
        try
        {
            if (request.Params is { ValueKind: JsonValueKind.Object } parameters)
            {
                if (parameters.TryGetProperty("action", out var actionElement) && actionElement.ValueKind == JsonValueKind.String)
                    action = actionElement.GetString() ?? "status";
                if (parameters.TryGetProperty("terminal_session_id", out var sessionElement) && sessionElement.ValueKind == JsonValueKind.String)
                    terminalSessionId = sessionElement.GetString();
                if (parameters.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.String)
                    data = dataElement.GetString();
            }
        }
        catch (JsonException)
        {
            // Fall through to status default.
        }

        switch (action.ToLowerInvariant())
        {
            case "write":
                if (terminalSessionId is null || data is null)
                    return Fail(request.ToolCallId, "write requires terminal_session_id and data.");
                return Ok(request.ToolCallId, new TerminalControlResult(
                    TerminalSessionHost.TryWriteStdin(terminalSessionId, data), terminalSessionId, null));
            case "kill":
                if (terminalSessionId is null)
                    return Fail(request.ToolCallId, "kill requires terminal_session_id.");
                return Ok(request.ToolCallId, new TerminalControlResult(
                    TerminalSessionHost.TryKill(terminalSessionId), terminalSessionId, null));
            case "replay":
                if (terminalSessionId is null)
                    return Fail(request.ToolCallId, "replay requires terminal_session_id.");
                return Ok(request.ToolCallId, new TerminalControlResult(true, terminalSessionId, null));
            default:
                // Serialize with the source-generated context; this host disables
                // reflection-based serialization.
                var sessions = TerminalSessionHost.ListSnapshots()
                    .Select(snapshot => JsonSerializer.SerializeToElement(
                        snapshot, ShellToolJsonContext.Default.TerminalSessionSnapshot))
                    .ToList();
                return Ok(request.ToolCallId, new TerminalControlResult(true, terminalSessionId, sessions));
        }
    }

    private static ToolCallResponse<JsonElement> Ok(long callId, ShellToolResult result) => new()
    {
        CallId = callId,
        IsSuccess = true,
        Response = JsonSerializer.SerializeToElement(result, ShellToolJsonContext.Default.ShellToolResult)
    };

    private static ToolCallResponse<JsonElement> Ok(long callId, TerminalControlResult result) => new()
    {
        CallId = callId,
        IsSuccess = true,
        Response = JsonSerializer.SerializeToElement(result, ShellToolJsonContext.Default.TerminalControlResult)
    };

    private static ToolCallResponse<JsonElement> NotApproved(long callId) => new()
    {
        CallId = callId,
        IsSuccess = false,
        Response = JsonSerializer.SerializeToElement(NotApprovedResponse.MESSAGE, ToolCallJsonContext.Default.String)
    };

    private static ToolCallResponse<JsonElement> Fail(long callId, string message) => new()
    {
        CallId = callId,
        IsSuccess = true,
        Response = JsonSerializer.SerializeToElement(
            new ShellToolResult(false, string.Empty, "failed", -1, string.Empty, string.Empty,
                false, false, false, 0, message),
            ShellToolJsonContext.Default.ShellToolResult)
    };
}

/// <summary>Response payload of the reserved <c>#terminal</c> control tool.</summary>
public sealed record TerminalControlResult(
    bool Success,
    string? TerminalSessionId,
    List<JsonElement>? Sessions);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ShellToolResult))]
[JsonSerializable(typeof(TerminalControlResult))]
[JsonSerializable(typeof(TerminalSessionSnapshot))]
internal partial class ShellToolJsonContext : JsonSerializerContext { }
