using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Durable, in-memory view of the terminal sessions Core has admitted for a run.
/// Sessions are created by the <c>shell</c> tool dispatch (id taken from the tool
/// result) and are addressable by the panel/stdin routes until they exit.
/// </summary>
public sealed record TerminalSessionRecord(
    string TerminalSessionId,
    Guid RunId,
    Guid TaskId,
    Guid AgentInstanceId,
    Guid ExecutionId,
    string WorkspaceRoot,
    string Command,
    string Status,
    DateTimeOffset StartedAt)
{
    public DateTimeOffset? EndedAt { get; init; }
    public int? ExitCode { get; init; }
    public bool TimedOut { get; init; }

    /// <summary>True while the session may still accept stdin or be killed.</summary>
    public bool IsLive => Status is "long_lived" or "running";
}

public interface ITerminalSessionRegistry
{
    void Record(TerminalSessionRecord record);

    TerminalSessionRecord? Find(string terminalSessionId);

    IReadOnlyList<TerminalSessionRecord> ListForRun(Guid runId);

    void MarkExited(string terminalSessionId, int exitCode, bool timedOut);

    /// <summary>Snapshots live sessions of a run before they are terminated.</summary>
    IReadOnlyList<TerminalSessionRecord> ListLiveForRun(Guid runId);
}

/// <summary>
/// Process-local registry. It is intentionally a projection: the authoritative
/// process lives in the TinadecTools child, so a host restart clears it and the
/// panel simply shows the session as gone.
/// </summary>
public sealed class TerminalSessionRegistry : ITerminalSessionRegistry
{
    private readonly ConcurrentDictionary<string, TerminalSessionRecord> _sessions = new(StringComparer.Ordinal);

    public void Record(TerminalSessionRecord record) => _sessions[record.TerminalSessionId] = record;

    public TerminalSessionRecord? Find(string terminalSessionId) =>
        _sessions.TryGetValue(terminalSessionId, out var record) ? record : null;

    public IReadOnlyList<TerminalSessionRecord> ListForRun(Guid runId) =>
        _sessions.Values.Where(x => x.RunId == runId).OrderBy(x => x.StartedAt).ToList();

    public IReadOnlyList<TerminalSessionRecord> ListLiveForRun(Guid runId) =>
        _sessions.Values.Where(x => x.RunId == runId && x.IsLive).ToList();

    public void MarkExited(string terminalSessionId, int exitCode, bool timedOut)
    {
        if (!_sessions.TryGetValue(terminalSessionId, out var existing)) return;
        _sessions[terminalSessionId] = new TerminalSessionRecord(
            existing.TerminalSessionId,
            existing.RunId,
            existing.TaskId,
            existing.AgentInstanceId,
            existing.ExecutionId,
            existing.WorkspaceRoot,
            existing.Command,
            timedOut ? "timed_out" : exitCode == 0 ? "exited" : "failed",
            existing.StartedAt)
        {
            EndedAt = DateTimeOffset.UtcNow,
            ExitCode = exitCode,
            TimedOut = timedOut
        };
    }
}

/// <summary>
/// Subscribes the bridge to provider broadcast events for the host lifetime.
/// </summary>
public sealed class TerminalSessionBridgeHost : IHostedService
{
    private readonly IToolProcessManager _processManager;
    private readonly TerminalSessionEventBridge _bridge;

    public TerminalSessionBridgeHost(IToolProcessManager processManager, TerminalSessionEventBridge bridge)
    {
        _processManager = processManager;
        _bridge = bridge;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _processManager.WireEventBroadcast += _bridge.Handle;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _processManager.WireEventBroadcast -= _bridge.Handle;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Bridges broadcast wire events (long-lived session exits) back onto the run that
/// owns the session, without making the provider depend on Core run concepts.
/// </summary>
public sealed class TerminalSessionEventBridge
{
    private readonly ITerminalSessionRegistry _registry;
    private readonly ILifecycleManager _lifecycle;

    public TerminalSessionEventBridge(ITerminalSessionRegistry registry, ILifecycleManager lifecycle)
    {
        _registry = registry;
        _lifecycle = lifecycle;
    }

    public void Handle(ToolWireEventDto wireEvent)
    {
        if (wireEvent.Payload is not { ValueKind: System.Text.Json.JsonValueKind.Object } payload) return;
        var terminalSessionId = ReadString(payload, "terminal_session_id");
        if (string.IsNullOrWhiteSpace(terminalSessionId)) return;

        var record = _registry.Find(terminalSessionId!);
        if (record is null) return;

        switch (wireEvent.Event)
        {
            case "terminal.stdout":
            {
                // Output produced after the originating call returned (the long-lived
                // case): attribute it to the run that owns the session.
                var data = ReadString(payload, "data");
                if (string.IsNullOrEmpty(data)) return;
                Fire(_lifecycle.AppendEventAsync(record.RunId, "terminal.stdout",
                    new
                    {
                        terminal_session_id = terminalSessionId,
                        execution_id = record.ExecutionId,
                        task_id = record.TaskId,
                        tool_id = "shell",
                        stream = ReadString(payload, "stream") ?? "stdout",
                        data
                    },
                    "Terminal output", "info", record.TaskId, null, "shell"));
                break;
            }
            case "terminal.exit":
            {
                var exitCode = ReadInt32(payload, "exit_code") ?? -1;
                var timedOut = ReadBoolean(payload, "timed_out") ?? false;
                _registry.MarkExited(terminalSessionId!, exitCode, timedOut);
                Fire(_lifecycle.AppendEventAsync(record.RunId, "terminal.exit",
                    new
                    {
                        terminal_session_id = terminalSessionId,
                        execution_id = record.ExecutionId,
                        task_id = record.TaskId,
                        tool_id = "shell",
                        exit_code = exitCode,
                        timed_out = timedOut
                    },
                    $"Terminal session {terminalSessionId} exited with code {exitCode}.",
                    "info", record.TaskId, null, "shell"));
                break;
            }
        }
    }

    private static void Fire(Task task) => _ = task.ContinueWith(
        static t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);

    private static string? ReadString(System.Text.Json.JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt32(System.Text.Json.JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;

    private static bool? ReadBoolean(System.Text.Json.JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                _ => null
            }
            : null;
}
