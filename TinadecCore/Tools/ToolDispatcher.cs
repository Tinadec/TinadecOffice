using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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

    private readonly IToolProvider _provider;
    private readonly IToolInvocationScopeResolver _scopeResolver;
    private readonly IToolExecutionCoordinator _executions;
    private readonly IAuthorizationService _authorization;
    private readonly ILifecycleManager _lifecycle;
    private readonly ToolDispatchOptions _options;
    private readonly ILogger<ToolDispatcher> _logger;

    public ToolDispatcher(
        IToolProvider provider,
        IToolInvocationScopeResolver scopeResolver,
        IToolExecutionCoordinator executions,
        IAuthorizationService authorization,
        ILifecycleManager lifecycle,
        ToolDispatchOptions options,
        ILogger<ToolDispatcher> logger)
    {
        _provider = provider;
        _scopeResolver = scopeResolver;
        _executions = executions;
        _authorization = authorization;
        _lifecycle = lifecycle;
        _options = options;
        _logger = logger;
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
                LeaseUses: request.LeaseUses), cancellationToken).ConfigureAwait(false);
            var execution = preparation.Execution;

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
                    execution = await _executions.EnsureApprovalAsync(execution.Id, cancellationToken).ConfigureAwait(false);
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
                    risk = execution.Risk
                }, cancellationToken, scope.TaskId, descriptor.Entry.Id).ConfigureAwait(false);

                if (execution.RequiresApproval)
                {
                    await AppendEventAsync(scope.RunId, "approval.requested", $"Approval requested for tool '{descriptor.Entry.Id}'.", new
                    {
                        approval_id = execution.ApprovalId,
                        execution_id = execution.Id,
                        task_id = scope.TaskId,
                        tool_id = descriptor.Entry.Id,
                        risk = execution.Risk
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
    }

    public async Task<ToolDispatchResultDto> ResumeAsync(string executionId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(executionId, out var executionGuid)) return Blocked("execution_id must be a valid id.");
        var execution = await _executions.FindAsync(executionGuid, cancellationToken).ConfigureAwait(false);
        if (execution is null) return Blocked("Tool execution was not found.");

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
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException or DirectoryNotFoundException)
        {
            return Blocked(SafeMessage(ex.Message), execution);
        }

        var start = await _executions.TryStartAsync(executionGuid, cancellationToken).ConfigureAwait(false);
        switch (start.Status)
        {
            case "awaiting_approval":
                return new ToolDispatchResultDto { Status = ToolDispatchStatus.AwaitingApproval, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, Message = start.Message };
            case "awaiting_resume":
                return new ToolDispatchResultDto { Status = ToolDispatchStatus.AwaitingResume, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, Message = start.Message };
            case "outcome_unknown":
                await PauseForUnknownOutcomeAsync(execution, start.Message, cancellationToken).ConfigureAwait(false);
                return new ToolDispatchResultDto { Status = ToolDispatchStatus.OutcomeUnknown, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, ErrorCategory = "outcome_unknown", Message = start.Message };
            case "not_approved":
                return new ToolDispatchResultDto { Status = ToolDispatchStatus.NotApproved, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, ErrorCategory = "not_approved", Message = start.Message };
            case "cancelled":
                return new ToolDispatchResultDto { Status = ToolDispatchStatus.Blocked, ExecutionId = executionId, ApprovalId = execution.ApprovalId?.ToString(), Attempt = execution.Attempt, ErrorCategory = "run_cancelled", Message = start.Message };
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

        var safeReadRetry = !execution.MutatesWorkspace
            && string.Equals(descriptor.RetrySafety, "safe", StringComparison.OrdinalIgnoreCase);
        var retryLimit = safeReadRetry ? Math.Max(0, scope.WorkerRetryLimit) : 0;
        var timeoutSeconds = scope.DefaultTimeoutSeconds > 0 ? scope.DefaultTimeoutSeconds : Math.Max(1, _options.DefaultTimeoutSeconds);
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        ToolWireResponseDto? last = null;
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
            if (scope.SerializeWorkspaceWrites && execution.MutatesWorkspace)
            {
                var gate = WorkspaceLocks.GetOrAdd(scope.WorkspaceRoot, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    response = await _provider.CallAsync(scope.WorkspaceRoot, wire, timeout, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }
            else
            {
                response = await _provider.CallAsync(scope.WorkspaceRoot, wire, timeout, cancellationToken).ConfigureAwait(false);
            }

            if (response.IsSuccess)
            {
                var resultJson = response.Result is { } result ? result.GetRawText() : "null";
                var completed = await _executions.CompleteAsync(execution.Id, resultJson, cancellationToken).ConfigureAwait(false);
                await AppendEventAsync(execution.RunId, "tool.execution.completed", $"Tool '{descriptor.Id}' completed.", new
                {
                    execution_id = execution.Id,
                    task_id = execution.TaskId,
                    tool_id = descriptor.Id,
                    attempt
                }, cancellationToken, execution.TaskId, descriptor.Id).ConfigureAwait(false);
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
            var retry = safeReadRetry && (category is "timeout" or "process_exit") && attempt <= retryLimit;
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
            _logger.LogWarning("Retrying safe read-only tool {ToolId} after {Category} ({Attempt}/{Max})", descriptor.Id, category, attempt + 1, retryLimit + 1);
        }

        var finalCategory = WireErrorCategory(last?.Error);
        var message = SafeMessage(last?.Error);
        if (execution.MutatesWorkspace && finalCategory is "timeout" or "process_exit")
        {
            var unknown = await _executions.FailAsync(execution.Id, "outcome_unknown", finalCategory, message, cancellationToken).ConfigureAwait(false);
            await PauseForUnknownOutcomeAsync(unknown, message, cancellationToken).ConfigureAwait(false);
            return ResultForFailure(unknown, ToolDispatchStatus.OutcomeUnknown);
        }

        var failureStatus = finalCategory == "timeout" ? "timed_out" : "failed";
        var failedExecution = await _executions.FailAsync(execution.Id, failureStatus, finalCategory, message, cancellationToken).ConfigureAwait(false);
        return ResultForFailure(failedExecution, failureStatus == "timed_out" ? ToolDispatchStatus.Timeout : finalCategory == "process_exit" ? ToolDispatchStatus.ProcessExit : ToolDispatchStatus.Failed);
    }

    private async Task<(ToolManifestEntryDto? Entry, string? Error)> FindV2ToolAsync(ToolInvocationScope scope, string toolId, CancellationToken cancellationToken)
    {
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
            $"tool-auth:{execution.Id:N}"), cancellationToken).ConfigureAwait(false);
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
            "outcome_unknown" => ToolDispatchStatus.OutcomeUnknown,
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

    private static ToolDispatchResultDto Blocked(string message, ToolExecutionSnapshot? execution = null) => new()
    {
        Status = ToolDispatchStatus.Blocked,
        ExecutionId = execution?.Id.ToString(),
        ApprovalId = execution?.ApprovalId?.ToString(),
        PermissionRequestId = execution?.PermissionRequestId?.ToString(),
        AuthorizationDecisionId = execution?.AuthorizationDecisionId?.ToString(),
        Attempt = execution?.Attempt ?? 0,
        ErrorCategory = "blocked",
        Message = message
    };

    private static string WireErrorCategory(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "tool_error";
        var separator = error.IndexOf(':');
        var category = separator > 0 ? error[..separator].Trim().ToLowerInvariant() : error.Trim().ToLowerInvariant();
        return category is "timeout" or "process_exit" ? category : "tool_error";
    }

    private static string SafeMessage(string? value) => string.IsNullOrWhiteSpace(value) ? "Tool call failed." : value.Trim()[..Math.Min(value.Trim().Length, 4096)];
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
