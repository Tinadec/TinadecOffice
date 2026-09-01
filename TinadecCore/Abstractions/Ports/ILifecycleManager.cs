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
        string? triggerMessageId = null,
        CancellationToken cancellationToken = default);

    Task<string> StartRunAsync(
        RunStartRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a run once for the durable trigger message. Repeating the same request
    /// returns the previously created run, including across process restarts.
    /// </summary>
    Task<RunStartResult> StartOrGetRunAsync(
        RunStartRequest request,
        CancellationToken cancellationToken = default);

    Task<RunState?> FindRunByTriggerMessageAsync(
        string sessionId,
        string triggerMessageId,
        CancellationToken cancellationToken = default);

    Task CompleteRunAsync(
        string runId,
        CancellationToken cancellationToken = default);

    Task SetRunStatusAsync(
        string runId,
        string status,
        string? summary = null,
        CancellationToken cancellationToken = default);

    Task<int> CountActiveRunsAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<RunState> GetRunStateAsync(
        string runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances the denormalized run context revision after an applied conversation
    /// patch. Revisions are monotonic; checkpoint CAS remains the execution authority.
    /// </summary>
    Task AdvanceRunContextRevisionAsync(
        string runId,
        long contextRevision,
        CancellationToken cancellationToken = default);

    Task<long> AppendEventAsync(
        Guid runId,
        string eventType,
        object? payload,
        string summary,
        string severity = "info",
        Guid? taskId = null,
        Guid? approvalId = null,
        string? toolId = null,
        CancellationToken cancellationToken = default);

    Task<string> StartToolExecutionAsync(
        ToolExecutionStart start,
        CancellationToken cancellationToken = default);

    Task CompleteToolExecutionAsync(
        string executionId,
        ToolExecutionCompletion completion,
        CancellationToken cancellationToken = default);

    Task FailToolExecutionAsync(
        string executionId,
        string errorCategory,
        string safeMessage,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RunState>> ListNonTerminalRunsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Lists non-terminal runs that are not held by a live execution lease.</summary>
    Task<IReadOnlyList<RunState>> ListLeaseEligibleRunsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>Stores the resolved configuration once; later writes must have the same hash.</summary>
    Task<FrozenRunConfiguration> FreezeRunConfigurationAsync(
        string runId,
        FrozenRunConfigurationWrite configuration,
        CancellationToken cancellationToken = default);

    Task<FrozenRunConfiguration?> GetFrozenRunConfigurationAsync(
        string runId,
        CancellationToken cancellationToken = default);

    /// <summary>Saves an immutable engine checkpoint using compare-and-set revision semantics.</summary>
    Task<RunCheckpoint> SaveRunCheckpointAsync(
        string runId,
        RunCheckpointWrite checkpoint,
        CancellationToken cancellationToken = default);

    Task<RunCheckpoint?> GetCurrentRunCheckpointAsync(
        string runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pending orchestration directives aimed at one run, oldest first. The
    /// pending-status filter is the resume mechanism, so draining marks rows
    /// instead of deleting them.
    /// </summary>
    Task<IReadOnlyList<RunDirective>> ListPendingRunDirectivesAsync(
        Guid runId,
        CancellationToken cancellationToken = default);

    /// <summary>Marks the given still-pending directives as drained or rejected.</summary>
    Task<RunDirectiveDrainResult> DrainRunDirectivesAsync(
        Guid runId,
        IReadOnlyList<Guid> directiveIds,
        string drainedStatus,
        CancellationToken cancellationToken = default);

    Task<RunLease> TryAcquireRunLeaseAsync(
        string runId,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    Task<bool> HeartbeatRunLeaseAsync(
        string runId,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    Task ReleaseRunLeaseAsync(
        string runId,
        string ownerId,
        CancellationToken cancellationToken = default);

    Task UpdateTaskSnapshotAsync(
        Guid runId,
        object taskNode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TinadecCore.Contracts.Events.EventEnvelope>> ReplayEventsAsync(
        Guid? sessionId,
        long afterSequence,
        CancellationToken cancellationToken = default);

    Task<DurableRunStreamChunk> AppendRunStreamAsync(
        string runId,
        DurableRunStreamAppend item,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DurableRunStreamChunk>> ReplayRunStreamAsync(
        string runId,
        Guid? turnId,
        long afterSequence,
        CancellationToken cancellationToken = default);
}

public sealed record RunState
{
    public string RunId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string? TriggerMessageId { get; init; }
    public string? TurnId { get; init; }
    public RunStatus Status { get; init; } = RunStatus.Planning;
    public long ContextRevision { get; init; }
    public long ConfigurationVersion { get; init; }
    public string ConfigurationHash { get; init; } = string.Empty;
    public string ApplicationMode { get; init; } = "conversation";
    public string AgentMode { get; init; } = "auto";
    public string PermissionMode { get; init; } = "default";
    public string RuntimeProfileId { get; init; } = "conversation.auto";
    public string? TenantId { get; init; }
    public string? WorkspaceId { get; init; }
    /// <summary>Immutable principal that admitted the run. Empty means legacy runs cannot authorize tools.</summary>
    public string? InitiatedByPrincipalId { get; init; }
    public long CheckpointRevision { get; init; }
    public string? FrozenConfigurationHash { get; init; }
    public string? LeaseOwner { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public DateTimeOffset? LeaseHeartbeatAt { get; init; }
    public int RecoveryCount { get; init; }
    public string? Summary { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>A durable orchestration directive row projected for the engine.</summary>
public sealed record RunDirective(
    Guid Id,
    Guid SessionId,
    string Kind,
    string PayloadJson,
    string? IdempotencyKey);

/// <summary>Outcome of a drain batch.</summary>
public sealed record RunDirectiveDrainResult(int DrainedCount);

/// <summary>Immutable values captured when a full-duplex run is admitted.</summary>
public sealed record RunStartRequest(
    string SessionId,
    string TriggerMessageId,
    string? TurnId = null,
    long ContextRevision = 0,
    long ConfigurationVersion = 0,
    string ConfigurationHash = "",
    string ApplicationMode = "conversation",
    string AgentMode = "auto",
    string PermissionMode = "default",
    string RuntimeProfileId = "conversation.auto",
    string? InitiatedByPrincipalId = null);

/// <summary>Values captured when a tool call is dispatched to the tool layer.</summary>
public sealed record ToolExecutionStart(
    Guid SessionId,
    Guid RunId,
    Guid TaskId,
    Guid AgentInstanceId,
    string ToolId,
    string Risk,
    bool RequiresApproval,
    string ParametersJson,
    Guid? ApprovalId = null);

/// <summary>Structured completion payload for a tool execution.</summary>
public sealed record ToolExecutionCompletion(
    string ResultJson,
    string ResultMediaType = "application/json");

public enum RunStatus
{
    Planning,
    Understanding,
    Executing,
    Replanning,
    AwaitingApproval,
    AwaitingDelegate,
    AwaitingUser,
    Paused,
    Reviewing,
    Completed,
    Failed,
    Cancelled
}
