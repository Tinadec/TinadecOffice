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
    /// <summary>
    /// Fallback wire budget when a run carries no frozen tool policy. Kept in step
    /// with the frozen default (<c>tools.default_timeout_seconds</c>) and with the
    /// shell tool's own default: a build or test run must not be cut off by Core
    /// before the tool's own deadline fires.
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 600;
    public int WorkerRetryLimit { get; set; } = 2;
}

/// <summary>
/// Core-governed dispatch with a durable split between prepare and resume. A write
/// never waits inside an HTTP request: prepare commits its execution/approval pair,
/// and resume consumes that approval exactly once when the run is eligible.
/// </summary>
public sealed class ToolDispatcher : ILeaseFencedToolDispatcher
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
    private readonly ITinaChatToolGateway? _tinaChat;
    private readonly IMessageAttachmentStore? _attachments;
    /// <summary>
    /// The resource ledger, when the host registered one. Optional so a trimmed composition still
    /// dispatches tools; the ledger only ever adds a refusal, never authority.
    /// </summary>
    private readonly IResourceLeaseService? _resourceLeases;

    /// <summary>Where governance facts the dispatcher sees (a lost lease, a parked approval) are raised. Optional, best-effort.</summary>
    private readonly IGovernanceTopicSink? _topics;

    /// <summary>The session-as-a-graph projection behind <c>graph_view</c>. Optional: a trimmed host simply does not offer the tool.</summary>
    private readonly ISessionTopology? _topology;

    /// <summary>The session's evidence archive behind <c>recall_evidence</c>. Optional like the graph.</summary>
    private readonly IEvidenceArchive? _evidence;
    /// <summary>The environment steward's registry behind <c>environment_*</c>. Optional like the archive.</summary>
    private readonly IEnvironmentRegistry? _environments;
    /// <summary>Standing approvals a person gave (todo E7): command prefixes honored at prepare, shell's per-session delegation opt-in re-checked at the gates.</summary>
    private readonly IApprovalRules? _approvalRules;
    /// <summary>Per-member visibility into run internals (todo E5). Optional; an absent organization answers "unrestricted".</summary>
    private readonly ISessionOrganization? _organization;

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
        ISessionWorkspaceBinder? workspaceBinder = null,
        ITinaChatToolGateway? tinaChat = null,
        IMessageAttachmentStore? attachments = null,
        IResourceLeaseService? resourceLeases = null,
        IGovernanceTopicSink? topics = null,
        ISessionTopology? topology = null,
        IEvidenceArchive? evidence = null,
        IEnvironmentRegistry? environments = null,
        IApprovalRules? approvalRules = null,
        ISessionOrganization? organization = null)
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
        _tinaChat = tinaChat;
        _attachments = attachments;
        _resourceLeases = resourceLeases;
        _topics = topics;
        _topology = topology;
        _evidence = evidence;
        _environments = environments;
        _approvalRules = approvalRules;
        _organization = organization;
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

            // Claim the resource before anything else this call does. A refusal here is correctable —
            // the model can wait or work elsewhere — so it goes back to the worker as a tool result
            // naming the current holder, instead of being discovered as a destroyed file later.
            if (_resourceLeases is not null)
            {
                var claim = ResourceClaimResolver.Resolve(
                    descriptor.Entry.Id, ReadStringArguments(parametersJson), scope.ExecutionRoot, descriptor.Entry.MutatesWorkspace);
                if (claim is not null)
                {
                    var decision = await _resourceLeases.AcquireAsync(new ResourceAcquireRequest(
                        claim,
                        scope.SessionId,
                        scope.RunId,
                        scope.TaskId,
                        scope.AgentInstanceId,
                        $"Tool '{descriptor.Entry.Id}' by agent {scope.AgentInstanceId}."), cancellationToken).ConfigureAwait(false);
                    if (!decision.Granted)
                    {
                        // Announced as well as refused: a governance role subscribed to
                        // lease_conflict must see the collision, not only the worker that lost it.
                        await AppendEventAsync(scope.RunId, ResourceLeaseMessages.ConflictEventType,
                            $"Tool '{descriptor.Entry.Id}' was refused: {claim.Kind} '{claim.ResourceKey}' is held by another run.",
                            ResourceLeaseMessages.ConflictPayload(claim, decision.Conflicts, scope.RunId, scope.TaskId, ResourceLeaseMessages.DetectedAtToolCall),
                            cancellationToken, scope.TaskId, descriptor.Entry.Id, "warning").ConfigureAwait(false);
                        await RaiseTopicAsync(scope, GovernanceTopics.LeaseConflict,
                            $"Tool '{descriptor.Entry.Id}' was refused (tool_call): {ResourceLeaseMessages.Describe(claim, decision.Conflicts)}",
                            "lease", decision.Conflicts.FirstOrDefault()?.Id.ToString("N"), cancellationToken).ConfigureAwait(false);
                        return Blocked(
                            $"Another run is using this resource: {ResourceLeaseMessages.Describe(claim, decision.Conflicts)} Wait for it to finish, "
                            + "work in a different worktree, or choose a different file.",
                            errorCategory: RunErrorTaxonomy.ResourceConflict);
                    }
                }
            }

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
                    // Announce the POLICY park as well as the approval-gate park. Only the
                    // approval layer used to emit approval.requested, so a call held by the
                    // PDP was invisible in the run's event log: the UI could not show the
                    // pending decision or offer the button, and a user watching a "stuck"
                    // run had nothing to act on. The permission_request_id is what the
                    // decision endpoint accepts for this kind of park, so it travels as
                    // approval_id and the client needs no special case.
                    await AppendEventAsync(execution.RunId, "approval.requested",
                        $"Approval requested for tool '{descriptor.Entry.Id}' (resource authorization).", new
                        {
                            approval_id = authorization.PermissionRequestId,
                            permission_request_id = authorization.PermissionRequestId,
                            execution_id = execution.Id,
                            task_id = scope.TaskId,
                            tool_id = descriptor.Entry.Id,
                            risk = execution.Risk,
                            status = authorization.Status,
                            lane_key = execution.LaneKey
                        }, cancellationToken, scope.TaskId, descriptor.Entry.Id, "warning").ConfigureAwait(false);
                    await RaiseTopicAsync(scope, GovernanceTopics.ApprovalRequested,
                        $"Tool '{descriptor.Entry.Id}' ({execution.Risk} risk) is waiting for a resource-authorization decision.",
                        "approval", authorization.PermissionRequestId?.ToString(), cancellationToken).ConfigureAwait(false);
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
                    await _executions.FailAsync(execution.Id, "failed", authorization.ErrorCategory ?? RunErrorTaxonomy.NotAuthorized, authorization.Message ?? "Tool authorization was denied.", cancellationToken).ConfigureAwait(false);
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
                    // A person's standing command-prefix rule is honored first: a covered call
                    // never reaches a human, and its audit says where it came from (todo E7).
                    var minted = await HonorCommandPrefixAsync(scope, descriptor.Entry, execution, cancellationToken).ConfigureAwait(false)
                        ?? await _executions.TryMintPreAuthorizedApprovalAsync(execution.Id, cancellationToken).ConfigureAwait(false);
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
                    // A call that was already approved (pre-authorized) owes nobody a look.
                    if (execution.ApprovalId is not null && !string.Equals(execution.Status, "approved", StringComparison.OrdinalIgnoreCase))
                        await RaiseTopicAsync(scope, GovernanceTopics.ApprovalRequested,
                            $"Tool '{descriptor.Entry.Id}' ({execution.Risk} risk) is waiting for approval.",
                            "approval", execution.ApprovalId?.ToString(), cancellationToken).ConfigureAwait(false);
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

    public Task<ToolDispatchResultDto> ResumeAsync(string executionId, CancellationToken cancellationToken = default) =>
        ResumeCoreAsync(executionId, authority: null, cancellationToken);

    public Task<ToolDispatchResultDto> ResumeAsync(
        string executionId,
        RunExecutionAuthority authority,
        CancellationToken cancellationToken = default) =>
        ResumeCoreAsync(executionId, authority, cancellationToken);

    private async Task<ToolDispatchResultDto> ResumeCoreAsync(
        string executionId,
        RunExecutionAuthority? authority,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(executionId, out var executionGuid)) return Blocked("execution_id must be a valid id.");
        var execution = await _executions.FindAsync(executionGuid, cancellationToken).ConfigureAwait(false);
        if (execution is null) return Blocked("Tool execution was not found.");

        // A running row this process holds is a genuinely live call, not a
        // stale remnant: answer already_running before the authorization phase
        // can clobber the row's status back to requested underneath the call.
        var wasLocallyTracked = _inFlightCalls.IsTracked(executionGuid);
        if (execution.Status == "running" && wasLocallyTracked)
        {
            return new ToolDispatchResultDto { Status = ToolDispatchStatus.Blocked, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, ErrorCategory = RunErrorTaxonomy.ToolAlreadyRunning, Message = "Tool execution is already running." };
        }

        // Register BEFORE the durable start claim. Once the execution becomes
        // running, run-control cancellation must be able to signal this exact
        // process during the narrow claim-to-provider window, not only after the
        // provider call has already begun.
        using var inFlight = _inFlightCalls.TryRegister(execution.RunId, execution.Id);
        if (inFlight is null)
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
                var denied = await _executions.FailAsync(execution.Id, "failed", authorization.ErrorCategory ?? RunErrorTaxonomy.NotAuthorized, authorization.Message ?? "Tool authorization was denied.", cancellationToken).ConfigureAwait(false);
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
        ToolExecutionStartDecision start;
        if (authority is not null)
        {
            if (_executions is not ILeaseFencedToolExecutionCoordinator fencedExecutions)
            {
                return Blocked(
                    "The configured tool execution coordinator does not support run-lease fencing.",
                    execution,
                    RunErrorTaxonomy.RunLeaseLost);
            }
            start = await fencedExecutions.TryStartUnderRunLeaseAsync(
                executionGuid,
                authority,
                allowStaleRunningReset: !wasLocallyTracked,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            start = await _executions.TryStartAsync(
                executionGuid,
                allowStaleRunningReset: !wasLocallyTracked,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        switch (start.Status)
        {
            case "awaiting_approval":
                return new ToolDispatchResultDto { Status = ToolDispatchStatus.AwaitingApproval, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, Message = start.Message, ParkExpired = start.ParkExpired };
            case "awaiting_resume":
                return new ToolDispatchResultDto { Status = ToolDispatchStatus.AwaitingResume, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, Message = start.Message };
            case RunErrorTaxonomy.OutcomeUnknown:
                // A stale "running" row left by a crashed host is also reported as an
                // unknown outcome rather than a run-level pause: the caller feeds it
                // back to the worker, which can re-read the workspace and decide.
                await AppendEventAsync(execution.RunId, "tool.execution.outcome_unknown",
                    $"Tool '{execution.ToolId}' was left running by a previous host; its outcome is unknown.", new
                    {
                        execution_id = execution.Id,
                        task_id = execution.TaskId,
                        tool_id = execution.ToolId,
                        error_category = RunErrorTaxonomy.OutcomeUnknown,
                        message = SafeMessage(start.Message),
                        paused = false
                    }, cancellationToken, execution.TaskId, execution.ToolId, "warning").ConfigureAwait(false);
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
            case RunErrorTaxonomy.RunLeaseLost:
                return new ToolDispatchResultDto { Status = ToolDispatchStatus.Blocked, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, ErrorCategory = RunErrorTaxonomy.RunLeaseLost, Message = start.Message };
            case "running":
                execution = start.Execution ?? execution;
                break;
            default:
                return new ToolDispatchResultDto { Status = ToolDispatchStatus.Blocked, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, ErrorCategory = start.Status, Message = start.Message };
        }

        // Cancellation may have committed immediately after the start claim. The
        // call has not reached the provider yet, so this outcome is still known:
        // cancel the execution instead of manufacturing outcome_unknown.
        if (inFlight.Token.IsCancellationRequested)
        {
            const string cancelledBeforeProvider = "The run was cancelled before the tool request reached the provider.";
            var cancelled = await _executions.FailAsync(
                execution.Id,
                "cancelled",
                RunErrorTaxonomy.Cancelled,
                cancelledBeforeProvider,
                CancellationToken.None).ConfigureAwait(false);
            return ResultForFailure(cancelled, ToolDispatchStatus.Blocked);
        }

        if (!await TrySetRunStatusAsync(execution.RunId, "executing", cancellationToken).ConfigureAwait(false))
        {
            RunState? runState = null;
            try
            {
                runState = await _lifecycle.GetRunStateAsync(execution.RunId.ToString(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Could not verify run {RunId} after execution-start status update failed.", execution.RunId);
            }

            if (runState is null
                || runState.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled or RunStatus.Paused
                || runState.CompletedAt is not null)
            {
                var category = runState?.Status == RunStatus.Cancelled
                    ? RunErrorTaxonomy.Cancelled
                    : RunErrorTaxonomy.RunLeaseLost;
                var cancelled = await _executions.FailAsync(
                    execution.Id,
                    "cancelled",
                    category,
                    "The run lost execution authority before the tool request reached the provider.",
                    CancellationToken.None).ConfigureAwait(false);
                return ResultForFailure(cancelled, ToolDispatchStatus.Blocked);
            }
        }
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
            await pump.AppendCommandAsync(parameters, scope.ExecutionRoot, cancellationToken).ConfigureAwait(false);
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
                    // TinadecTools responses commonly report business success inside
                    // the result payload. A wire-level success only means the provider
                    // returned a result; { success:false } is still a real tool failure
                    // (write_file validation, shell exit code, etc.). Record that fact
                    // without changing the completed dispatch outcome so the result can
                    // still flow back to the model for correction.
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
                    if (execution.MutatesWorkspace && execution.WorkspaceSnapshotId is { } observedSnapshot)
                        await ObserveUnclaimedChangesAsync(scope, descriptor.Id, execution, observedSnapshot, cancellationToken).ConfigureAwait(false);
                    if (toolSuccess != false && WorktreeTools.IsMutation(descriptor.Id))
                        await StewardWorktreeAsync(scope, descriptor.Id, execution, response.Result, cancellationToken).ConfigureAwait(false);
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
            // A mutating call that timed out or died may or may not have taken
            // effect, so its outcome stays explicitly unknown — but that ambiguity
            // is now returned to the caller as a tool RESULT instead of freezing the
            // whole run. The former pause was clearable only by a human, which turned
            // "the tool is slow" into "the run is stuck", and a build or test run
            // legitimately outlives a short default. The pre-write workspace snapshot
            // check and the approval gate are unchanged, so the write still cannot be
            // silently replayed; only the report path changed.
            var unknown = await _executions.FailAsync(execution.Id, RunErrorTaxonomy.OutcomeUnknown, finalCategory, message, cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(execution.RunId, "tool.execution.outcome_unknown",
                $"Tool '{descriptor.Id}' finished with an unknown outcome ({finalCategory}); returned to the caller.", new
                {
                    execution_id = execution.Id,
                    task_id = execution.TaskId,
                    tool_id = descriptor.Id,
                    category = finalCategory,
                    message,
                    paused = false
                }, cancellationToken, execution.TaskId, descriptor.Id, "warning").ConfigureAwait(false);
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

        if (CoreVirtualToolPolicy.IsTaskDispatch(wire.ToolId))
        {
            return await ExecuteTaskDispatchToolAsync(scope, wire, cancellationToken).ConfigureAwait(false);
        }

        if (CoreVirtualToolPolicy.IsTaskWait(wire.ToolId) || CoreVirtualToolPolicy.IsPlanUpdate(wire.ToolId))
        {
            // The run engine executes task_wait and plan_update against the task graph it owns;
            // reaching the dispatcher means the call came from outside a worker's tool loop.
            return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = $"{wire.ToolId} can only be called from inside a run's tool loop." };
        }

        if (CoreVirtualToolPolicy.IsReadAttachment(wire.ToolId))
        {
            return _attachments is null
                ? new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = "No attachment store is registered in this host." }
                : await CoreAttachmentReadTool.ExecuteAsync(_attachments, scope, wire, cancellationToken).ConfigureAwait(false);
        }

        if (CoreVirtualToolPolicy.IsTinaChat(wire.ToolId) || CoreVirtualToolPolicy.IsOrganization(wire.ToolId))
        {
            return await ExecuteTinaChatToolAsync(scope, wire, cancellationToken).ConfigureAwait(false);
        }

        if (CoreVirtualToolPolicy.IsGraphView(wire.ToolId))
        {
            return await ExecuteGraphViewAsync(scope, wire, cancellationToken).ConfigureAwait(false);
        }

        if (CoreVirtualToolPolicy.IsRecallEvidence(wire.ToolId))
        {
            return await ExecuteRecallEvidenceAsync(scope, wire, cancellationToken).ConfigureAwait(false);
        }

        if (CoreVirtualToolPolicy.IsEnvironment(wire.ToolId))
        {
            return await ExecuteEnvironmentToolAsync(scope, wire, cancellationToken).ConfigureAwait(false);
        }

        if (!scope.SerializeWorkspaceWrites || !execution.MutatesWorkspace)
        {
            return streaming is not null
                ? await streaming.CallStreamingAsync(scope.ExecutionRoot, wire, timeout, observer, cancellationToken).ConfigureAwait(false)
                : await _provider.CallAsync(scope.ExecutionRoot, wire, timeout, cancellationToken).ConfigureAwait(false);
        }

        var gate = WorkspaceLocks.GetOrAdd(WorkspaceWriteLockKey(scope, wire), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return streaming is not null
                ? await streaming.CallStreamingAsync(scope.ExecutionRoot, wire, timeout, observer, cancellationToken).ConfigureAwait(false)
                : await _provider.CallAsync(scope.ExecutionRoot, wire, timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string WorkspaceWriteLockKey(ToolInvocationScope scope, ToolWireRequestDto wire)
    {
        if (wire.Params is not { ValueKind: JsonValueKind.Object } parameters)
            return scope.ExecutionRoot;

        if (parameters.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "worktree_path", "repository_path", "cwd", "workdir", "workspace_root" })
            {
                if (parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    var candidate = value.GetString()!.Trim();
                    try { return Path.GetFullPath(Path.IsPathRooted(candidate) ? candidate : Path.Combine(scope.ExecutionRoot, candidate)); }
                    catch (ArgumentException) { return scope.ExecutionRoot; }
                }
            }
        }
        return scope.ExecutionRoot;
    }

    /// <summary>
    /// Executes the Core-owned task_dispatch virtual tool: the conversation identity hands
    /// a sub-task to the ordinary dispatch path.
    ///
    /// The sub-task is persisted as a durable run directive rather than written straight
    /// into a checkpoint, because the dispatcher does not own the checkpoint: the engine is
    /// its single writer and materializes the task node on its next tick, where worker
    /// selection, spawnable templates, approval and convergence already work.
    ///
    /// v1 returns as soon as the sub-task is QUEUED. The result text says exactly that —
    /// claiming the sub-agent finished would be a lie the master would then repeat to the
    /// user. The sub-agent's outcome reaches the answer through the close-out evidence.
    /// </summary>
    private async Task<ToolWireResponseDto> ExecuteTaskDispatchToolAsync(
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        CancellationToken cancellationToken)
    {
        string? agent = null;
        string? followUpOf = null;
        string? title = null;
        string? description = null;
        string[] successCriteria = [];
        string[] requiredTools = [];
        string[] requiredCapabilities = [];
        string[] writeScope = [];
        if (wire.Params is { ValueKind: JsonValueKind.Object } parameters)
        {
            if (parameters.TryGetProperty("agent", out var agentElement) && agentElement.ValueKind == JsonValueKind.String)
                agent = agentElement.GetString()?.Trim();
            if (parameters.TryGetProperty("follow_up_of", out var followElement) && followElement.ValueKind == JsonValueKind.String)
                followUpOf = followElement.GetString()?.Trim();
            if (parameters.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String)
                title = titleElement.GetString()?.Trim();
            if (parameters.TryGetProperty("description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String)
                description = descriptionElement.GetString()?.Trim();
            successCriteria = ReadStringArray(parameters, "success_criteria");
            requiredTools = ReadStringArray(parameters, "required_tools");
            requiredCapabilities = ReadStringArray(parameters, "required_capabilities");
            writeScope = ReadStringArray(parameters, "write_scope")
                .Where(entry => !string.IsNullOrWhiteSpace(entry)).Select(entry => entry.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray();
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = "task_dispatch requires a non-empty 'title'." };
        }
        // The executor is validated HERE, while the caller can still correct it in its next turn: a
        // name only discovered to be wrong when the worker is assigned would fail the sub-task after
        // the caller had already moved on believing it was queued.
        if (scope.DispatchRoster is { } roster)
        {
            if (scope.DispatchTargets is { } allowedTargets)
                roster = roster.Where(entry => allowedTargets.Contains(entry.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
            var match = string.IsNullOrWhiteSpace(agent)
                ? null
                : roster.FirstOrDefault(entry => string.Equals(entry.Id, agent, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                var choices = roster.Count == 0
                    ? "This run has no dispatchable executor; do the work yourself or tell the user."
                    : "Choose one of: " + string.Join("; ", roster.Select(entry =>
                        string.IsNullOrWhiteSpace(entry.Description) ? entry.Id : $"{entry.Id} — {entry.Description}")) + ".";
                var problem = string.IsNullOrWhiteSpace(agent)
                    ? "task_dispatch requires 'agent' (the executor id)."
                    : $"'{agent}' is not a dispatchable executor in this run.";
                return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = $"{problem} {choices}" };
            }
            agent = match.Id;
        }

        // Derived from the call, not random: the directive below is de-duplicated per tool call, so a
        // replayed call must hand the model the SAME id the queued sub-task carries (a fresh one would
        // name a task that does not exist, and task_wait on it would fail). It is also the sub-task's
        // task id, which is what its write-scope leases are keyed by.
        var dispatchId = DeterministicGuid($"run:{scope.RunId:N}:task-dispatch:{wire.ToolCallId}");

        // A declared write scope is leased NOW, while the caller can still react: an overlap with
        // another run's work is refused before either side runs a command.
        if (writeScope.Length > 0 && _resourceLeases is not null)
        {
            foreach (var claim in ResourceClaimResolver.ResolveWriteScope(writeScope, scope.WorkspaceRoot))
            {
                var decision = await _resourceLeases.AcquireAsync(new ResourceAcquireRequest(
                    claim, scope.SessionId, scope.RunId, dispatchId, null,
                    $"Declared write scope of sub-task '{title}'.", ResourceLeasePurposes.WriteScope), cancellationToken).ConfigureAwait(false);
                if (decision.Granted) continue;
                await _resourceLeases.ReleaseTaskAsync(scope.RunId, dispatchId, cancellationToken).ConfigureAwait(false);
                await AppendEventAsync(scope.RunId, ResourceLeaseMessages.ConflictEventType,
                    $"Sub-task '{title}' was not dispatched: its write scope overlaps a resource another run holds.",
                    ResourceLeaseMessages.ConflictPayload(claim, decision.Conflicts, scope.RunId, scope.TaskId, ResourceLeaseMessages.DetectedAtWriteScope),
                    cancellationToken, scope.TaskId, CoreTaskDispatchTool.ToolId, "warning").ConfigureAwait(false);
                await RaiseTopicAsync(scope, GovernanceTopics.LeaseConflict,
                    $"Sub-task '{title}' was not dispatched (write_scope): {ResourceLeaseMessages.Describe(claim, decision.Conflicts)}",
                    "lease", decision.Conflicts.FirstOrDefault()?.Id.ToString("N"), cancellationToken).ConfigureAwait(false);
                return new ToolWireResponseDto
                {
                    CallId = wire.ToolCallId,
                    IsSuccess = false,
                    Error = "The sub-task was not dispatched because its write_scope overlaps work another run holds: "
                        + ResourceLeaseMessages.Describe(claim, decision.Conflicts)
                        + " Wait for that work, narrow write_scope, or dispatch it into a separate worktree."
                };
            }
        }

        var payload = JsonSerializer.Serialize(new
        {
            dispatch_id = dispatchId,
            agent,
            follow_up_of = string.IsNullOrWhiteSpace(followUpOf) ? null : followUpOf,
            title,
            description,
            success_criteria = successCriteria,
            required_tools = requiredTools,
            required_capabilities = requiredCapabilities,
            write_scope = writeScope.Length == 0 ? null : writeScope,
            dispatched_by_tool_call = wire.ToolCallId,
            task_id = scope.TaskId
        });
        try
        {
            await _lifecycle.EnqueueRunDirectiveAsync(new RunDirectiveWrite(
                scope.RunId,
                scope.SessionId,
                MessageId: null,
                Kind: "task_dispatch",
                PayloadJson: payload,
                // One dispatch per tool call: a replayed call must not create a second
                // sub-task, and the key is what makes that true across a host crash.
                IdempotencyKey: $"run:{scope.RunId}:task-dispatch:{wire.ToolCallId}"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = $"The sub-task could not be queued: {SafeMessage(ex.Message)}" };
        }

        await AppendEventAsync(scope.RunId, "task.dispatch_requested",
            $"Sub-task '{title}' was queued for dispatch.",
            new
            {
                dispatch_id = dispatchId,
                agent,
                title,
                required_tools = requiredTools,
                required_capabilities = requiredCapabilities,
                write_scope = writeScope,
                requested_by_task_id = scope.TaskId,
                agent_instance_id = scope.AgentInstanceId
            }, cancellationToken, scope.TaskId, CoreTaskDispatchTool.ToolId).ConfigureAwait(false);

        return new ToolWireResponseDto
        {
            CallId = wire.ToolCallId,
            IsSuccess = true,
            Result = JsonSerializer.SerializeToElement(new
            {
                queued = true,
                dispatch_id = dispatchId,
                agent,
                title,
                message = $"Sub-task '{title}' is queued for {agent ?? "an executor"}. Its result is NOT in this reply: call task_wait with this dispatch_id to get it, and do not report it as done before then."
            })
        };
    }

    /// <summary>
    /// Executes a TinaChat tool in-process against Core's own communication state. Authorization
    /// already happened the same way as for any other tool - declared surface intersected with the
    /// frozen manifest, then the per-instance grant - and the communication module re-checks room
    /// membership, audience entitlement and provenance on every call, so no approval gate applies
    /// and none is needed: the only writable surface is a message this participant may send.
    /// </summary>
    private async Task<ToolWireResponseDto> ExecuteTinaChatToolAsync(
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        CancellationToken cancellationToken)
    {
        if (_tinaChat is null)
            return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = "TinaChat is not available in this host." };
        var outcome = await _tinaChat.ExecuteAsync(new TinaChatToolCall(
            scope.TenantId, scope.WorkspaceId, scope.PrincipalId, scope.SessionId, scope.RunId,
            wire.ToolCallId, wire.ToolId, wire.Params)
        {
            // Organization tools act as the member this instance was enrolled as.
            AgentInstanceId = scope.AgentInstanceId
        }, cancellationToken).ConfigureAwait(false);
        if (!outcome.IsSuccess)
            return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = outcome.Error ?? "The chat tool call failed." };
        return new ToolWireResponseDto
        {
            CallId = wire.ToolCallId,
            IsSuccess = true,
            Result = JsonDocument.Parse(outcome.ResultJson).RootElement.Clone(),
        };
    }

    /// <summary>
    /// Serves <c>graph_view</c>: the session as a graph, bounded, in the model-facing shape. Read-only, so
    /// like the other Core-owned reads it needs no approval; the declared tool surface is what grants it.
    /// </summary>
    private async Task<ToolWireResponseDto> ExecuteGraphViewAsync(ToolInvocationScope scope, ToolWireRequestDto wire, CancellationToken cancellationToken)
    {
        if (_topology is null)
            return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = "The session graph is not available in this host." };
        Guid? runId = null;
        var includeFinished = true;
        if (wire.Params is { ValueKind: JsonValueKind.Object } parameters)
        {
            if (parameters.TryGetProperty("run_id", out var run) && run.ValueKind == JsonValueKind.String)
            {
                if (!Guid.TryParse(run.GetString(), out var parsed))
                    return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = $"'{run.GetString()}' is not a run id from graph_view." };
                runId = parsed;
            }
            if (parameters.TryGetProperty("include_finished", out var finished) && finished.ValueKind == JsonValueKind.False) includeFinished = false;
        }
        if (_organization is not null && scope.AgentInstanceId is { } callerInstance)
        {
            // Per-member visibility (todo E5): a member the user restricted to its own run sees the
            // graph only of that run; a standing member without one is told there is nothing to see.
            var visibility = await _organization.VisibilityForInstanceAsync(scope.SessionId, callerInstance, cancellationToken).ConfigureAwait(false);
            if (visibility?.Restricted == true)
            {
                if (visibility.CurrentRunId is { } own)
                    runId = own;
                else
                    return new ToolWireResponseDto
                    {
                        CallId = wire.ToolCallId,
                        IsSuccess = true,
                        Result = JsonSerializer.SerializeToElement(new { runs = Array.Empty<object>(), note = "Your visibility is restricted to your own run, and you have none: there is nothing in the graph for you." }),
                    };
            }
        }
        var view = await _topology.GetAsync(scope.SessionId, new SessionTopologyQuery(IncludeFinishedRuns: includeFinished, RunId: runId), cancellationToken)
            .ConfigureAwait(false);
        if (view is null)
            return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = "This run's session could not be read." };
        return new ToolWireResponseDto
        {
            CallId = wire.ToolCallId,
            IsSuccess = true,
            Result = JsonSerializer.SerializeToElement(SessionTopologyProjection.ForModel(view)),
        };
    }

    /// <summary>
    /// The worktree steward (todo R5). A created worktree is assigned to the run that created it: an
    /// exclusive <c>assignment</c> lease with no task, so it outlives the creating call and task — a
    /// sub-task of the same run may work in it (its write scope inside the worktree is the same run's
    /// business), while another run's writer is refused with this run named as the holder. Removing
    /// the worktree releases the assignment; the run's end releases whatever is left. The path comes
    /// from the tool's own result, the only authority on where the worktree really is. Best-effort:
    /// the git operation already happened, and its outcome stands.
    /// </summary>
    private async Task StewardWorktreeAsync(ToolInvocationScope scope, string toolId, ToolExecutionSnapshot execution, JsonElement? result, CancellationToken cancellationToken)
    {
        if (_resourceLeases is null) return;
        if (result is not { ValueKind: JsonValueKind.Object } payload
            || !payload.TryGetProperty("path", out var pathNode) || pathNode.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(pathNode.GetString())) return;
        var key = ResourceClaimResolver.Absolute(pathNode.GetString()!, scope.ExecutionRoot);
        var branch = payload.TryGetProperty("branch", out var branchNode) && branchNode.ValueKind == JsonValueKind.String ? branchNode.GetString() : null;
        try
        {
            if (string.Equals(toolId, WorktreeTools.Create, StringComparison.OrdinalIgnoreCase))
            {
                var claim = new ResourceClaim(ResourceLeaseKinds.Worktree, key, Exclusive: true);
                var decision = await _resourceLeases.AcquireAsync(new ResourceAcquireRequest(claim, scope.SessionId, scope.RunId,
                    TaskId: null, AgentInstanceId: scope.AgentInstanceId,
                    Reason: $"Worktree {key}{(branch is null ? "" : $" on {branch}")}, assigned to the run that created it.",
                    Purpose: ResourceLeasePurposes.Assignment), cancellationToken).ConfigureAwait(false);
                if (decision.Granted)
                {
                    await AppendEventAsync(execution.RunId, "worktree.assigned", $"Worktree {key} is assigned to this run.", new
                    {
                        path = key, branch, lease_id = decision.Lease!.Id, run_id = scope.RunId, task_id = scope.TaskId, agent_instance_id = scope.AgentInstanceId
                    }, cancellationToken, scope.TaskId, toolId).ConfigureAwait(false);
                }
                else
                {
                    await AppendEventAsync(execution.RunId, ResourceLeaseMessages.ConflictEventType,
                        $"Worktree {key} was created but another run already holds it.",
                        ResourceLeaseMessages.ConflictPayload(claim, decision.Conflicts, execution.RunId, execution.TaskId, ResourceLeaseMessages.DetectedAfterTheFact),
                        cancellationToken, scope.TaskId, toolId, "warning").ConfigureAwait(false);
                    await RaiseTopicAsync(scope, GovernanceTopics.LeaseConflict,
                        $"Worktree {key} was created by this run but is held elsewhere: {ResourceLeaseMessages.Describe(claim, decision.Conflicts)}",
                        "path", key, cancellationToken).ConfigureAwait(false);
                }
                return;
            }

            // Removal: release this run's assignment of that worktree (another run's hold would have
            // refused the removal call itself before it ran).
            var released = 0;
            foreach (var lease in await _resourceLeases.ListActiveAsync(scope.SessionId, cancellationToken).ConfigureAwait(false))
            {
                if (lease.RunId != scope.RunId || !string.Equals(lease.Kind, ResourceLeaseKinds.Worktree, StringComparison.Ordinal)
                    || !string.Equals(lease.Purpose, ResourceLeasePurposes.Assignment, StringComparison.Ordinal)
                    || !string.Equals(ResourceLeasePolicy.Normalize(lease.ResourceKey), ResourceLeasePolicy.Normalize(key), StringComparison.Ordinal)) continue;
                released += await _resourceLeases.ReleaseAsync(lease.Id, cancellationToken).ConfigureAwait(false);
            }
            await AppendEventAsync(execution.RunId, "worktree.released", $"Worktree {key} was removed; its assignment is released.", new
            {
                path = key, released, run_id = scope.RunId, task_id = scope.TaskId
            }, cancellationToken, scope.TaskId, toolId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogWarning(ex, "Worktree steward could not record {ToolId} of {Path} for run {RunId}.", toolId, key, scope.RunId);
        }
    }

    /// <summary>
    /// Serves <c>recall_evidence</c> inside the caller's own session — the session is the scope's, never
    /// an argument, so a worker cannot read another session's archive by naming it.
    /// </summary>
    private async Task<ToolWireResponseDto> ExecuteRecallEvidenceAsync(ToolInvocationScope scope, ToolWireRequestDto wire, CancellationToken cancellationToken)
    {
        if (_evidence is null)
            return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = "The evidence archive is not available in this host." };
        var parsed = EvidenceRecallArguments.Parse(wire.Params);
        if (parsed.Error is { } error)
            return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = error };
        var runId = parsed.RunId;
        if (_organization is not null && scope.AgentInstanceId is { } callerInstance)
        {
            // Per-member visibility (todo E5): a restricted member recalls only its own run's evidence;
            // a standing member without one is told there is nothing to recall.
            var visibility = await _organization.VisibilityForInstanceAsync(scope.SessionId, callerInstance, cancellationToken).ConfigureAwait(false);
            if (visibility?.Restricted == true)
            {
                if (visibility.CurrentRunId is { } own)
                    runId = own;
                else
                    return new ToolWireResponseDto
                    {
                        CallId = wire.ToolCallId,
                        IsSuccess = true,
                        Result = JsonSerializer.SerializeToElement(new { hits = Array.Empty<object>(), note = "Your visibility is restricted to your own run, and you have none: there is no evidence for you to recall." }),
                    };
            }
        }
        var result = await _evidence.RecallAsync(new EvidenceRecallQuery(scope.TenantId, scope.WorkspaceId, scope.SessionId,
            parsed.Query!, parsed.Kinds, runId, parsed.Limit), cancellationToken).ConfigureAwait(false);
        return new ToolWireResponseDto
        {
            CallId = wire.ToolCallId,
            IsSuccess = true,
            Result = JsonSerializer.SerializeToElement(EvidenceRecallArguments.ForModel(result)),
        };
    }

    /// <summary>
    /// Serves the environment steward's tools (todo E1) for the calling run: the run and session are the
    /// scope's, never arguments, so a worker can only take or give back environments for its own run.
    /// </summary>
    private async Task<ToolWireResponseDto> ExecuteEnvironmentToolAsync(ToolInvocationScope scope, ToolWireRequestDto wire, CancellationToken cancellationToken)
    {
        if (_environments is not { } registry)
            return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = "No environment registry is available in this host." };
        static string? Text(JsonElement parameters, string name) =>
            parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() is { Length: > 0 } text ? text : null
                : null;
        ToolWireResponseDto Ok(object result) => new() { CallId = wire.ToolCallId, IsSuccess = true, Result = JsonSerializer.SerializeToElement(result) };
        object Describe(EnvironmentView environment) => new
        {
            key = environment.Key,
            kind = environment.Kind,
            name = environment.DisplayName,
            description = environment.Description,
            connection = JsonDocument.Parse(environment.ConnectionJson).RootElement.Clone(),
            status = environment.Status,
            capacity = environment.Capacity,
            free_slots = environment.FreeSlots,
            held_by = environment.Holders.Select(holder => new
            {
                slot = holder.Slot,
                run_id = holder.RunId?.ToString("N"),
                this_run = holder.RunId == scope.RunId,
                reason = holder.Reason
            }).ToArray()
        };

        var parameters = wire.Params ?? default;
        try
        {
            if (string.Equals(wire.ToolId, CoreVirtualToolPolicy.EnvironmentListToolId, StringComparison.OrdinalIgnoreCase))
            {
                var kind = Text(parameters, "kind");
                var all = await registry.ListAsync(cancellationToken).ConfigureAwait(false);
                return Ok(new { environments = all.Where(x => kind is null || x.Kind == kind).Take(50).Select(Describe).ToArray(), total = all.Count });
            }
            if (string.Equals(wire.ToolId, CoreVirtualToolPolicy.EnvironmentAcquireToolId, StringComparison.OrdinalIgnoreCase))
            {
                var result = await registry.AcquireAsync(new EnvironmentAcquireRequest(scope.SessionId, scope.RunId, scope.TaskId, scope.AgentInstanceId,
                    Text(parameters, "key"), Text(parameters, "kind"), Text(parameters, "reason") ?? string.Empty), cancellationToken).ConfigureAwait(false);
                if (result.Granted)
                {
                    await AppendEventAsync(scope.RunId, "environment.assigned", $"Environment {result.Environment!.Key} slot {result.Slot} was assigned to the run.", new
                    {
                        key = result.Environment.Key, kind = result.Environment.Kind, slot = result.Slot, lease_id = result.LeaseId, run_id = scope.RunId, task_id = scope.TaskId
                    }, cancellationToken, scope.TaskId, wire.ToolId).ConfigureAwait(false);
                    return Ok(new { granted = true, slot = result.Slot, environment = Describe(result.Environment) });
                }
                // A refusal is an answer, not a failure: the model reads who holds it and decides.
                return Ok(new
                {
                    granted = false,
                    reason = result.Error,
                    held_by = result.BusyHolders.Select(holder => new { slot = holder.Slot, run_id = holder.RunId?.ToString("N"), reason = holder.Reason }).ToArray()
                });
            }
            var key = Text(parameters, "key");
            if (key is null) return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = "environment_release needs the key of the environment to give back." };
            var released = await registry.ReleaseAsync(scope.RunId, key, cancellationToken).ConfigureAwait(false);
            if (released > 0)
                await AppendEventAsync(scope.RunId, "environment.released", $"The run gave back environment {key}.", new
                {
                    key, released, run_id = scope.RunId, task_id = scope.TaskId
                }, cancellationToken, scope.TaskId, wire.ToolId).ConfigureAwait(false);
            return Ok(new { released, note = released == 0 ? "This run held no slot of that environment." : null });
        }
        catch (EnvironmentRegistryException ex)
        {
            return new ToolWireResponseDto { CallId = wire.ToolCallId, IsSuccess = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Raises a governance fact for the run's standing subscribers. Best-effort: the call the fact came
    /// from has its own outcome, and a reviewer that cannot be told must never change it.
    /// </summary>
    private async Task RaiseTopicAsync(ToolInvocationScope scope, string topic, string summary, string? subjectKind, string? subjectId, CancellationToken cancellationToken)
    {
        if (_topics is null) return;
        try
        {
            await _topics.RaiseAsync(new GovernanceTopicSignal(scope.SessionId, scope.RunId, topic, summary, subjectKind, subjectId), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not raise {Topic} for run {RunId}.", topic, scope.RunId);
        }
    }

    private static string[] ReadStringArray(JsonElement parameters, string propertyName) =>
        parameters.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

    /// <summary>
    /// Covers this execution when a person's standing "commands that start with this are fine"
    /// rule matches the command the call would run (todo E7). The match is the engine's own
    /// against the execution's parameters — a caller naming a rule id in its arguments cannot
    /// ride in on it. The mint's <see cref="PreAuthorizationMintResult.Source"/> becomes
    /// <c>approval_rule</c> so the audit says where it came from.
    /// </summary>
    private async Task<PreAuthorizationMintResult?> HonorCommandPrefixAsync(
        ToolInvocationScope scope,
        ToolManifestEntryDto descriptor,
        ToolExecutionSnapshot execution,
        CancellationToken cancellationToken)
    {
        var ruleId = await MatchCommandRuleIdAsync(descriptor, execution, scope.SessionId, cancellationToken).ConfigureAwait(false);
        if (ruleId is not { } matchedRuleId) return null;
        // A covered call never reaches a human: mint from the rule the person wrote, always — the
        // run-grant and auto-policy paths would name the wrong source, or ask a human who was never
        // meant to be there.
        var minted = await _executions.MintApprovalFromRuleAsync(execution.Id, matchedRuleId, cancellationToken).ConfigureAwait(false);
        if (minted is not null && _approvalRules is not null)
        {
            try { await _approvalRules.RecordUseAsync(matchedRuleId, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not record the use of approval rule {RuleId} for execution {ExecutionId}.", matchedRuleId, execution.Id);
            }
        }
        return minted;
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

        // WS-4: create_workspace is the Core-reserved projectless bootstrap channel
        // (IncludesCoreReserved). Its authorization is the approval gate (the tool
        // is RequiresApproval/high-risk) plus the workspace binder's own path
        // validation — the resource envelope governs provider-backed workspace
        // tools and deliberately does not gate the bootstrap tool that CREATES the
        // workspace a grant would be relative to. (The historical grant check here
        // was a no-op under the coarse ["workspace"] seeds.)

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
            scope.ExecutionRoot,
            command,
            status,
            DateTimeOffset.UtcNow));
    }

    private async Task<(ToolManifestEntryDto? Entry, string? Error)> FindV2ToolAsync(ToolInvocationScope scope, string toolId, CancellationToken cancellationToken)
    {
        // A Core-owned virtual tool has no child-process entry by construction, so the run's frozen
        // manifest is where its entry lives. That is a lookup rule, not a bypass: a project session
        // still reads the live manifest and still requires it to match the hash the run froze, and a
        // projectless session still hashes its synthetic list against the run binding.
        var frozen = scope.AuthorizedToolManifest?
            .FirstOrDefault(item => string.Equals(item.Id, toolId, StringComparison.OrdinalIgnoreCase));

        if (CoreVirtualToolPolicy.IsProjectlessScope(scope.ProjectId))
        {
            // Projectless (free-conversation) scope: no live provider exists. Core-owned
            // virtual tools are the only legal calls, and the run-frozen manifest is the
            // sole declaration source.
            if (!CoreVirtualToolPolicy.IsCoreVirtual(toolId)) return (null, $"Unknown tool '{toolId}'.");
            if (frozen is null) return (null, $"Tool '{toolId}' is not present in the frozen authorized manifest.");
            if (string.IsNullOrWhiteSpace(scope.FrozenToolManifestHash)
                || !string.Equals(ToolManifestHasher.Compute(scope.AuthorizedToolManifest!), scope.FrozenToolManifestHash, StringComparison.OrdinalIgnoreCase))
            {
                return (null, "The frozen tool manifest hash does not match the run binding.");
            }
            return (ToFrozenEntry(frozen), null);
        }

        var manifest = await _provider.GetManifestAsync(scope.ExecutionRoot, cancellationToken).ConfigureAwait(false);
        if (manifest.ProtocolVersion != 2) return (null, "TinadecTools manifest v2 is required for autonomous dispatch.");
        if (string.IsNullOrWhiteSpace(scope.FrozenToolManifestHash)
            || !string.Equals(manifest.ManifestHash, scope.FrozenToolManifestHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ToolManifestHasher.Compute(manifest.Tools), scope.FrozenToolManifestHash, StringComparison.OrdinalIgnoreCase))
        {
            return (null, "The TinadecTools manifest changed after run admission.");
        }
        var descriptor = manifest.Tools.FirstOrDefault(item => string.Equals(item.Id, toolId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            if (CoreVirtualToolPolicy.RequiresLiveManifestEntry(toolId)) return (null, $"Unknown tool '{toolId}'.");
            // The child process does not offer it, and never will: only a run that declared the
            // virtual tool can dispatch it, so naming an id is not a grant.
            return frozen is null
                ? (null, $"Tool '{toolId}' is not present in the frozen authorized manifest.")
                : (ToFrozenEntry(frozen), null);
        }
        return frozen is null || !ToolManifestHasher.Equivalent(descriptor, frozen)
            ? (null, $"Tool '{toolId}' is not present in the frozen authorized manifest.")
            : (descriptor, null);
    }

    /// <summary>
    /// The one place a frozen manifest entry becomes a dispatch descriptor. Both the projectless
    /// path and the Core-virtual-tool path in a project session need it, and a copy per path is how
    /// the two would drift out of the run's authorization.
    /// </summary>
    private static ToolManifestEntryDto ToFrozenEntry(FrozenToolManifestEntry frozen) => new()
    {
        Id = frozen.Id,
        Description = frozen.Description,
        RequiresApproval = frozen.RequiresApproval,
        InputSchema = frozen.InputSchema.Clone(),
        Risk = frozen.Risk,
        MutatesWorkspace = frozen.MutatesWorkspace,
        RetrySafety = frozen.RetrySafety,
        ConfirmationFields = frozen.ConfirmationFields.ToArray()
    };

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

    private async Task<bool> TrySetRunStatusAsync(Guid runId, string status, CancellationToken cancellationToken)
    {
        try
        {
            await _lifecycle.SetRunStatusAsync(runId.ToString(), status, null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not move run {RunId} to {Status}", runId, status);
            return false;
        }
    }

    /// <summary>
    /// The after-the-fact half of resource governance, for a call that named no file (a shell
    /// command, <c>command_run</c>, a git mutation) and therefore claimed nothing up front.
    ///
    /// Compares the pre-write snapshot this call was guarded by with the workspace as it is now and
    /// publishes what changed (<c>workspace.changes_observed</c>), what fell outside the task's
    /// declared write scope (<c>scope.violation</c>), and what overlaps a resource another live run
    /// holds (<c>lease.conflict</c>, detected after the fact). It never changes the call's outcome:
    /// the write already happened, and the point is that a reviewer sees it before a merge does.
    /// Bounded for large workspaces and clusters: at most 50 paths are published and probed.
    /// </summary>
    private async Task ObserveUnclaimedChangesAsync(
        ToolInvocationScope scope,
        string toolId,
        ToolExecutionSnapshot execution,
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        const int PathBudget = 50;
        if (_resourceLeases is null) return;
        // A call that named its file already holds a lease on exactly that file.
        if (ResourceClaimResolver.Resolve(toolId, ReadStringArguments(execution.ParametersJson), scope.ExecutionRoot, mutatesWorkspace: true) is not null) return;
        try
        {
            var changes = await _snapshots.ListFileChangesAsync(snapshotId, cancellationToken).ConfigureAwait(false);
            var changed = changes
                .Where(change => !string.Equals(change.Status, "unchanged", StringComparison.OrdinalIgnoreCase))
                .Select(change => ResourceClaimResolver.Absolute(change.Path, scope.WorkspaceRoot))
                .ToArray();
            if (changed.Length == 0) return;
            var sample = changed.Take(PathBudget).ToArray();
            await AppendEventAsync(execution.RunId, "workspace.changes_observed",
                $"Tool '{toolId}' changed {changed.Length} file(s).", new
                {
                    execution_id = execution.Id,
                    task_id = execution.TaskId,
                    tool_id = toolId,
                    changed_count = changed.Length,
                    paths = sample,
                    truncated = changed.Length > sample.Length
                }, cancellationToken, execution.TaskId, toolId).ConfigureAwait(false);

            var own = await _resourceLeases.ListTaskAsync(execution.RunId, execution.TaskId, cancellationToken).ConfigureAwait(false);
            var outside = ResourceLeasePolicy.OutsideScope(changed, own);
            if (outside.Count > 0)
            {
                await AppendEventAsync(execution.RunId, "scope.violation",
                    $"Tool '{toolId}' changed {outside.Count} file(s) outside the task's declared write scope.", new
                    {
                        execution_id = execution.Id,
                        task_id = execution.TaskId,
                        tool_id = toolId,
                        outside_count = outside.Count,
                        paths = outside.Take(PathBudget).ToArray(),
                        write_scope = own.Where(lease => lease.Purpose == ResourceLeasePurposes.WriteScope).Select(lease => lease.ResourceKey).ToArray()
                    }, cancellationToken, execution.TaskId, toolId, "warning").ConfigureAwait(false);
                await RaiseTopicAsync(scope, GovernanceTopics.LeaseConflict,
                    $"Tool '{toolId}' changed {outside.Count} file(s) outside its task's declared write scope, e.g. {outside[0]}.",
                    "task", execution.TaskId.ToString("N"), cancellationToken).ConfigureAwait(false);
            }

            foreach (var path in sample)
            {
                var claim = new ResourceClaim(ResourceLeaseKinds.Path, path, Exclusive: true);
                var holders = await _resourceLeases.ProbeAsync(claim, execution.RunId, cancellationToken).ConfigureAwait(false);
                if (holders.Count == 0) continue;
                await AppendEventAsync(execution.RunId, ResourceLeaseMessages.ConflictEventType,
                    $"Tool '{toolId}' changed '{path}', which another run holds.",
                    ResourceLeaseMessages.ConflictPayload(claim, holders, execution.RunId, execution.TaskId, ResourceLeaseMessages.DetectedAfterTheFact),
                    cancellationToken, execution.TaskId, toolId, "warning").ConfigureAwait(false);
                await RaiseTopicAsync(scope, GovernanceTopics.LeaseConflict,
                    $"Tool '{toolId}' already changed '{path}' (after_the_fact): {ResourceLeaseMessages.Describe(claim, holders)}",
                    "lease", holders[0].Id.ToString("N"), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Observation must never turn a completed call into a failure.
            _logger.LogDebug(ex, "Could not observe the changes of execution {ExecutionId}.", execution.Id);
        }
    }

    /// <summary>A stable id derived from a key (RFC 4122 variant bits set so it reads as a normal GUID).</summary>
    private static Guid DeterministicGuid(string key)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)).AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
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
        // A person's standing prefix rule rides to the PDP as a hint (todo E7): the match here is the
        // engine's own against the frozen parameters, and the PDP re-verifies before releasing — so a
        // default-ask shell call covered by a rule never waits for a click the person already gave.
        var commandRuleId = await MatchCommandRuleIdAsync(descriptor, execution, scope.SessionId, cancellationToken).ConfigureAwait(false);
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
            scope.PermissionMode,
            ResourceClaim(descriptor, execution, scope),
            commandRuleId), cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// The standing prefix rule covering this call's command, if any (todo E7). Shared by the PDP
    /// hint and the approval-layer mint; purely a lookup — the use is recorded once, at the mint.
    /// </summary>
    private async Task<Guid?> MatchCommandRuleIdAsync(ToolManifestEntryDto descriptor, ToolExecutionSnapshot execution, Guid sessionId, CancellationToken cancellationToken)
    {
        if (_approvalRules is null || execution.ParametersJson is not { Length: > 0 } parametersJson) return null;
        string? command;
        try { command = CommandPrefixRules.CommandOf(descriptor.Id, JsonDocument.Parse(parametersJson).RootElement); }
        catch (JsonException) { return null; }
        if (command is null) return null;
        var rule = await _approvalRules.MatchCommandAsync(sessionId, descriptor.Id, command, cancellationToken).ConfigureAwait(false);
        return rule?.Id;
    }

    private static CapabilityClaim ToolClaim(ToolManifestEntryDto descriptor) => new(
        "tool.invoke",
        descriptor.MutatesWorkspace ? "mutate" : "read",
        $"tool://{descriptor.Id}");

    /// <summary>
    /// WS-8 resource dimension: the concrete workspace target of a file tool, so
    /// the PDP can enforce the instance's resource prefix grants. Tools without a
    /// single path (shell, mcp_*, git_*) yield null and keep the level-only rule.
    /// A target that cannot be expressed workspace-relative also yields null —
    /// the tool process resolves it against the same workspace root and refuses
    /// anything outside, so the fallback still cannot widen access.
    /// </summary>
    private static CapabilityClaim? ResourceClaim(
        ToolManifestEntryDto descriptor,
        ToolExecutionSnapshot execution,
        ToolInvocationScope scope) =>
        ToolResourcePathRegistry.TryBuildResourceClaim(
            descriptor.Id, execution.ParametersJson, scope.ExecutionRoot, descriptor.MutatesWorkspace);

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

    /// <summary>
    /// Top-level string arguments as a lookup for <see cref="ResourceClaimResolver"/>. Only strings
    /// are read: a claim is always named by a path or a directory, so a nested object or array is not
    /// a target the ledger can reason about. A parameter that is not a JSON object yields no keys.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> ReadStringArguments(string parametersJson)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(parametersJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
        }
        catch (JsonException)
        {
            // Unparseable parameters are reported by the execution layer; the ledger simply claims nothing.
            return result;
        }
        return result;
    }

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
        // The margin is unconditional — not only for a call that carries an explicit
        // timeout_ms. The wire budget must always outlive the tool's own deadline, so
        // the tool reports its timeout as a tool RESULT the worker can read. Equal
        // budgets on the two sides made Core win the race every time and severed the
        // call before the tool could say what happened.
        return TimeSpan.FromSeconds(Math.Clamp(baseSeconds + WireTimeoutMarginSeconds, 1, MaxWireTimeoutSeconds));
    }

    internal const int WireTimeoutMarginSeconds = 30;
    // The tool-side timeout ceiling (30 minutes) plus the wire margin.
    internal const int MaxWireTimeoutSeconds = 1800 + WireTimeoutMarginSeconds;

    /// <summary>
    /// Reads the embedded <c>success</c> field of any tool result that exposes
    /// one. Null when the payload does not carry an explicit business outcome.
    /// The tool id remains part of the signature for call-site/test compatibility
    /// and diagnostics even though outcome extraction is schema-driven now.
    /// </summary>
    internal static bool? ReadEmbeddedToolSuccess(string toolId, JsonElement? result)
    {
        _ = toolId;
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
