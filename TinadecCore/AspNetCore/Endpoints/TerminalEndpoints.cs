using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Tools;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Terminal session control for the Desktop panel. Sessions are created by the
/// governed <c>shell</c> tool dispatch, so every route here is bound to a run that
/// Core already authorized; stdin and kill are forwarded to the Tool Provider as
/// the reserved <c>#terminal</c> transport call and recorded on the run for audit.
/// </summary>
public static class TerminalEndpoints
{
    private const string TerminalControlToolId = "#terminal";

    public static IEndpointRouteBuilder MapTerminalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/terminals", (string? run_id, ITerminalSessionRegistry registry) =>
        {
            if (!Guid.TryParse(run_id, out var runGuid))
                return Results.BadRequest(new { code = "INVALID_RUN_ID", message = "run_id must be a valid Guid." });

            var sessions = registry.ListForRun(runGuid);
            return Results.Ok(sessions.Select(Project).ToList());
        });

        app.MapPost("/api/v1/terminals/{terminalSessionId}/stdin", async (
            string terminalSessionId,
            TerminalStdinRequest? input,
            ITerminalSessionRegistry registry,
            IToolProvider provider,
            ILifecycleManager lifecycle,
            CancellationToken ct) =>
        {
            var session = registry.Find(terminalSessionId);
            if (session is null)
                return Results.NotFound(new { code = "TERMINAL_SESSION_NOT_FOUND", message = "Terminal session was not found." });
            if (!session.IsLive)
                return Results.Conflict(new { code = "TERMINAL_SESSION_CLOSED", message = "Terminal session is no longer running." });
            if (input is null || string.IsNullOrEmpty(input.Data))
                return Results.BadRequest(new { code = "INVALID_STDIN", message = "data is required." });

            var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["action"] = "write",
                ["terminal_session_id"] = terminalSessionId,
                ["data"] = input.Data
            });

            ToolWireResponseDto response;
            try
            {
                response = await provider.CallAsync(session.WorkspaceRoot, new ToolWireRequestDto
                {
                    ToolId = TerminalControlToolId,
                    SessionId = session.RunId.ToString(),
                    Approved = false,
                    Params = payload
                }, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Results.Json(new { code = "TOOL_PROVIDER_UNAVAILABLE", message = "The tool provider rejected the terminal write." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!response.IsSuccess)
                return Results.Json(new { code = "TERMINAL_WRITE_FAILED", message = response.Error ?? "Terminal write failed." },
                    statusCode: StatusCodes.Status502BadGateway);

            // Audit: user input handed to an agent-owned terminal stays on the run.
            await lifecycle.AppendEventAsync(session.RunId, "terminal.stdin", new
            {
                terminal_session_id = terminalSessionId,
                execution_id = session.ExecutionId,
                task_id = session.TaskId,
                tool_id = "shell",
                length = input.Data.Length
            }, "User input sent to terminal session.", "info", session.TaskId, null, "shell", ct).ConfigureAwait(false);

            return Results.Ok(new { terminal_session_id = terminalSessionId, accepted = true });
        });

        app.MapPost("/api/v1/terminals/{terminalSessionId}/kill", async (
            string terminalSessionId,
            ITerminalSessionRegistry registry,
            IToolProvider provider,
            ILifecycleManager lifecycle,
            CancellationToken ct) =>
        {
            var session = registry.Find(terminalSessionId);
            if (session is null)
                return Results.NotFound(new { code = "TERMINAL_SESSION_NOT_FOUND", message = "Terminal session was not found." });

            var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["action"] = "kill",
                ["terminal_session_id"] = terminalSessionId
            });

            try
            {
                await provider.CallAsync(session.WorkspaceRoot, new ToolWireRequestDto
                {
                    ToolId = TerminalControlToolId,
                    SessionId = session.RunId.ToString(),
                    Approved = false,
                    Params = payload
                }, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Results.Json(new { code = "TOOL_PROVIDER_UNAVAILABLE", message = "The tool provider rejected the terminal kill." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            registry.MarkExited(terminalSessionId, -1, false);
            await lifecycle.AppendEventAsync(session.RunId, "terminal.session.killed", new
            {
                terminal_session_id = terminalSessionId,
                execution_id = session.ExecutionId,
                task_id = session.TaskId,
                tool_id = "shell"
            }, "Terminal session was terminated.", "warning", session.TaskId, null, "shell", ct).ConfigureAwait(false);

            return Results.Ok(new { terminal_session_id = terminalSessionId, killed = true });
        });

        return app;
    }

    private static object Project(TerminalSessionRecord session) => new
    {
        terminal_session_id = session.TerminalSessionId,
        run_id = session.RunId,
        task_id = session.TaskId,
        agent_instance_id = session.AgentInstanceId,
        execution_id = session.ExecutionId,
        command = session.Command,
        status = session.Status,
        live = session.IsLive,
        started_at = session.StartedAt,
        ended_at = session.EndedAt,
        exit_code = session.ExitCode,
        timed_out = session.TimedOut
    };
}

public sealed class TerminalStdinRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public string Data { get; init; } = string.Empty;
}
