using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Provider-neutral tool execution port. Core depends on this contract rather
/// than on a particular transport or process model. Providers may be local
/// child processes, remote workers, or an in-process implementation; all of
/// them expose the same manifest and structured call semantics.
/// </summary>
public interface IToolProvider
{
    /// <summary>Ensures the provider is available for the workspace and returns its manifest.</summary>
    Task<ToolManifestDto> EnsureStartedAsync(string workspaceRoot, CancellationToken cancellationToken = default);

    /// <summary>Dispatches one structured tool call and awaits its response.</summary>
    Task<ToolWireResponseDto> CallAsync(
        string workspaceRoot,
        ToolWireRequestDto request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the provider manifest for a running (or freshly started) workspace.</summary>
    Task<ToolManifestDto> GetManifestAsync(string workspaceRoot, CancellationToken cancellationToken = default);

    /// <summary>Stops provider resources. In-flight calls fail with a structured process_exit result.</summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Compatibility name retained for hosts that used the original local-process
/// adapter. New Core code consumes <see cref="IToolProvider"/> so replacing the
/// default provider does not require a process manager implementation.
/// </summary>
public interface IToolProcessManager : IToolProvider
{
    /// <summary>
    /// Dispatches one structured tool call and forwards unsolicited wire events
    /// (terminal output, session lifecycle) raised while the call is in flight.
    /// </summary>
    Task<ToolWireResponseDto> CallStreamingAsync(
        string workspaceRoot,
        ToolWireRequestDto request,
        TimeSpan? timeout,
        Action<ToolWireEventDto>? onEvent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Events not attributable to an in-flight call, e.g. the exit of a long-lived
    /// terminal session. Subscribers must be exception-safe; a throwing subscriber
    /// must not break the provider read loop.
    /// </summary>
    event Action<ToolWireEventDto>? WireEventBroadcast;
}

/// <summary>Cached view of the TinadecTools manifest exposed by a process manager.</summary>
public interface IToolRegistry
{
    Task<IReadOnlyList<ToolManifestEntryDto>> ListToolsAsync(string? workspaceRoot = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ToolManifestEntryDto>> SearchToolsAsync(string query, string? workspaceRoot = null, CancellationToken cancellationToken = default);

    Task<ToolManifestEntryDto?> FindToolAsync(string toolId, string? workspaceRoot = null, CancellationToken cancellationToken = default);
}

/// <summary>Terminal status values a dispatch can resolve to.</summary>
public static class ToolDispatchStatus
{
    public const string Requested = "requested";
    public const string Completed = "completed";
    public const string AwaitingApproval = "awaiting_approval";
    public const string AwaitingDelegate = "awaiting_delegate";
    public const string AwaitingUser = "awaiting_user";
    public const string AwaitingResume = "awaiting_resume";
    public const string OutcomeUnknown = "outcome_unknown";
    public const string NotApproved = "not_approved";
    public const string Rejected = "rejected";
    public const string Timeout = "timeout";
    public const string ProcessExit = "process_exit";
    public const string Blocked = "blocked";
    public const string Failed = "failed";
}

/// <summary>
/// Core-governed tool dispatch: policy check, approval gating, execution persistence,
/// workspace write serialization, and process calls.
/// </summary>
public interface IToolDispatcher
{
    /// <summary>
    /// Persists a tool execution. Mutating calls stop at <c>awaiting_approval</c>;
    /// eligible read-only calls continue through <see cref="ResumeAsync"/>.
    /// </summary>
    Task<ToolDispatchResultDto> PrepareAsync(ToolDispatchRequestDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Continues one persisted execution after the durable approval/run state makes
    /// it eligible. This method never waits on an in-memory approval signal.
    /// </summary>
    Task<ToolDispatchResultDto> ResumeAsync(string executionId, CancellationToken cancellationToken = default);

    /// <summary>Compatibility alias for callers that previously used one-shot dispatch.</summary>
    Task<ToolDispatchResultDto> ExecuteAsync(ToolDispatchRequestDto request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves a tool call's trusted run/session/project/agent scope. Callers never
/// supply a workspace root or session id to the tools process.
/// </summary>
public interface IToolInvocationScopeResolver
{
    Task<ToolInvocationScope> ResolveAsync(ToolInvocationScopeRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only declaration source for autonomous worker model calls.  It exposes
/// only the run-frozen v2 entries that the persisted agent instance may use; the
/// engine must not derive function declarations from a live/default registry.
/// </summary>
public interface IFrozenToolManifestCatalog
{
    Task<IReadOnlyList<FrozenToolManifestEntry>> ListAuthorizedAsync(
        Guid runId,
        Guid taskId,
        Guid agentInstanceId,
        CancellationToken cancellationToken = default);
}

public sealed record ToolInvocationScopeRequest(
    Guid RunId,
    Guid TaskId,
    Guid AgentInstanceId,
    string ToolId);

public sealed record ToolInvocationScope(
    Guid TenantId,
    Guid WorkspaceId,
    Guid PrincipalId,
    Guid ProjectId,
    Guid SessionId,
    Guid RunId,
    Guid TaskId,
    Guid AgentInstanceId,
    string WorkspaceRoot,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> AllowedResources,
    string FrozenConfigurationHash,
    int DefaultTimeoutSeconds,
    int WorkerRetryLimit,
    bool SerializeWorkspaceWrites,
    IReadOnlyList<FrozenToolManifestEntry>? AuthorizedToolManifest = null,
    string? FrozenToolManifestHash = null);

/// <summary>
/// Agent-runtime authorization boundary consumed by the Tools module. DmaEA owns
/// the underlying agent-instance definitions; Tools only receives this constrained
/// view and never reads agent tables directly.
/// </summary>
public interface IAgentToolAuthorization
{
    /// <summary>Returns the persisted agent grant without selecting a tool.</summary>
    Task<AgentToolAuthorization?> GetAuthorizationAsync(
        Guid runId,
        Guid taskId,
        Guid agentInstanceId,
        CancellationToken cancellationToken = default);

    Task<AgentToolAuthorization?> AuthorizeAsync(
        Guid runId,
        Guid taskId,
        Guid agentInstanceId,
        string toolId,
        CancellationToken cancellationToken = default);
}

public sealed record AgentToolAuthorization(
    Guid TenantId,
    Guid WorkspaceId,
    Guid SessionId,
    Guid RunId,
    Guid TaskId,
    Guid AgentInstanceId,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> AllowedResources);

/// <summary>
/// Durable execution and approval state transitions. The Lifecycle module owns
/// their transaction, content references, and one-time approval consumption.
/// </summary>
public interface IToolExecutionCoordinator
{
    Task<ToolExecutionPreparation> PrepareAsync(ToolExecutionPrepareRequest request, CancellationToken cancellationToken = default);
    Task<ToolExecutionSnapshot?> FindAsync(Guid executionId, CancellationToken cancellationToken = default);
    Task<ToolExecutionStartDecision> TryStartAsync(Guid executionId, CancellationToken cancellationToken = default);
    Task<ToolExecutionSnapshot> CompleteAsync(Guid executionId, string resultJson, CancellationToken cancellationToken = default);
    Task<ToolExecutionSnapshot> FailAsync(Guid executionId, string status, string errorCategory, string safeMessage, CancellationToken cancellationToken = default);
    Task<ToolExecutionSnapshot> BindAuthorizationAsync(
        Guid executionId,
        Guid? permissionRequestId,
        Guid? authorizationDecisionId,
        Guid? capabilityLeaseId,
        string status,
        CancellationToken cancellationToken = default);
    Task<ToolExecutionSnapshot> BindWorkspaceSnapshotAsync(
        Guid executionId,
        Guid snapshotId,
        string snapshotHash,
        CancellationToken cancellationToken = default);
    Task<ToolExecutionSnapshot> EnsureApprovalAsync(Guid executionId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Attempts to turn this execution's pending approval into an approved one
    /// from an unattended grant: an explicit pre-authorization row, or the run's
    /// frozen full-access mode. All binding checks in TryStartAsync still apply
    /// afterwards. Returns null when no grant matches; the execution then keeps
    /// waiting on its normal human approval path.
    /// </summary>
    Task<PreAuthorizationMintResult?> TryMintPreAuthorizedApprovalAsync(Guid executionId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Compatibility projection for callers that own an execution coordinator but
    /// need to atomically consume its bound action approval.
    /// </summary>
    Task<bool> TryConsumeApprovalAsync(
        Guid approvalId,
        string executionId,
        string expectedRequestHash,
        CancellationToken cancellationToken = default);
    Task<ToolExecutionRecoveryResult> ApplyRecoveryDecisionAsync(Guid executionId, string decision, CancellationToken cancellationToken = default);
    Task CancelPendingForRunAsync(Guid runId, CancellationToken cancellationToken = default);
}

public sealed record ToolExecutionPrepareRequest(
    Guid TenantId,
    Guid WorkspaceId,
    Guid ProjectId,
    Guid SessionId,
    Guid RunId,
    Guid TaskId,
    Guid AgentInstanceId,
    string ToolId,
    string Risk,
    bool MutatesWorkspace,
    bool RequiresApproval,
    string ParametersJson,
    string ParametersHash,
    string ToolCallKey,
    string Summary,
    bool DeferApproval = false,
    int LeaseUses = 1);

/// <param name="Source">Why the approval was minted: pre_authorized or full_access_auto_mint.</param>
public sealed record PreAuthorizationMintResult(ToolExecutionSnapshot Snapshot, string Source);

public sealed record ToolExecutionSnapshot(
    Guid Id,
    Guid TenantId,
    Guid WorkspaceId,
    Guid ProjectId,
    Guid SessionId,
    Guid RunId,
    Guid TaskId,
    Guid AgentInstanceId,
    Guid? ApprovalId,
    string ToolId,
    string ToolCallKey,
    string Risk,
    bool MutatesWorkspace,
    bool RequiresApproval,
    string Status,
    string ParametersJson,
    string ParametersHash,
    int Attempt,
    string? ResultJson,
    string? ErrorCategory,
    string? SafeErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    Guid? PermissionRequestId = null,
    Guid? AuthorizationDecisionId = null,
    Guid? CapabilityLeaseId = null,
    int LeaseUses = 1,
    Guid? WorkspaceSnapshotId = null,
    string? WorkspaceSnapshotHash = null);

/// <summary>
/// Result of durably admitting one logical tool call. Replaying the same
/// <see cref="ToolExecutionPrepareRequest.ToolCallKey"/> returns the original
/// execution instead of creating another approval or external side effect.
/// </summary>
public sealed record ToolExecutionPreparation(ToolExecutionSnapshot Execution, bool Existing);

public sealed record ToolExecutionStartDecision(string Status, ToolExecutionSnapshot? Execution, string? Message = null);

public sealed record ToolExecutionRecoveryResult(
    string Status,
    ToolExecutionSnapshot? Execution,
    ToolExecutionSnapshot? ReplacementExecution,
    Guid? ApprovalId,
    string? Message = null);

/// <summary>In-memory gate that parks a tool call until its approval is decided.</summary>
public interface IToolApprovalGate
{
    /// <summary>Awaits the decision for an approval. Returns the decision ("approved"/"rejected"/"cancelled").</summary>
    Task<string> WaitAsync(Guid approvalId, CancellationToken cancellationToken = default);

    /// <summary>Releases a waiter with the decided outcome.</summary>
    void Resolve(Guid approvalId, string decision);

    /// <summary>Releases a waiter with a cancellation outcome (expired, run failed, host shutdown).</summary>
    void Cancel(Guid approvalId);
}

/// <summary>
/// Approval persistence owned by the control plane. The Tools module calls this port so
/// approval records stay Core-owned without a module-to-module reference.
/// </summary>
public interface IToolApprovalCoordinator
{
    /// <summary>Creates a pending approval bound to run/task/tool and the canonical parameters hash.</summary>
    Task<Guid> CreateToolApprovalAsync(
        string sessionId,
        string runId,
        string taskId,
        string toolId,
        string parametersHash,
        string summary,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically consumes an approved, unexpired approval whose request hash matches.
    /// Returns false when the approval is missing, not approved, expired, hash-mismatched,
    /// or already consumed.
    /// </summary>
    Task<bool> TryConsumeApprovalAsync(
        Guid approvalId,
        string executionId,
        string expectedRequestHash,
        CancellationToken cancellationToken = default);

    /// <summary>Persists a human decision; notification/wake-up is derived from that durable state.</summary>
    Task<ToolApprovalDecision> DecideAsync(
        Guid approvalId,
        string decision,
        string? reason,
        CancellationToken cancellationToken = default);
}

public sealed record ToolApprovalDecision(
    Guid ApprovalId,
    string Status,
    Guid? RunId,
    Guid? TaskId,
    Guid? ExecutionId,
    DateTimeOffset DecidedAt);
