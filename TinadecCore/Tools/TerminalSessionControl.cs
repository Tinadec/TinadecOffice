using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Run-lifecycle coupling for terminal sessions: a cancelled run must not leave
/// long-lived shell processes behind, so live sessions are terminated through the
/// provider and the kill is recorded on the run.
/// </summary>
public interface ITerminalSessionControl
{
    Task<int> KillForRunAsync(Guid runId, CancellationToken cancellationToken = default);
}

public sealed class TerminalSessionControlService : ITerminalSessionControl
{
    private const string TerminalControlToolId = "#terminal";

    private readonly ITerminalSessionRegistry _registry;
    private readonly IToolProvider _provider;
    private readonly ILifecycleManager _lifecycle;

    public TerminalSessionControlService(
        ITerminalSessionRegistry registry,
        IToolProvider provider,
        ILifecycleManager lifecycle)
    {
        _registry = registry;
        _provider = provider;
        _lifecycle = lifecycle;
    }

    public async Task<int> KillForRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var sessions = _registry.ListLiveForRun(runId);
        if (sessions.Count == 0) return 0;

        var killed = 0;
        foreach (var session in sessions)
        {
            var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["action"] = "kill",
                ["terminal_session_id"] = session.TerminalSessionId
            });

            try
            {
                await _provider.CallAsync(session.WorkspaceRoot, new ToolWireRequestDto
                {
                    ToolId = TerminalControlToolId,
                    SessionId = runId.ToString(),
                    Approved = false,
                    Params = payload
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The provider may already be gone; the registry still closes the session.
            }

            _registry.MarkExited(session.TerminalSessionId, -1, false);
            try
            {
                await _lifecycle.AppendEventAsync(runId, "terminal.session.killed", new
                {
                    terminal_session_id = session.TerminalSessionId,
                    execution_id = session.ExecutionId,
                    task_id = session.TaskId,
                    tool_id = "shell",
                    reason = "run_cancelled"
                }, "Terminal session terminated because the run was cancelled.", "warning",
                    session.TaskId, null, "shell", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Event append is best-effort; the process kill already happened.
            }

            killed++;
        }

        return killed;
    }
}
