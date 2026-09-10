using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>Fallback values for legacy callers; admitted full-duplex runs use their frozen policy instead.</summary>
public sealed class ToolDispatchOptions
{
    public bool MutationRequiresApproval { get; set; } = true;
    public bool SerializeWorkspaceWrites { get; set; } = true;
    public int DefaultTimeoutSeconds { get; set; } = 120;
    public int WorkerRetryLimit { get; set; } = 2;
}

/// <summary>
/// Core-governed dispatch with a durable split between prepare and resume. A write
/// never waits inside an HTTP request: prepare commits its execution/approval pair,
/// and resume consumes that approval exactly once when the run is eligible.
/// </summary>
public sealed class ToolDispatcher : IToolDispatcher
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WorkspaceLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tool ids whose calls stream wire events (terminal output) while in flight.</summary>
    internal static readonly HashSet<string> StreamingTools = new(StringComparer.OrdinalIgnoreCase) { "shell" };

    private readonly IToolProvider _provider;
    private readonly IToolInvocationScopeResolver _scopeResolver;
    private readonly IToolExecutionCoordinator _executions;
    private readonly IAuthorizationService _authorization;
    private readonly IWorkspaceSnapshotService _snapshots;
    private readonly ILifecycleManager _lifecycle;
    private readonly ITerminalSessionRegistry _terminalSessions;
    private readonly IInFlightToolCallRegistry _inFlightCalls;
    private readonly ToolDispatchOptions _options;
    private readonly ILogger<ToolDispatcher> _logger;
    private readonly ISessionWorkspaceBinder? _workspaceBinder;

    public ToolDispatcher(
        IToolProvider provider,
        IToolInvocationScopeResolver scopeResolver,
        IToolExecutionCoordinator executions,
        IAuthorizationService authorization,
        IWorkspaceSnapshotService snapshots,
        ILifecycleManager lifecycle,
        ITerminalSessionRegistry terminalSessions,
        IInFlightToolCallRegistry inFlightCalls,
        ToolDispatchOptions options,
        ILogger<ToolDispatcher> logger,
        ISessionWorkspaceBinder? workspaceBinder = null)
    {
        _provider = provider;
        _scopeResolver = scopeResolver;
        _executions = executions;
        _authorization = authorization;
        _snapshots = snapshots;
        _lifecycle = lifecycle;
        _terminalSessions = terminalSessions;
        _inFlightCalls = inFlightCalls;
        _options = options;
        _logger = logger;
        _workspaceBinder = workspaceBinder;
    }

    public Task<ToolDispatchResultDto> ExecuteAsync(ToolDispatchRequestDto request, CancellationToken cancellationToken = default) =>
        PrepareAsync(request, cancellationToken);

    public async Task<ToolDispatchResultDto> PrepareAsync(ToolDispatchRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!TryParseRequest(request, out var runId, out var taskId, out var agentId, out var validationError))
            return Blocked(validationError!);

        try
        {
            var scope = await _scopeResolver.ResolveAsync(
                new ToolInvocationScopeRequest(runId, taskId, agentId, request.ToolId), cancellationToken).ConfigureAwait(false);
            var descriptor = await FindV2ToolAsync(scope, request.ToolId, cancellationToken).ConfigureAwait(false);
            if (descriptor.Entry is null) return Blocked(descriptor.Error!);

            var parametersJson = request.Params is { } parameters ? parameters.GetRawText() : "{}";
            if (!IsObjectOrNull(parametersJson)) return Blocked("Tool parameters must be a JSON object or null.");
            var requiresApproval = descriptor.Entry.MutatesWorkspace || descriptor.Entry.RequiresApproval;
            var toolCallKey = ResolveToolCallKey(request, runId, taskId, agentId, descriptor.Entry.Id, parametersJson);
            var preparation = await _executions.PrepareAsync(new ToolExecutionPrepareRequest(
                scope.TenantId,
                scope.WorkspaceId,
                scope.ProjectId,
                scope.SessionId,
                scope.RunId,
                scope.TaskId,
                scope.AgentInstanceId,
                descriptor.Entry.Id,
                descriptor.Entry.Risk,
                descriptor.Entry.MutatesWorkspace,
                requiresApproval,
                parametersJson,
                ToolParametersHash.Compute(parametersJson),
                toolCallKey,
                $"Tool '{descriptor.Entry.Id}' requested by agent {scope.AgentInstanceId}.",
                DeferApproval: true,
                LeaseUses: request.LeaseUses,
                LaneKey: request.LaneKey), cancellationToken).ConfigureAwait(false);
            var execution = preparation.Execution;

            // High-risk writes receive the same pre-write snapshot guard as
            // explicit user actions. The execution was persisted first, so a
            // snapshot failure can durably block it without calling the provider.
            // Projectless scopes have no workspace to snapshot; the Core-owned
            // virtual tool's safety net is its approval gate instead.
            if (!CoreVirtualToolPolicy.IsProjectlessScope(scope.ProjectId)
                && NeedsPrewriteSnapshot(execution)
                && execution.WorkspaceSnapshotId is null
                && execution.Status is not ("completed" or "failed" or "timed_out" or "cancelled"))
            {
                try
                {
                    var snapshot = await _snapshots.CreateAsync(new WorkspaceSnapshotCreateRequest(
                        scope.ProjectId, $"execution:{execution.Id:N}:prewrite"), cancellationToken).ConfigureAwait(false);
                    execution = await _executions.BindWorkspaceSnapshotAsync(
                        execution.Id, snapshot.Id, snapshot.WorkspaceHash, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or DirectoryNotFoundException)
                {
                    var blocked = await _executions.FailAsync(execution.Id, "failed", RunErrorTaxonomy.SnapshotFailed,
                        SafeMessage(ex.Message), cancellationToken).ConfigureAwait(false);
                    return ResultForFailure(blocked, ToolDispatchStatus.Blocked);
                }
            }

            // Persist the execution before asking governance so permission requests
            // can safely point at a stable execution id and be replayed after a host
            // crash. The claim is intentionally fixed and provider-neutral.
            if (execution.PermissionRequestId is null && execution.AuthorizationDecisionId is null)
            {
                var authorization = await AuthorizeAsync(scope, descriptor.Entry, execution, cancellationToken).ConfigureAwait(false);
                if (authorization.Status is ToolDispatchStatus.AwaitingDelegate or ToolDispatchStatus.AwaitingUser)
                {
                    execution = await _executions.BindAuthorizationAsync(execution.Id,
                        authorization.PermissionRequestId,
                        authorization.AuthorizationDecisionId,
                        authorization.CapabilityLeaseId,
                        authorization.Status,
                        cancellationToken).ConfigureAwait(false);
                    await TrySetRunStatusAsync(execution.RunId, authorization.Status, cancellationToken).ConfigureAwait(false);
                    return PreparedResult(execution, preparation.Existing, authorization);
                }
                if (authorization.Status == ToolDispatchStatus.Blocked)
                {
                    execution = await _executions.BindAuthorizationAsync(execution.Id,
                        authorization.PermissionRequestId,
                        authorization.AuthorizationDecisionId,
                        authorization.CapabilityLeaseId,
                        "blocked",
                        cancellationToken).ConfigureAwait(false);
                    await _executions.FailAsync(execution.Id, "failed", authorization.ErrorCategory ?? "not_authorized", authorization.Message ?? "Tool authorization was denied.", cancellationToken).ConfigureAwait(false);
                    return DispatchBlocked(execution, authorization);
                }
                execution = await _executions.BindAuthorizationAsync(execution.Id,
                    authorization.PermissionRequestId,
                    authorization.AuthorizationDecisionId,
                    authorization.CapabilityLeaseId,
                    "requested",
                    cancellationToken).ConfigureAwait(false);
                if (execution.RequiresApproval)
                {
                    execution = await _executions.EnsureApprovalAsync(execution.Id, cancellationToken).ConfigureAwait(false);
                    var minted = await _executions.TryMintPreAuthorizedApprovalAsync(execution.Id, cancellationToken).ConfigureAwait(false);
                    if (minted is not null)
                    {
                        execution = minted.Snapshot;
                        await AppendEventAsync(scope.RunId, "approval.pre_authorized_minted",
                            $"Tool '{descriptor.Entry.Id}' was approved by {minted.Source}.",
                            new { execution_id = execution.Id, approval_id = execution.ApprovalId, tool_id = descriptor.Entry.Id, source = minted.Source, lane_key = execution.LaneKey },
                            cancellationToken, scope.TaskId, descriptor.Entry.Id).ConfigureAwait(false);
                    }
                }
            }

            if (!preparation.Existing)
            {
                await AppendEventAsync(scope.RunId, "tool.execution.requested", $"Tool '{descriptor.Entry.Id}' requested.", new
                {
                    execution_id = execution.Id,
                    task_id = scope.TaskId,
                    agent_instance_id = scope.AgentInstanceId,
                    tool_id = descriptor.Entry.Id,
                    requires_approval = execution.RequiresApproval,
                    mutates_workspace = execution.MutatesWorkspace,
                    risk = execution.Risk,
                    lane_key = execution.LaneKey
                }, cancellationToken, scope.TaskId, descriptor.Entry.Id).ConfigureAwait(false);

                if (execution.RequiresApproval)
                {
                    await AppendEventAsync(scope.RunId, "approval.requested", $"Approval requested for tool '{descriptor.Entry.Id}'.", new
                    {
                        approval_id = execution.ApprovalId,
                        execution_id = execution.Id,
                        task_id = scope.TaskId,
                        tool_id = descriptor.Entry.Id,
                        risk = execution.Risk,
                        lane_key = execution.LaneKey
                    }, cancellationToken, scope.TaskId, descriptor.Entry.Id).ConfigureAwait(false);
                }
            }

            // The caller must checkpoint this execution id before deciding when
            // to resume it. In particular, an unapproved read call must not run
            // between process recovery and persistence of its function call.
            return PreparedResult(execution, preparation.Existing, null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException or DirectoryNotFoundException)
        {
            _logger.LogDebug(ex, "Tool prepare was rejected for run {RunId} and tool {ToolId}", request.RunId, request.ToolId);
            return Blocked(SafeMessage(ex.Message));
        }
        catch (InvalidDataException ex)
        {
            // Manifest handshake failure: the tool runtime never accepted the
            // call. This is a task-level transport failure, not a run failure.
            _logger.TryLogWarning(ex, "Tool runtime unavailable while preparing {ToolId} for run {RunId}", request.ToolId, request.RunId);
            return Blocked($"The tool runtime is unavailable: {SafeMessage(ex.Message)}", errorCategory: RunErrorTaxonomy.ToolRuntimeUnavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Provider startup timeout. Host shutdown (caller's own token) is
            // deliberately not caught here and keeps its propagation semantics.
            _logger.TryLogWarning("Tool runtime startup timed out while preparing {ToolId} for run {RunId}", request.ToolId, request.RunId);
            return Blocked("The tool runtime did not become ready in time.", errorCategory: RunErrorTaxonomy.ToolRuntimeUnavailable);
        }
    }

    public async Task<ToolDispatchResultDto> ResumeAsync(string executionId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(executionId, out var executionGuid)) return Blocked("execution_id must be a valid id.");
        var execution = await _executions.FindAsync(executionGuid, cancellationToken).ConfigureAwait(false);
        if (execution is null) return Blocked("Tool execution was not found.");

        // A running row this process holds is a genuinely live call, not a
        // stale remnant: answer already_running before the authorization phase
        // can clobber the row's status back to requested underneath the call.
        if (execution.Status == "running" && _inFlightCalls.IsTracked(executionGuid))
        {
            return new ToolDispatchResultDto { Status = ToolDispatchStatus.Blocked, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, ErrorCategory = RunErrorTaxonomy.ToolAlreadyRunning, Message = "Tool execution is already running." };
        }

        ToolInvocationScope scope;
        ToolManifestEntryDto descriptor;
        try
        {
            scope = await _scopeResolver.ResolveAsync(
                new ToolInvocationScopeRequest(execution.RunId, execution.TaskId, execution.AgentInstanceId, execution.ToolId), cancellationToken).ConfigureAwait(false);
            var found = await FindV2ToolAsync(scope, execution.ToolId, cancellationToken).ConfigureAwait(false);
            if (found.Entry is null)
            {
                await _executions.FailAsync(execution.Id, "failed", "manifest_changed", found.Error!, cancellationToken).ConfigureAwait(false);
                return new ToolDispatchResultDto { Status = ToolDispatchStatus.Failed, ExecutionId = execution.Id.ToString(), ApprovalId = execution.ApprovalId?.ToString(), ErrorCategory = "manifest_changed", Message = found.Error };
            }
            descriptor = found.Entry;
            if (descriptor.MutatesWorkspace != execution.MutatesWorkspace || (descriptor.RequiresApproval || descriptor.MutatesWorkspace) != execution.RequiresApproval)
            {
                const string manifestPolicyMessage = "The current manifest no longer matches the persisted tool execution policy.";
                await _executions.FailAsync(execution.Id, "failed", "manifest_changed", manifestPolicyMessage, cancellationToken).ConfigureAwait(false);
                return new ToolDispatchResultDto { Status = ToolDispatchStatus.Failed, ExecutionId = execution.Id.ToString(), ApprovalId = execution.ApprovalId?.ToString(), ErrorCategory = "manifest_changed", Message = manifestPolicyMessage };
            }

            var authorization = await AuthorizeAsync(scope, descriptor, execution, cancellationToken).ConfigureAwait(false);
            if (authorization.Status is ToolDispatchStatus.AwaitingDelegate or ToolDispatchStatus.AwaitingUser)
            {
                execution = await _executions.BindAuthorizationAsync(execution.Id,
                    authorization.PermissionRequestId,
                    authorization.AuthorizationDecisionId,
                    authorization.CapabilityLeaseId,
                    authorization.Status,
                    cancellationToken).ConfigureAwait(false);
                await TrySetRunStatusAsync(execution.RunId, authorization.Status, cancellationToken).ConfigureAwait(false);
                return PreparedResult(execution, existing: true, authorization);
            }
            if (authorization.Status == ToolDispatchStatus.Blocked)
            {
                execution = await _executions.BindAuthorizationAsync(execution.Id,
                    authorization.PermissionRequestId,
                    authorization.AuthorizationDecisionId,
                    authorization.CapabilityLeaseId,
                    "blocked",
                    cancellationToken).ConfigureAwait(false);
                var denied = await _executions.FailAsync(execution.Id, "failed", authorization.ErrorCategory ?? "not_authorized", authorization.Message ?? "Tool authorization was denied.", cancellationToken).ConfigureAwait(false);
                return DispatchBlocked(denied, authorization);
            }
            execution = await _executions.BindAuthorizationAsync(execution.Id,
                authorization.PermissionRequestId,
                authorization.AuthorizationDecisionId,
                authorization.CapabilityLeaseId,
                "requested",
                cancellationToken).ConfigureAwait(false);
            if (execution.CapabilityLeaseId is not { } leaseId)
                return DispatchBlocked(execution, new DispatchAuthorization(ToolDispatchStatus.Blocked, null, authorization.AuthorizationDecisionId, null, "capability_lease_required", "A capability lease is required before a tool can execute."));
            var consumedLease = await _authorization.ConsumeToolLeaseAsync(new ToolLeaseConsumptionCommand(
                leaseId,
                authorization.LeaseNonce,
                scope.PrincipalId,
                scope.AgentInstanceId,
                ToolClaim(descriptor),
                execution.RunId,
                execution.TaskId,
                $"tool-lease:{execution.Id:N}"), cancellationToken).ConfigureAwait(false);
            if (consumedLease.Status != "allowed")
            {
                var failedLease = await _executions.FailAsync(execution.Id, "failed", consumedLease.Decision.ReasonCode, consumedLease.Decision.Reason, cancellationToken).ConfigureAwait(false);
                return DispatchBlocked(failedLease, new DispatchAuthorization(ToolDispatchStatus.Blocked, consumedLease.Decision.PermissionRequestId, consumedLease.Decision.Id, leaseId, consumedLease.Decision.ReasonCode, consumedLease.Decision.Reason));
            }
            if (execution.RequiresApproval && execution.ApprovalId is null)
                execution = await _executions.EnsureApprovalAsync(execution.Id, cancellationToken).ConfigureAwait(false);
            var resumedMint = await _executions.TryMintPreAuthorizedApprovalAsync(executionGuid, cancellationToken).ConfigureAwait(false);
            if (resumedMint is not null)
            {
                execution = resumedMint.Snapshot;
                await AppendEventAsync(execution.RunId, "approval.pre_authorized_minted",
                    $"Tool '{descriptor.Id}' was approved by {resumedMint.Source}.",
                    new { execution_id = execution.Id, approval_id = execution.ApprovalId, tool_id = descriptor.Id, source = resumedMint.Source },
                    cancellationToken, execution.TaskId, descriptor.Id).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException or DirectoryNotFoundException)
        {
            return Blocked(SafeMessage(ex.Message), execution);
        }
        catch (InvalidDataException ex)
        {
            // Manifest handshake failure before the execution was started: the
            // row stays resumable and the failure surfaces at task level.
            _logger.TryLogWarning(ex, "Tool runtime unavailable while resuming execution {ExecutionId}", execution.Id);
            return Blocked($"The tool runtime is unavailable: {SafeMessage(ex.Message)}", execution, RunErrorTaxonomy.ToolRuntimeUnavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogWarning("Tool runtime startup timed out while resuming execution {ExecutionId}", execution.Id);
            return Blocked("The tool runtime did not become ready in time.", execution, RunErrorTaxonomy.ToolRuntimeUnavailable);
        }

        // A "running" row this process does not hold is a stale remnant of a
        // crashed host: the coordinator converts it (outcome_unknown for
        // approval-gated calls, re-drive for read-only ones) instead of
        // answering already_running forever. A row this process does hold
        // keeps the legacy already_running answer.
        var start = await _executions.TryStartAsync(executionGuid,
            allowStaleRunningReset: !_inFlightCalls.IsTracked(executionGuid),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        switch (start.Status)
        {
            case "awaiting_approval":
                return new ToolDispatchResultDto { Status = ToolDispatchStatus.AwaitingApproval, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, Message = start.Message, ParkExpired = start.ParkExpired };
            case "awaiting_resume":
                return new ToolDispatchResultDto { Status = ToolDispatchStatus.AwaitingResume, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, Message = start.Message };
            case RunErrorTaxonomy.OutcomeUnknown:
                await PauseForUnknownOutcomeAsync(execution, start.Message, cancellationToken).ConfigureAwait(false);
                return new ToolDispatchResultDto { Status = ToolDispatchStatus.OutcomeUnknown, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, ErrorCategory = RunErrorTaxonomy.OutcomeUnknown, Message = start.Message };
            case "not_approved":
                return new ToolDispatchResultDto { Status = ToolDispatchStatus.NotApproved, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, ErrorCategory = "not_approved", Message = start.Message };
            case "cancelled":
                return new ToolDispatchResultDto { Status = ToolDispatchStatus.Blocked, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, ErrorCategory = RunErrorTaxonomy.Cancelled, Message = start.Message };
            case "completed":
            case "failed":
            case "timed_out":
                return new ToolDispatchResultDto { Status = start.Status, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, Message = start.Message };
            case "already_running":
                return new ToolDispatchResultDto { Status = ToolDispatchStatus.Blocked, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, ErrorCategory = "already_running", Message = start.Message };
            case "running":
                execution = start.Execution ?? execution;
                break;
            default:
                return new ToolDispatchResultDto { Status = ToolDispatchStatus.Blocked, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, ErrorCategory = start.Status, Message = start.Message };
        }

        await TrySetRunStatusAsync(execution.RunId, "executing", cancellationToken).ConfigureAwait(false);
        if (!TryParseParameters(execution.ParametersJson, out var parameters))
        {
            var failed = await _executions.FailAsync(execution.Id, "failed", "invalid_parameters", "Persisted tool parameters are invalid JSON.", cancellationToken).ConfigureAwait(false);
            return ResultForFailure(failed, ToolDispatchStatus.Failed);
        }

        if (execution.MutatesWorkspace && execution.WorkspaceSnapshotId is { } snapshotId)
        {
            try
            {
                var validation = await _snapshots.ValidateAsync(snapshotId, cancellationToken).ConfigureAwait(false);
                if (!validation.IsValid)
                {
                    var failed = await _executions.FailAsync(execution.Id, "failed", "snapshot_changed",
                        "The pre-write workspace snapshot no longer matches the current workspace.", cancellationToken).ConfigureAwait(false);
                    return ResultForFailure(failed, ToolDispatchStatus.Blocked);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException or DirectoryNotFoundException)
            {
                var failed = await _executions.FailAsync(execution.Id, "failed", "snapshot_unavailable",
                    SafeMessage(ex.Message), cancellationToken).ConfigureAwait(false);
                return ResultForFailure(failed, ToolDispatchStatus.Blocked);
            }
        }

        var safeReadRetry = !execution.MutatesWorkspace
            && string.Equals(descriptor.RetrySafety, "safe", StringComparison.OrdinalIgnoreCase);
        var retryLimit = safeReadRetry ? Math.Max(0, scope.WorkerRetryLimit) : 0;
        var timeoutSeconds = scope.DefaultTimeoutSeconds > 0 ? scope.DefaultTimeoutSeconds : Math.Max(1, _options.DefaultTimeoutSeconds);
        // The wire timeout must cover the tool's own budget: a shell call with
        // timeout_ms above the configured default must not be killed by Core
        // before the tool's own deadline fires.
        var timeout = ResolveWireTimeout(parameters, TimeSpan.FromSeconds(timeoutSeconds));
        var streaming = _provider as IToolProcessManager;
        var streams = streaming is not null && StreamingTools.Contains(descriptor.Id);
        ToolEventPump? pump = null;
        if (streams)
        {
            pump = new ToolEventPump(_lifecycle, _logger, execution.RunId, execution.TaskId, execution.Id, descriptor.Id);
            await pump.AppendCommandAsync(parameters, scope.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
        }

        // Track the in-flight call so a run cancel interrupts the provider call
        // instead of waiting out the wire timeout, and so a later resume can
        // tell a live call apart from a stale running row.
        using var inFlight = _inFlightCalls.TryRegister(execution.RunId, execution.Id);
        if (inFlight is null)
        {
            return new ToolDispatchResultDto { Status = ToolDispatchStatus.Blocked, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, ErrorCategory = RunErrorTaxonomy.ToolAlreadyRunning, Message = "Tool execution is already running." };
        }
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, inFlight.Token);
        var callToken = linkedCts.Token;

        ToolWireResponseDto? last = null;
        try
        {
            for (var attempt = 1; attempt <= retryLimit + 1; attempt++)
            {
                var wire = new ToolWireRequestDto
                {
                    ToolId = descriptor.Id,
                    SessionId = execution.SessionId.ToString(),
                    Approved = execution.RequiresApproval,
                    Params = parameters
                };

                ToolWireResponseDto response;
                try
                {
                    response = await CallProviderAsync(
                        streaming, pump, scope, execution, wire, timeout, callToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && inFlight.Token.IsCancellationRequested)
                {
                    // The run was cancelled while the provider held the call. The
                    // child may or may not have executed it, so a mutating call
                    // lands in outcome_unknown and a read-only call is cancelled.
                    const string cancelMessage = "The run was cancelled while the tool call was in flight.";
                    if (execution.MutatesWorkspace)
                    {
                        var unknown = await _executions.FailAsync(execution.Id, RunErrorTaxonomy.OutcomeUnknown, RunErrorTaxonomy.Cancelled, cancelMessage, CancellationToken.None).ConfigureAwait(false);
                        await PauseForUnknownOutcomeAsync(unknown, cancelMessage, CancellationToken.None).ConfigureAwait(false);
                        return ResultForFailure(unknown, ToolDispatchStatus.OutcomeUnknown);
                    }
                    var cancelled = await _executions.FailAsync(execution.Id, "cancelled", RunErrorTaxonomy.Cancelled, cancelMessage, CancellationToken.None).ConfigureAwait(false);
                    return ResultForFailure(cancelled, ToolDispatchStatus.Blocked);
                }
                catch (Exception ex) when ((ex is InvalidDataException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
                {
                    // Handshake/startup failures happen before the request line
                    // is written: the call provably never reached the tool, so
                    // even a mutating execution fails (never outcome_unknown) and
                    // the failure stays at task level instead of killing the run.
                    _logger.TryLogWarning(ex, "Tool runtime unavailable while dispatching {ToolId} for run {RunId}", descriptor.Id, execution.RunId);
                    response = new ToolWireResponseDto { CallId = -1, IsSuccess = false, Error = $"{RunErrorTaxonomy.ToolRuntimeUnavailable}: {SafeMessage(ex.Message)}" };
                }

                if (response.IsSuccess)
                {
                    var resultJson = response.Result is { } result ? result.GetRawText() : "null";
                    if (streams) RecordTerminalSession(response.Result, execution, scope);
                    // Shell-class tools report command success inside the result
                    // payload (a non-zero exit is a wire success). Record that
                    // honestly without changing the completed dispatch outcome:
                    // the result still flows back to the model.
                    var toolSuccess = ReadEmbeddedToolSuccess(descriptor.Id, response.Result);
                    var completed = await _executions.CompleteAsync(execution.Id, resultJson, toolSuccess, cancellationToken).ConfigureAwait(false);
                    await AppendEventAsync(execution.RunId, "tool.execution.completed",
                        toolSuccess == false ? $"Tool '{descriptor.Id}' completed with an embedded failure." : $"Tool '{descriptor.Id}' completed.", new
                    {
                        execution_id = execution.Id,
                        task_id = execution.TaskId,
                        tool_id = descriptor.Id,
                        attempt,
                        tool_success = toolSuccess
                    }, cancellationToken, execution.TaskId, descriptor.Id, toolSuccess == false ? "warning" : "info").ConfigureAwait(false);
                    return new ToolDispatchResultDto
                    {
                        Status = ToolDispatchStatus.Completed,
                        ExecutionId = completed.Id.ToString(),
                        ApprovalId = completed.ApprovalId?.ToString(),
                        Attempt = attempt,
                        Result = response.Result
                    };
                }

                last = response;
                var category = WireErrorCategory(response.Error);
                var retry = safeReadRetry && (category is RunErrorTaxonomy.ToolTimeout or RunErrorTaxonomy.ToolProcessExit) && attempt <= retryLimit;
                await AppendEventAsync(execution.RunId, "tool.execution.failed", $"Tool '{descriptor.Id}' failed ({category}).", new
                {
                    execution_id = execution.Id,
                    task_id = execution.TaskId,
                    tool_id = descriptor.Id,
                    attempt,
                    category,
                    retryable = retry
                }, cancellationToken, execution.TaskId, descriptor.Id, retry ? "warning" : "error").ConfigureAwait(false);
                if (!retry) break;
                _logger.TryLogWarning("Retrying safe read-only tool {ToolId} after {Category} ({Attempt}/{Max})", descriptor.Id, category, attempt + 1, retryLimit + 1);
            }
        }
        finally
        {
            // Let buffered terminal events land before the execution is reported.
            if (pump is not null) await pump.CompleteAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }

        var finalCategory = WireErrorCategory(last?.Error);
        var message = SafeMessage(last?.Error);
        if (execution.MutatesWorkspace && finalCategory is RunErrorTaxonomy.ToolTimeout or RunErrorTaxonomy.ToolProcessExit)
        {
            var unknown = await _executions.FailAsync(execution.Id, RunErrorTaxonomy.OutcomeUnknown, finalCategory, message, cancellationToken).ConfigureAwait(false);
            await PauseForUnknownOutcomeAsync(unknown, message, cancellationToken).ConfigureAwait(false);
            return ResultForFailure(unknown, ToolDispatchStatus.OutcomeUnknown);
        }

        var failureStatus = finalCategory == RunErrorTaxonomy.ToolTimeout ? "timed_out" : "failed";
        var failedExecution = await _executions.FailAsync(execution.Id, failureStatus, finalCategory, message, cancellationToken).ConfigureAwait(false);
        return ResultForFailure(failedExecution, failureStatus == "timed_out" ? ToolDispatchStatus.Timeout : finalCategory == RunErrorTaxonomy.ToolProcessExit ? ToolDispatchStatus.ProcessExit : ToolDispatchStatus.Failed);
    }

    /// <summary>
    /// Calls the provider, using the streaming overload when the provider supports it,
    /// so terminal output raised during the call reaches the run event log.
    /// </summary>
    private async Task<ToolWireResponseDto> CallProviderAsync(
        IToolProcessManager? streaming,
        ToolEventPump? pump,
        ToolInvocationScope scope,
        ToolExecutionSnapshot execution,
        ToolWireRequestDto wire,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Action<ToolWireEventDto>? observer = pump is null ? null : pump.Enqueue;

        if (CoreVirtualToolPolicy.IsProjectlessCreateWorkspace(scope.ProjectId, wire.ToolId))
        {
            return await ExecuteCoreWorkspaceToolAsync(scope, wire, cancellationToken).ConfigureAwait(false);
        }

        if (!scope.SerializeWorkspaceWrites || !execution.MutatesWorkspace)
        {
            return streaming is not null
                ? await streaming.CallStreamingAsync(scope.WorkspaceRoot, wire, timeout, observer, cancellationToken).ConfigureAwait(false)
                : await _provider.CallAsync(scope.WorkspaceRoot, wire, timeout, cancellationToken).ConfigureAwait(false);
        }

        var gate = WorkspaceLocks.GetOrAdd(scope.WorkspaceRoot, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return streaming is not null
                ? await streaming.CallStreamingAsync(scope.WorkspaceRoot, wire, timeout, observer, cancellationToken).ConfigureAwait(false)
                : await _provider.CallAsync(scope.WorkspaceRoot, wire, timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Executes the Core-owned create_workspace virtual tool: creates the directory,
    /// registers the project, and migrates the projectless session onto it — all
    /// after the standard approval gate has already passed for this execution.
    /// </summary>
    private async Task<ToolWireResponseDto> ExecuteCoreWorkspaceToolAsync(
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        CancellationToken cancellationToken)
    {
        if (_workspaceBinder is null)
        {
            return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = "The session workspace binder is not registered." };
        }

        string? name = null;
        string? path = null;
        if (wire.Params is { ValueKind: JsonValueKind.Object } parameters)
        {
            if (parameters.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                name = nameElement.GetString();
            if (parameters.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String)
                path = pathElement.GetString();
        }
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
        {
            return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = "create_workspace requires non-empty 'name' and 'path' parameters." };
        }

        // The invocation-scope resolver skips project-root checks for a projectless
        // scope, so the agent's declared resource grant must be evaluated against the
        // requested target here — otherwise this is the one tool that could act
        // outside its allow list. An undeclared (empty) grant stays unrestricted.
        if (!ToolResourceAllowList.IsAllowed(scope.AllowedResources, path))
        {
            return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = "create_workspace path is outside the agent's allowed resources." };
        }

        try
        {
            var binding = await _workspaceBinder.BindSessionToWorkspaceAsync(scope.SessionId, name, path, cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(scope.RunId, "session.workspace_bound",
                $"Session was bound to workspace '{binding.ProjectName}'.", new
                {
                    session_id = scope.SessionId,
                    project_id = binding.ProjectId,
                    root_path = binding.RootPath,
                    project_created = binding.ProjectCreated
                }, cancellationToken, scope.TaskId, CoreWorkspaceTool.ToolId).ConfigureAwait(false);
            var result = JsonSerializer.SerializeToElement(new
            {
                success = true,
                name = binding.ProjectName,
                path = binding.RootPath,
                project_id = binding.ProjectId,
                project_created = binding.ProjectCreated,
                message = $"Workspace '{binding.ProjectName}' is ready at '{binding.RootPath}'; this conversation is now bound to it. New interactions run with the full tool set of that workspace."
            });
            return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = true, Result = result };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = SafeMessage(ex.Message) };
        }
    }

    /// <summary>
    /// Records the terminal session a shell call created so the panel and stdin
    /// routes can address it later. Exit events arrive through the provider broadcast.
    /// </summary>
    private void RecordTerminalSession(JsonElement? result, ToolExecutionSnapshot execution, ToolInvocationScope scope)
    {
        if (result is not { ValueKind: JsonValueKind.Object } payload) return;
        if (!payload.TryGetProperty("terminal_session_id", out var sessionId)
            || sessionId.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(sessionId.GetString()))
        {
            return;
        }

        var status = payload.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String
            ? statusElement.GetString() ?? "completed"
            : "completed";
        var command = payload.TryGetProperty("command", out var commandElement) && commandElement.ValueKind == JsonValueKind.String
            ? commandElement.GetString() ?? string.Empty
            : string.Empty;

        _terminalSessions.Record(new TerminalSessionRecord(
            sessionId.GetString()!,
            execution.RunId,
            execution.TaskId,
            execution.AgentInstanceId,
            execution.Id,
            scope.WorkspaceRoot,
            command,
            status,
            DateTimeOffset.UtcNow));
    }

    private async Task<(ToolManifestEntryDto? Entry, string? Error)> FindV2ToolAsync(ToolInvocationScope scope, string toolId, CancellationToken cancellationToken)
    {
        if (CoreVirtualToolPolicy.IsProjectlessScope(scope.ProjectId))
        {
            // Projectless (free-conversation) scope: no live provider exists. The
            // Core-owned create_workspace virtual tool is the only legal call, and
            // the run-frozen manifest is the sole declaration source.
            if (!CoreVirtualToolPolicy.IsCreateWorkspace(toolId)) return (null, $"Unknown tool '{toolId}'.");
            var frozenOnly = scope.AuthorizedToolManifest?.FirstOrDefault(item => string.Equals(item.Id, toolId, StringComparison.OrdinalIgnoreCase));
            if (frozenOnly is null) return (null, $"Tool '{toolId}' is not present in the frozen authorized manifest.");
            if (string.IsNullOrWhiteSpace(scope.FrozenToolManifestHash)
                || !string.Equals(ToolManifestHasher.Compute(scope.AuthorizedToolManifest!), scope.FrozenToolManifestHash, StringComparison.OrdinalIgnoreCase))
            {
                return (null, "The frozen tool manifest hash does not match the run binding.");
            }
            return (new ToolManifestEntryDto
            {
                Id = frozenOnly.Id,
                Description = frozenOnly.Description,
                RequiresApproval = frozenOnly.RequiresApproval,
                InputSchema = frozenOnly.InputSchema.Clone(),
                Risk = frozenOnly.Risk,
                MutatesWorkspace = frozenOnly.MutatesWorkspace,
                RetrySafety = frozenOnly.RetrySafety,
                ConfirmationFields = frozenOnly.ConfirmationFields.ToArray()
            }, null);
        }

        var manifest = await _provider.GetManifestAsync(scope.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
        if (manifest.ProtocolVersion != 2) return (null, "TinadecTools manifest v2 is required for autonomous dispatch.");
        if (string.IsNullOrWhiteSpace(scope.FrozenToolManifestHash)
            || !string.Equals(manifest.ManifestHash, scope.FrozenToolManifestHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ToolManifestHasher.Compute(manifest.Tools), scope.FrozenToolManifestHash, StringComparison.OrdinalIgnoreCase))
        {
            return (null, "The TinadecTools manifest changed after run admission.");
        }
        var descriptor = manifest.Tools.FirstOrDefault(item => string.Equals(item.Id, toolId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null) return (null, $"Unknown tool '{toolId}'.");
        var frozen = scope.AuthorizedToolManifest?.FirstOrDefault(item => string.Equals(item.Id, toolId, StringComparison.OrdinalIgnoreCase));
        return frozen is null || !ToolManifestHasher.Equivalent(descriptor, frozen)
            ? (null, $"Tool '{toolId}' is not present in the frozen authorized manifest.")
            : (descriptor, null);
    }

    private async Task PauseForUnknownOutcomeAsync(ToolExecutionSnapshot execution, string? message, CancellationToken cancellationToken)
    {
        await TrySetRunStatusAsync(execution.RunId, "paused", cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(execution.RunId, "tool.execution.outcome_unknown", $"Tool '{execution.ToolId}' outcome is unknown.", new
        {
            execution_id = execution.Id,
            task_id = execution.TaskId,
            tool_id = execution.ToolId,
            error_category = execution.ErrorCategory,
            message = SafeMessage(message)
        }, cancellationToken, execution.TaskId, execution.ToolId, "error").ConfigureAwait(false);
    }

    private async Task TrySetRunStatusAsync(Guid runId, string status, CancellationToken cancellationToken)
    {
        try
        {
            await _lifecycle.SetRunStatusAsync(runId.ToString(), status, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not move run {RunId} to {Status}", runId, status);
        }
    }

    private async Task AppendEventAsync(Guid runId, string type, string summary, object payload, CancellationToken cancellationToken, Guid taskId, string toolId, string severity = "info")
    {
        try
        {
            await _lifecycle.AppendEventAsync(runId, type, payload, summary, severity, taskId, toolId: toolId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not append {EventType} for run {RunId}", type, runId);
        }
    }

    private static bool TryParseRequest(ToolDispatchRequestDto request, out Guid runId, out Guid taskId, out Guid agentId, out string? error)
    {
        runId = taskId = agentId = Guid.Empty;
        error = null;
        if (!Guid.TryParse(request.RunId, out runId) || !Guid.TryParse(request.TaskId, out taskId) || !Guid.TryParse(request.AgentInstanceId, out agentId))
        {
            error = "run_id, task_id, and agent_instance_id must be valid ids.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(request.ToolId))
        {
            error = "tool_id is required.";
            return false;
        }
        return true;
    }

    private static string ResolveToolCallKey(
        ToolDispatchRequestDto request,
        Guid runId,
        Guid taskId,
        Guid agentId,
        string toolId,
        string parametersJson)
    {
        if (!string.IsNullOrWhiteSpace(request.ToolCallKey)) return request.ToolCallKey.Trim();

        // Direct HTTP calls do not have the worker's checkpoint sequence. Their
        // complete trusted identity and canonical parameters still give Core a
        // stable, idempotent key without accepting a caller-supplied root/session.
        return $"api:{runId:N}:{taskId:N}:{agentId:N}:{toolId}:{ToolParametersHash.Compute(parametersJson)}";
    }

    private async Task<DispatchAuthorization> AuthorizeAsync(
        ToolInvocationScope scope,
        ToolManifestEntryDto descriptor,
        ToolExecutionSnapshot execution,
        CancellationToken cancellationToken)
    {
        var result = await _authorization.AuthorizeToolAsync(new ToolAuthorizationCommand(
            scope.PrincipalId,
            scope.AgentInstanceId,
            ToolClaim(descriptor),
            execution.RunId,
            execution.TaskId,
            descriptor.Risk,
            0m,
            Math.Clamp(execution.LeaseUses, 1, 32),
            TimeSpan.FromMinutes(30),
            $"Tool '{descriptor.Id}' requested by agent {scope.AgentInstanceId}.",
            $"tool-auth:{execution.Id:N}",
            scope.PermissionMode), cancellationToken).ConfigureAwait(false);
        var status = result.Status switch
        {
            "awaiting_delegate" => ToolDispatchStatus.AwaitingDelegate,
            "awaiting_user" => ToolDispatchStatus.AwaitingUser,
            "allowed" => ToolDispatchStatus.Requested,
            _ => ToolDispatchStatus.Blocked
        };
        return new DispatchAuthorization(status, result.PermissionRequest?.Id,
            result.Decision.Id, result.CapabilityLeaseId,
            result.Decision.ReasonCode, result.Decision.Reason, result.LeaseNonce);
    }

    private static CapabilityClaim ToolClaim(ToolManifestEntryDto descriptor) => new(
        "tool.invoke",
        descriptor.MutatesWorkspace ? "mutate" : "read",
        $"tool://{descriptor.Id}");

    private static ToolDispatchResultDto PreparedResult(ToolExecutionSnapshot execution, bool existing, DispatchAuthorization? authorization)
    {
        var status = execution.Status switch
        {
            "requested" => ToolDispatchStatus.Requested,
            "awaiting_approval" => ToolDispatchStatus.AwaitingApproval,
            "awaiting_delegate" => ToolDispatchStatus.AwaitingDelegate,
            "awaiting_user" => ToolDispatchStatus.AwaitingUser,
            RunErrorTaxonomy.OutcomeUnknown => ToolDispatchStatus.OutcomeUnknown,
            "completed" => ToolDispatchStatus.Completed,
            "timed_out" => ToolDispatchStatus.Timeout,
            "failed" => ToolDispatchStatus.Failed,
            _ => ToolDispatchStatus.Blocked
        };
        return new ToolDispatchResultDto
        {
            Status = status,
            ExecutionId = execution.Id.ToString(),
            ApprovalId = execution.ApprovalId?.ToString(),
            PermissionRequestId = execution.PermissionRequestId?.ToString() ?? authorization?.PermissionRequestId?.ToString(),
            AuthorizationDecisionId = execution.AuthorizationDecisionId?.ToString() ?? authorization?.AuthorizationDecisionId?.ToString(),
            Attempt = execution.Attempt,
            ErrorCategory = execution.ErrorCategory,
            Message = status switch
            {
                ToolDispatchStatus.Requested => existing
                    ? "Existing tool execution is ready for the durable worker."
                    : "Tool execution was persisted and is ready for the durable worker.",
                ToolDispatchStatus.AwaitingApproval => "Tool execution is awaiting human approval.",
                ToolDispatchStatus.AwaitingDelegate => "Tool execution is awaiting delegated approval.",
                ToolDispatchStatus.AwaitingUser => "Tool execution is awaiting user authorization.",
                _ => execution.SafeErrorMessage
            }
        };
    }

    private static ToolDispatchResultDto DispatchBlocked(ToolExecutionSnapshot execution, DispatchAuthorization authorization) => new()
    {
        Status = authorization.Status == ToolDispatchStatus.Blocked ? ToolDispatchStatus.Blocked : authorization.Status,
        ExecutionId = execution.Id.ToString(),
        ApprovalId = execution.ApprovalId?.ToString(),
        PermissionRequestId = execution.PermissionRequestId?.ToString() ?? authorization.PermissionRequestId?.ToString(),
        AuthorizationDecisionId = execution.AuthorizationDecisionId?.ToString() ?? authorization.AuthorizationDecisionId?.ToString(),
        Attempt = execution.Attempt,
        ErrorCategory = authorization.ErrorCategory,
        Message = authorization.Message
    };

    private sealed record DispatchAuthorization(
        string Status,
        Guid? PermissionRequestId,
        Guid? AuthorizationDecisionId,
        Guid? CapabilityLeaseId,
        string? ErrorCategory,
        string? Message,
        string? LeaseNonce = null);

    private static bool IsObjectOrNull(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseParameters(string json, out JsonElement? parameters)
    {
        parameters = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) return false;
            parameters = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ToolDispatchResultDto ResultForFailure(ToolExecutionSnapshot execution, string status) => new()
    {
        Status = status,
        ExecutionId = execution.Id.ToString(),
        ApprovalId = execution.ApprovalId?.ToString(),
        PermissionRequestId = execution.PermissionRequestId?.ToString(),
        AuthorizationDecisionId = execution.AuthorizationDecisionId?.ToString(),
        Attempt = execution.Attempt,
        ErrorCategory = execution.ErrorCategory,
        Message = execution.SafeErrorMessage
    };

    private static ToolDispatchResultDto Blocked(string message, ToolExecutionSnapshot? execution = null, string errorCategory = "blocked") => new()
    {
        Status = ToolDispatchStatus.Blocked,
        ExecutionId = execution?.Id.ToString(),
        ApprovalId = execution?.ApprovalId?.ToString(),
        PermissionRequestId = execution?.PermissionRequestId?.ToString(),
        AuthorizationDecisionId = execution?.AuthorizationDecisionId?.ToString(),
        Attempt = execution?.Attempt ?? 0,
        ErrorCategory = errorCategory,
        Message = message
    };

    /// <summary>
    /// Wire timeout for one call: the configured default, raised to cover the
    /// tool's own <c>timeout_ms</c> parameter (shell clamps it to 30 minutes)
    /// plus a margin, and clamped to the wire ceiling. Without this a worker
    /// asking for e.g. ten minutes would be killed by Core's 120s default first.
    /// </summary>
    internal static TimeSpan ResolveWireTimeout(JsonElement? parameters, TimeSpan defaultTimeout)
    {
        var baseSeconds = Math.Clamp((int)Math.Ceiling(defaultTimeout.TotalSeconds), 1, MaxWireTimeoutSeconds);
        if (parameters is { ValueKind: JsonValueKind.Object } payload
            && payload.TryGetProperty("timeout_ms", out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out var timeoutMs)
            && timeoutMs > 0)
        {
            var toolSeconds = (int)Math.Clamp((timeoutMs + 999) / 1000, 1, 1800);
            return TimeSpan.FromSeconds(Math.Clamp(Math.Max(baseSeconds, toolSeconds + WireTimeoutMarginSeconds), 1, MaxWireTimeoutSeconds));
        }
        return TimeSpan.FromSeconds(baseSeconds);
    }

    internal const int WireTimeoutMarginSeconds = 30;
    // The tool-side timeout ceiling (30 minutes) plus the wire margin.
    internal const int MaxWireTimeoutSeconds = 1800 + WireTimeoutMarginSeconds;

    /// <summary>
    /// Reads the embedded <c>success</c> field of a shell-class tool result.
    /// Null when the tool reports no embedded outcome (non-shell tools, or a
    /// payload without the field).
    /// </summary>
    internal static bool? ReadEmbeddedToolSuccess(string toolId, JsonElement? result)
    {
        if (!StreamingTools.Contains(toolId)) return null;
        if (result is not { ValueKind: JsonValueKind.Object } payload) return null;
        if (!payload.TryGetProperty("success", out var success)) return null;
        return success.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string WireErrorCategory(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return RunErrorTaxonomy.ToolError;
        var separator = error.IndexOf(':');
        var category = separator > 0 ? error[..separator].Trim().ToLowerInvariant() : error.Trim().ToLowerInvariant();
        return category is RunErrorTaxonomy.ToolTimeout or RunErrorTaxonomy.ToolProcessExit or RunErrorTaxonomy.ToolRuntimeUnavailable
            ? category
            : RunErrorTaxonomy.ToolError;
    }

    private static string SafeMessage(string? value) => string.IsNullOrWhiteSpace(value) ? "Tool call failed." : value.Trim()[..Math.Min(value.Trim().Length, 4096)];

    private static bool NeedsPrewriteSnapshot(ToolExecutionSnapshot execution) =>
        execution.MutatesWorkspace && RiskRank(execution.Risk) >= 2;

    private static int RiskRank(string risk) => risk.Trim().ToLowerInvariant() switch
    {
        "low" => 0,
        "medium" => 1,
        "elevated" => 2,
        "high" => 3,
        "critical" => 4,
        _ => int.MaxValue
    };
}

/// <summary>Canonical parameters hashing shared by dispatch and approval consumption.</summary>
public static class ToolParametersHash
{
    public static string Compute(string parametersJson)
    {
        var canonical = Canonicalize(parametersJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string Canonicalize(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            {
                WriteCanonical(writer, document.RootElement);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
