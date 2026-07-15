namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Manages run/task/agent/tool/approval state and audit events.
/// Receives MAF middleware, workflow events, session/checkpoint, and cancellation signals.
/// Core remains the sole authority for state; MAF session/checkpoint is execution runtime state only.
/// </summary>
public interface ILifecycleManager
{
    Task<string> StartRunAsync(
        string sessionId,
        string? parentRunId = null,
        CancellationToken cancellationToken = default);

    Task CompleteRunAsync(
        string runId,
        CancellationToken cancellationToken = default);

    Task<RunState> GetRunStateAsync(
        string runId,
        CancellationToken cancellationToken = default);
}

public sealed record RunState
{
    public string RunId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public RunStatus Status { get; init; } = RunStatus.Pending;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }
}

public enum RunStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}
