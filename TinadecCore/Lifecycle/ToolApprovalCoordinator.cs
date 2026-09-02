using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Lifecycle;

/// <summary>
/// Durable authority for tool approvals and executions. It intentionally owns the
/// state machine in the relational store rather than holding a request open on an
/// in-memory waiter. Content writes may be left orphaned on a database rollback,
/// but no executable approval/execution relationship is committed partially.
/// </summary>
public sealed class ToolApprovalCoordinator : IToolApprovalCoordinator, IToolExecutionCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<LifecycleDbContext> _factory;
    private readonly ITenantContextAccessor _tenant;
    private readonly IContentStore _content;
    private readonly ISessionLocator _sessions;
    private readonly INonceMaterialStore _nonceMaterials;
    private readonly ILifecycleManager? _lifecycle;
    private readonly IToolApprovalAutoPolicy? _autoPolicy;
    private readonly TimeSpan _decisionWindow;
    private readonly TimeSpan _executionWindow;

    public ToolApprovalCoordinator(
        IDbContextFactory<LifecycleDbContext> factory,
        ITenantContextAccessor tenant,
        IContentStore content,
        ISessionLocator sessions,
        INonceMaterialStore? nonceMaterials = null,
        ILifecycleManager? lifecycle = null,
        IOptions<TinadecApprovalOptions>? approvalOptions = null,
        IToolApprovalAutoPolicy? autoPolicy = null)
    {
        _factory = factory;
        _tenant = tenant;
        _content = content;
        _sessions = sessions;
        _nonceMaterials = nonceMaterials ?? new InMemoryNonceMaterialStore();
        _lifecycle = lifecycle;
        _autoPolicy = autoPolicy;
        var options = approvalOptions?.Value ?? new TinadecApprovalOptions();
        _decisionWindow = TimeSpan.FromMinutes(Math.Clamp(options.DecisionWindowMinutes, 1, 1440));
        _executionWindow = TimeSpan.FromMinutes(Math.Clamp(options.ExecutionWindowMinutes, 1, 1440));
    }

    public async Task<ToolExecutionPreparation> PrepareAsync(
        ToolExecutionPrepareRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidatePrepareRequest(request);
        var scope = _tenant.Current;
        if (request.TenantId != scope.TenantId || request.WorkspaceId != scope.WorkspaceId)
        {
            throw new UnauthorizedAccessException("Tool execution scope does not belong to the current tenant/workspace.");
        }

        var needsApproval = request.RequiresApproval || request.MutatesWorkspace;
        var existing = await FindByToolCallKeyAsync(request, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            AssertSamePreparation(existing, request, needsApproval);
            return new ToolExecutionPreparation(await ToSnapshotAsync(existing, cancellationToken).ConfigureAwait(false), Existing: true);
        }

        var session = await _sessions.FindAsync(request.SessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Session was not found.");
        if (session.ProjectId != request.ProjectId || session.TenantId != request.TenantId || session.WorkspaceId != request.WorkspaceId)
        {
            throw new UnauthorizedAccessException("Tool execution session/project scope is inconsistent.");
        }

        var bytes = Encoding.UTF8.GetBytes(request.ParametersJson);
        var parameters = await _content.PutAsync(new ContentWriteRequest(
            request.TenantId, request.WorkspaceId, "tool_parameters", "application/json", new MemoryStream(bytes)), cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var executionId = Guid.NewGuid();
        var approvalId = needsApproval && !request.DeferApproval ? Guid.NewGuid() : (Guid?)null;
        var execution = new ToolExecutionRecord
        {
            Id = executionId,
            TenantId = request.TenantId,
            WorkspaceId = request.WorkspaceId,
            ProjectId = request.ProjectId,
            SessionId = request.SessionId,
            RunId = request.RunId,
            TaskId = request.TaskId,
            AgentInstanceId = request.AgentInstanceId,
            ApprovalId = approvalId,
            ToolId = request.ToolId,
            ToolCallKey = request.ToolCallKey,
            Risk = NormalizeRisk(request.Risk),
            MutatesWorkspace = request.MutatesWorkspace,
            RequiresApproval = needsApproval,
            LeaseUses = request.LeaseUses,
            LaneKey = request.LaneKey,
            Status = needsApproval && !request.DeferApproval ? "awaiting_approval" : "requested",
            ParametersHash = request.ParametersHash,
            ParametersReference = parameters.Value,
            ParametersLength = parameters.Length,
            Attempt = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            db.ToolExecutions.Add(execution);
            if (approvalId is { } pendingApprovalId && !request.DeferApproval)
            {
                var approval = new ApprovalRequestRecord
                {
                    Id = pendingApprovalId,
                    TenantId = request.TenantId,
                    WorkspaceId = request.WorkspaceId,
                    ProjectId = request.ProjectId,
                    SessionId = request.SessionId,
                    RunId = request.RunId,
                    TaskId = request.TaskId,
                    AgentInstanceId = request.AgentInstanceId,
                    ExecutionId = executionId,
                    Kind = "tool",
                    ToolId = request.ToolId,
                    Risk = NormalizeRisk(request.Risk),
                    RequestHash = request.ParametersHash,
                    ParametersReference = parameters.Value,
                    Summary = Truncate(request.Summary, 4096),
                    Status = "pending",
                    ExpiresAt = now.Add(_decisionWindow),
                    RequestedByPrincipalId = scope.PrincipalId,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                await PrepareApprovalNonceAsync(approval, cancellationToken).ConfigureAwait(false);
                db.ApprovalRequests.Add(approval);
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ToolExecutionPreparation(ToSnapshot(execution, request.ParametersJson, null), Existing: false);
        }
        catch (DbUpdateException)
        {
            // The run/tool-call unique index is the final authority under
            // concurrent recovery hosts. Return the equivalent record, but do
            // not silently accept a key reused with changed tool parameters.
            existing = await FindByToolCallKeyAsync(request, cancellationToken).ConfigureAwait(false);
            if (existing is null) throw;
            AssertSamePreparation(existing, request, needsApproval);
            return new ToolExecutionPreparation(await ToSnapshotAsync(existing, cancellationToken).ConfigureAwait(false), Existing: true);
        }
    }

    public async Task<ToolExecutionSnapshot?> FindAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ToolExecutions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == executionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
            cancellationToken).ConfigureAwait(false);
        return row is null ? null : await ToSnapshotAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ToolExecutionSnapshot> BindAuthorizationAsync(
        Guid executionId,
        Guid? permissionRequestId,
        Guid? authorizationDecisionId,
        Guid? capabilityLeaseId,
        string status,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ToolExecutions.SingleOrDefaultAsync(x => x.Id == executionId
            && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Tool execution was not found.");
        if (row.Status is "completed" or "failed" or "timed_out" or "cancelled")
            return await ToSnapshotAsync(row, cancellationToken).ConfigureAwait(false);
        row.AuthorizationDecisionId = authorizationDecisionId;
        row.PermissionRequestId = permissionRequestId;
        row.CapabilityLeaseId = capabilityLeaseId;
        row.Status = status;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ToSnapshotAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ToolExecutionSnapshot> BindWorkspaceSnapshotAsync(
        Guid executionId,
        Guid snapshotId,
        string snapshotHash,
        CancellationToken cancellationToken = default)
    {
        if (snapshotId == Guid.Empty || string.IsNullOrWhiteSpace(snapshotHash))
            throw new ArgumentException("A workspace snapshot id and hash are required.");
        var scope = _tenant.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ToolExecutions.SingleOrDefaultAsync(x => x.Id == executionId
            && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Tool execution was not found.");
        if (row.WorkspaceSnapshotId is { } existingId)
        {
            if (existingId != snapshotId || !string.Equals(row.WorkspaceSnapshotHash, snapshotHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Tool execution is already bound to a different workspace snapshot.");
            return await ToSnapshotAsync(row, cancellationToken).ConfigureAwait(false);
        }
        if (row.Status is "completed" or "failed" or "timed_out" or "cancelled")
            return await ToSnapshotAsync(row, cancellationToken).ConfigureAwait(false);
        row.WorkspaceSnapshotId = snapshotId;
        row.WorkspaceSnapshotHash = snapshotHash.Trim().ToLowerInvariant();
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ToSnapshotAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ToolExecutionSnapshot> EnsureApprovalAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ToolExecutions.SingleOrDefaultAsync(x => x.Id == executionId
            && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Tool execution was not found.");
        if (!row.RequiresApproval || row.ApprovalId is not null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return await ToSnapshotAsync(row, cancellationToken).ConfigureAwait(false);
        }
        row.ApprovalId = Guid.NewGuid();
        row.Status = "awaiting_approval";
        row.UpdatedAt = DateTimeOffset.UtcNow;
        var approval = new ApprovalRequestRecord
        {
            Id = row.ApprovalId.Value,
            TenantId = row.TenantId,
            WorkspaceId = row.WorkspaceId,
            ProjectId = row.ProjectId,
            SessionId = row.SessionId,
            RunId = row.RunId,
            TaskId = row.TaskId,
            AgentInstanceId = row.AgentInstanceId,
            ExecutionId = row.Id,
            Kind = "tool",
            ToolId = row.ToolId,
            Risk = row.Risk,
            RequestHash = row.ParametersHash,
            ParametersReference = row.ParametersReference,
            Summary = $"Tool '{row.ToolId}' requested by agent {row.AgentInstanceId}.",
            Status = "pending",
            ExpiresAt = row.UpdatedAt.Add(_decisionWindow),
            RequestedByPrincipalId = scope.PrincipalId,
            CreatedAt = row.UpdatedAt,
            UpdatedAt = row.UpdatedAt
        };
        await PrepareApprovalNonceAsync(approval, cancellationToken).ConfigureAwait(false);
        db.ApprovalRequests.Add(approval);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await ToSnapshotAsync(row, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The unique execution_id index makes approval creation a durable
            // compare-and-set under concurrent resume/recovery hosts. Re-read
            // the winner rather than surfacing a duplicate approval error.
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await using var retryDb = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var winner = await retryDb.ToolExecutions.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == executionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
                cancellationToken).ConfigureAwait(false);
            if (winner is null) throw;
            return await ToSnapshotAsync(winner, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Converts this execution's pending approval into an approved one when an
    /// unattended release path covers it. Three paths may release a call, in
    /// order: a frozen full-access run, a pre-authorization grant, and the
    /// auto-approve policy. A grant spends its budget through a CAS on
    /// use_count; wildcard scopes are refused here even if one was ever stored.
    /// When no path covers the call the approval simply stays pending — this
    /// method never denies, so a human can still decide the call afterwards.
    /// </summary>
    public async Task<PreAuthorizationMintResult?> TryMintPreAuthorizedApprovalAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        var now = DateTimeOffset.UtcNow;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var execution = await db.ToolExecutions.SingleOrDefaultAsync(x => x.Id == executionId
            && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (execution is null || !execution.RequiresApproval || execution.ApprovalId is not { } approvalId) return null;
        var approval = await db.ApprovalRequests.SingleOrDefaultAsync(x => x.Id == approvalId
            && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (approval is null || approval.Status != "pending") return null;
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == execution.RunId, cancellationToken).ConfigureAwait(false);
        if (run is null) return null;

        var frozenMode = await ReadFrozenPermissionModeAsync(execution.RunId, cancellationToken).ConfigureAwait(false);
        PreAuthorizationRecord? spent = null;
        string? autoPolicyVersion = null;
        if (string.Equals(frozenMode, "full-access", StringComparison.Ordinal))
        {
            // The frozen manifest and per-instance grants are still enforced by
            // ToolInvocationScopeResolver; the mint only removes the human wait.
        }
        else
        {
            // SQLite cannot translate DateTimeOffset relational comparisons, so
            // expiry stays out of the SQL predicates: candidates are loaded with
            // the translatable CAS state and filtered in memory, mirroring
            // TryConsumeApprovalAsync. The lane dimension is decided in memory
            // too — SQL equality against a nullable CLR argument would silently
            // evaluate to unknown once the execution carries no lane.
            var candidates = await db.PreAuthorizations
                .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                    && x.RunId == execution.RunId && !x.Revoked
                    && x.UseCount < x.MaxUses)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            // A run-level grant (LaneKey null) covers every lane of the run —
            // before the run starts the user only knows its id, so a main-lane
            // grant is the only shape that can authorize the deferred lane. A
            // lane-bound grant matches its own lane exactly and is preferred, so
            // a scoped grant is never shadowed by a broader one. Within each
            // group the oldest grant is spent first; SQLite cannot order by
            // DateTimeOffset, so that ordering happens client-side.
            var matched = candidates
                .Where(x => x.LaneKey == null || string.Equals(x.LaneKey, execution.LaneKey, StringComparison.Ordinal))
                .OrderBy(x => x.LaneKey == null ? 1 : 0)
                .ThenBy(x => x.CreatedAt)
                .FirstOrDefault(x => x.ExpiresAt > now && MatchesGrant(x, execution));
            if (matched is not null)
            {
                var cas = await db.PreAuthorizations
                    .Where(x => x.Id == matched.Id && !x.Revoked && x.UseCount < x.MaxUses)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(x => x.UseCount, x => x.UseCount + 1)
                        .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
                if (cas == 1)
                {
                    var spentRow = await db.PreAuthorizations.AsNoTracking().SingleAsync(x => x.Id == matched.Id, cancellationToken).ConfigureAwait(false);
                    if (spentRow.ExpiresAt <= now)
                    {
                        // The grant lapsed between load and spend; give the
                        // budget back rather than minting from an expired grant.
                        await db.PreAuthorizations
                            .Where(x => x.Id == matched.Id && x.UseCount == matched.UseCount + 1)
                            .ExecuteUpdateAsync(set => set
                                .SetProperty(x => x.UseCount, x => x.UseCount - 1)
                                .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        spent = spentRow;
                    }
                }
            }

            // No grant covered the call, so ask the auto-approve policy — the
            // third release path. A missing port, an abstaining policy, or an
            // exhausted budget all leave the approval pending rather than
            // denying it: a denial is terminal and a human could never grant
            // the call afterwards. The budget counts this store's own durable
            // auto-policy decisions for the run, so each release path spends
            // the budget it can actually see.
            if (spent is null)
            {
                var autoPolicyUses = await db.ApprovalDecisions.AsNoTracking()
                    .Where(decision => decision.Reason == "auto_policy_approved"
                        && db.ApprovalRequests.Any(approval => approval.Id == decision.ApprovalRequestId
                            && approval.RunId == execution.RunId))
                    .CountAsync(cancellationToken).ConfigureAwait(false);
                var verdict = _autoPolicy is null
                    ? null
                    : await _autoPolicy.EvaluateAsync(new ToolApprovalAutoPolicyContext(
                        execution.ToolId,
                        execution.Risk,
                        execution.RunId,
                        execution.LaneKey,
                        execution.ParametersHash,
                        execution.Id,
                        autoPolicyUses), cancellationToken).ConfigureAwait(false);
                if (verdict is null || !verdict.Approved) return null;
                autoPolicyVersion = verdict.PolicyVersion;
            }
        }

        var source = spent is not null ? "pre_authorized"
            : autoPolicyVersion is not null ? "auto_policy"
            : "full_access_auto_mint";
        var reason = source == "auto_policy" ? "auto_policy_approved" : source;
        // The approval can never outlive the grant that backed it.
        var expiry = spent is { } grant && grant.ExpiresAt < now.Add(_decisionWindow) ? grant.ExpiresAt : now.Add(_decisionWindow);
        var upgraded = await db.ApprovalRequests
            .Where(x => x.Id == approvalId && x.Status == "pending")
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.Status, "approved")
                .SetProperty(x => x.Decision, "approved")
                .SetProperty(x => x.DecisionReason, reason)
                .SetProperty(x => x.DecidedAt, now)
                .SetProperty(x => x.ExpiresAt, expiry)
                .SetProperty(x => x.ExecutionWindowExpiresAt, now.Add(_executionWindow))
                .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
        if (upgraded != 1) return null;

        db.ApprovalDecisions.Add(new ApprovalDecisionRecord
        {
            Id = Guid.NewGuid(),
            ApprovalRequestId = approvalId,
            Decision = "approved",
            Reason = reason,
            // A policy decision has no human behind it; leaving the column empty
            // keeps the audit trail from attributing it to the run initiator.
            DecidedByPrincipalId = spent?.GrantedByPrincipalId
                ?? (source == "auto_policy" ? Guid.Empty : run.InitiatedByPrincipalId),
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (source == "auto_policy" && _lifecycle is not null)
        {
            await _lifecycle.AppendEventAsync(execution.RunId, "approval.auto_decided",
                new
                {
                    execution_id = executionId,
                    approval_id = approvalId,
                    tool_id = execution.ToolId,
                    lane_key = execution.LaneKey,
                    policy_version = autoPolicyVersion,
                    risk = execution.Risk
                },
                $"Auto-approve policy {autoPolicyVersion} approved tool '{execution.ToolId}' without a human decision.",
                "info", taskId: execution.TaskId, approvalId: approvalId, toolId: execution.ToolId, cancellationToken).ConfigureAwait(false);
        }

        var fresh = await db.ToolExecutions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == executionId
            && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return new PreAuthorizationMintResult(await ToSnapshotAsync(fresh ?? execution, cancellationToken).ConfigureAwait(false), source);
    }

    private async Task<string?> ReadFrozenPermissionModeAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (_lifecycle is null) return null;
        try
        {
            var frozen = await _lifecycle.GetFrozenRunConfigurationAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(frozen?.Content)) return null;
            using var document = JsonDocument.Parse(frozen.Content);
            foreach (var name in new[] { "permissionMode", "permission_mode" })
            {
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString()?.Trim().ToLowerInvariant();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool MatchesGrant(PreAuthorizationRecord grant, ToolExecutionRecord execution)
    {
        List<string> tools;
        try
        {
            tools = JsonSerializer.Deserialize<List<string>>(grant.ToolScopeJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return false;
        }
        if (tools.Count == 0 || tools.Any(t => string.Equals(t, "*", StringComparison.Ordinal))) return false;
        if (!tools.Any(t => string.Equals(t, execution.ToolId, StringComparison.OrdinalIgnoreCase))) return false;
        if (!string.IsNullOrWhiteSpace(grant.ParameterConstraintHash)
            && !string.Equals(grant.ParameterConstraintHash, execution.ParametersHash, StringComparison.OrdinalIgnoreCase)) return false;
        return RiskRank(execution.Risk) <= RiskRank(grant.RiskMax);
    }

    private static int RiskRank(string? risk) => risk?.Trim().ToLowerInvariant() switch
    {
        "low" => 0,
        "medium" => 1,
        "high" => 2,
        "elevated" => 3,
        "critical" => 4,
        _ => 5
    };

    public async Task<ToolExecutionStartDecision> TryStartAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var execution = await db.ToolExecutions.SingleOrDefaultAsync(x =>
            x.Id == executionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
            cancellationToken).ConfigureAwait(false);
        if (execution is null) return new ToolExecutionStartDecision("not_found", null, "Tool execution was not found.");

        var snapshot = await ToSnapshotAsync(execution, cancellationToken).ConfigureAwait(false);
        if (IsExecutionTerminal(execution.Status)) return new ToolExecutionStartDecision(execution.Status, snapshot, "Tool execution is terminal.");
        if (execution.Status == "running") return new ToolExecutionStartDecision("already_running", snapshot, "Tool execution is already running.");
        if (execution.Status == "outcome_unknown") return new ToolExecutionStartDecision("outcome_unknown", snapshot, "Recovery decision is required before this execution can continue.");

        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == execution.RunId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
            cancellationToken).ConfigureAwait(false);
        if (run is null) return new ToolExecutionStartDecision("not_found", snapshot, "Run was not found.");
        if (run.Status == "cancelled")
        {
            await CancelExecutionAsync(db, execution, cancellationToken).ConfigureAwait(false);
            return new ToolExecutionStartDecision("cancelled", await ToSnapshotAsync(execution, cancellationToken).ConfigureAwait(false), "Run was cancelled.");
        }
        if (run.Status == "paused") return new ToolExecutionStartDecision("awaiting_resume", snapshot, "Run is paused.");
        if (run.Status is "completed" or "failed") return new ToolExecutionStartDecision("cancelled", snapshot, "Run is terminal.");

        if (!execution.RequiresApproval)
        {
            if (execution.Status != "requested") return new ToolExecutionStartDecision(execution.Status, snapshot, "Tool execution is not ready.");
            execution.Status = "running";
            execution.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ToolExecutionStartDecision("running", await ToSnapshotAsync(execution, cancellationToken).ConfigureAwait(false));
        }

        if (execution.ApprovalId is not { } approvalId)
        {
            execution.Status = "failed";
            execution.ErrorCategory = "approval_missing";
            execution.SafeErrorMessage = "Approval-gated execution has no approval record.";
            execution.UpdatedAt = DateTimeOffset.UtcNow;
            execution.CompletedAt = execution.UpdatedAt;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ToolExecutionStartDecision("not_approved", await ToSnapshotAsync(execution, cancellationToken).ConfigureAwait(false), execution.SafeErrorMessage);
        }

        var approval = await db.ApprovalRequests.SingleOrDefaultAsync(x => x.Id == approvalId
            && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (approval is null)
        {
            execution.Status = "failed";
            execution.ErrorCategory = "approval_missing";
            execution.SafeErrorMessage = "Approval record was not found.";
            execution.UpdatedAt = DateTimeOffset.UtcNow;
            execution.CompletedAt = execution.UpdatedAt;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ToolExecutionStartDecision("not_approved", await ToSnapshotAsync(execution, cancellationToken).ConfigureAwait(false), execution.SafeErrorMessage);
        }

        var now = DateTimeOffset.UtcNow;
        if (approval.Status == "pending" && approval.ExpiresAt <= now)
        {
            // Park, don't fail: a lapsed decision window means the human never
            // answered, not that the tool call was wrong. The execution keeps
            // waiting and the lane escalates when the engine consumes
            // approval.park_expired.
            approval.Status = "expired";
            approval.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (_lifecycle is not null)
            {
                await _lifecycle.AppendEventAsync(execution.RunId, "approval.park_expired",
                    new { execution_id = execution.Id, approval_id = approval.Id, tool_id = execution.ToolId, lane_key = execution.LaneKey },
                    "Approval decision window elapsed; execution parked awaiting escalation or re-approval.",
                    "warning", taskId: execution.TaskId, approvalId: approval.Id, toolId: execution.ToolId, cancellationToken).ConfigureAwait(false);
            }
            return new ToolExecutionStartDecision("awaiting_approval", snapshot, "Decision window elapsed; execution parked awaiting escalation or re-approval.", ParkExpired: true);
        }
        if (approval.Status == "approved")
        {
            // Legacy rows predating the split windows keep their old hard-fail
            // semantics through the decision-window fallback.
            var executionWindow = approval.ExecutionWindowExpiresAt ?? approval.ExpiresAt;
            if (executionWindow <= now)
            {
                approval.Status = "expired";
                approval.UpdatedAt = now;
                execution.Status = "failed";
                execution.ErrorCategory = "approval_expired";
                execution.SafeErrorMessage = "Approval expired before execution began.";
                execution.UpdatedAt = now;
                execution.CompletedAt = execution.UpdatedAt;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new ToolExecutionStartDecision("not_approved", await ToSnapshotAsync(execution, cancellationToken).ConfigureAwait(false), execution.SafeErrorMessage);
            }
        }
        if (approval.Kind != "tool"
            || approval.ExecutionId != execution.Id
            || approval.RunId != execution.RunId
            || approval.TaskId != execution.TaskId
            || approval.AgentInstanceId != execution.AgentInstanceId
            || !string.Equals(approval.ToolId, execution.ToolId, StringComparison.Ordinal)
            || !string.Equals(approval.RequestHash, execution.ParametersHash, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(approval.NonceHash)
            || string.IsNullOrWhiteSpace(approval.NonceSecretReference))
        {
            execution.Status = "failed";
            execution.ErrorCategory = "approval_binding_mismatch";
            execution.SafeErrorMessage = "Approval does not match the persisted tool execution.";
            execution.UpdatedAt = now;
            execution.CompletedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ToolExecutionStartDecision("not_approved", await ToSnapshotAsync(execution, cancellationToken).ConfigureAwait(false), execution.SafeErrorMessage);
        }
        if (approval.Status == "pending") return new ToolExecutionStartDecision("awaiting_approval", snapshot, "Approval is pending.");
        if (approval.Status is "rejected" or "expired" or "cancelled")
        {
            execution.Status = "failed";
            execution.ErrorCategory = "not_approved";
            execution.SafeErrorMessage = $"Approval decision was '{approval.Status}'.";
            execution.UpdatedAt = now;
            execution.CompletedAt = execution.UpdatedAt;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ToolExecutionStartDecision("not_approved", await ToSnapshotAsync(execution, cancellationToken).ConfigureAwait(false), execution.SafeErrorMessage);
        }
        if (approval.Status == "consumed")
        {
            // A process may have died after consumption but before a result was
            // persisted. Never replay a potentially mutating call automatically.
            execution.Status = "outcome_unknown";
            execution.ErrorCategory = "approval_consumed_without_outcome";
            execution.SafeErrorMessage = "Approval was consumed without a durable tool outcome.";
            execution.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ToolExecutionStartDecision("outcome_unknown", await ToSnapshotAsync(execution, cancellationToken).ConfigureAwait(false), execution.SafeErrorMessage);
        }
        if (approval.Status != "approved"
            || !string.Equals(approval.Decision, "approved", StringComparison.OrdinalIgnoreCase))
            return new ToolExecutionStartDecision("not_approved", snapshot, "Approval is not executable.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var consumed = await db.ApprovalRequests
            .Where(x => x.Id == approvalId
                && x.TenantId == scope.TenantId
                && x.WorkspaceId == scope.WorkspaceId
                && x.Kind == "tool"
                && x.ExecutionId == execution.Id
                && x.RunId == execution.RunId
                && x.TaskId == execution.TaskId
                && x.AgentInstanceId == execution.AgentInstanceId
                && x.ToolId == execution.ToolId
                && x.Status == "approved"
                && x.Decision == "approved"
                && x.ConsumedByExecutionId == null
                && x.RequestHash == execution.ParametersHash
                && !string.IsNullOrWhiteSpace(x.NonceHash))
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.Status, "consumed")
                .SetProperty(x => x.ConsumedByExecutionId, executionId)
                .SetProperty(x => x.ConsumedAt, now)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
        if (consumed != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new ToolExecutionStartDecision("not_approved", snapshot, "Approval could not be consumed.");
        }
        // ExecuteUpdate bypasses EF's tracked entity. Keep the in-memory row in
        // sync so the later execution save cannot restore its former status.
        approval.Status = "consumed";
        approval.ConsumedByExecutionId = executionId;
        approval.ConsumedAt = now;
        approval.UpdatedAt = now;
        execution.Status = "running";
        execution.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ToolExecutionStartDecision("running", await ToSnapshotAsync(execution, cancellationToken).ConfigureAwait(false));
    }

    public async Task<ToolExecutionSnapshot> CompleteAsync(Guid executionId, string resultJson, CancellationToken cancellationToken = default)
    {
        var row = await GetExecutionForWriteAsync(executionId, cancellationToken).ConfigureAwait(false);
        if (row.Status != "running") return await ToSnapshotAsync(row, cancellationToken).ConfigureAwait(false);
        var stored = await StoreResultAsync(row, resultJson, cancellationToken).ConfigureAwait(false);
        row.Status = "completed";
        row.ResultReference = stored.Value;
        row.ResultHash = stored.Sha256;
        row.ResultLength = stored.Length;
        row.ErrorCategory = null;
        row.SafeErrorMessage = null;
        row.CompletedAt = row.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveExecutionAsync(row, cancellationToken).ConfigureAwait(false);
        return await ToSnapshotAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ToolExecutionSnapshot> FailAsync(
        Guid executionId,
        string status,
        string errorCategory,
        string safeMessage,
        CancellationToken cancellationToken = default)
    {
        if (status is not ("failed" or "timed_out" or "outcome_unknown" or "cancelled"))
            throw new ArgumentException("Unsupported tool execution terminal status.", nameof(status));
        var row = await GetExecutionForWriteAsync(executionId, cancellationToken).ConfigureAwait(false);
        if (IsExecutionTerminal(row.Status)) return await ToSnapshotAsync(row, cancellationToken).ConfigureAwait(false);
        var message = Truncate(safeMessage, 4096);
        var body = JsonSerializer.Serialize(new { error_category = errorCategory, message }, JsonOptions);
        var stored = await StoreResultAsync(row, body, cancellationToken).ConfigureAwait(false);
        row.Status = status;
        row.ResultReference = stored.Value;
        row.ResultHash = stored.Sha256;
        row.ResultLength = stored.Length;
        row.ErrorCategory = Truncate(errorCategory, 128);
        row.SafeErrorMessage = message;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.CompletedAt = status == "outcome_unknown" ? null : row.UpdatedAt;
        await SaveExecutionAsync(row, cancellationToken).ConfigureAwait(false);
        return await ToSnapshotAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ToolExecutionRecoveryResult> ApplyRecoveryDecisionAsync(Guid executionId, string decision, CancellationToken cancellationToken = default)
    {
        var normalized = decision.Trim().ToLowerInvariant();
        if (normalized is not ("retry" or "fail")) throw new ArgumentException("decision must be retry or fail", nameof(decision));
        var scope = _tenant.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var source = await db.ToolExecutions.SingleOrDefaultAsync(x => x.Id == executionId
            && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (source is null) return new ToolExecutionRecoveryResult("not_found", null, null, null, "Tool execution was not found.");
        if (source.Status != "outcome_unknown") return new ToolExecutionRecoveryResult("not_recoverable", await ToSnapshotAsync(source, cancellationToken).ConfigureAwait(false), null, null, "Only outcome_unknown executions require a recovery decision.");

        if (normalized == "fail")
        {
            source.Status = "failed";
            source.ErrorCategory = "recovery_failed";
            source.SafeErrorMessage = "A human selected fail after the tool outcome became unknown.";
            source.UpdatedAt = DateTimeOffset.UtcNow;
            source.CompletedAt = source.UpdatedAt;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ToolExecutionRecoveryResult("failed", await ToSnapshotAsync(source, cancellationToken).ConfigureAwait(false), null, null);
        }

        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == source.RunId
            && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (run is null || run.Status is "cancelled" or "completed" or "failed")
            return new ToolExecutionRecoveryResult("run_not_active", await ToSnapshotAsync(source, cancellationToken).ConfigureAwait(false), null, null, "The run is not active.");

        var now = DateTimeOffset.UtcNow;
        var nextId = Guid.NewGuid();
        var needsApproval = source.RequiresApproval || source.MutatesWorkspace;
        var approvalId = needsApproval ? Guid.NewGuid() : (Guid?)null;
        var replacement = new ToolExecutionRecord
        {
            Id = nextId,
            TenantId = source.TenantId,
            WorkspaceId = source.WorkspaceId,
            ProjectId = source.ProjectId,
            SessionId = source.SessionId,
            RunId = source.RunId,
            TaskId = source.TaskId,
            AgentInstanceId = source.AgentInstanceId,
            ApprovalId = approvalId,
            ToolId = source.ToolId,
            ToolCallKey = CreateRecoveryToolCallKey(source.ToolCallKey, source.Attempt + 1),
            Risk = source.Risk,
            MutatesWorkspace = source.MutatesWorkspace,
            RequiresApproval = needsApproval,
            LeaseUses = source.LeaseUses,
            Status = needsApproval ? "awaiting_approval" : "requested",
            ParametersHash = source.ParametersHash,
            ParametersReference = source.ParametersReference,
            ParametersLength = source.ParametersLength,
            WorkspaceSnapshotId = source.WorkspaceSnapshotId,
            WorkspaceSnapshotHash = source.WorkspaceSnapshotHash,
            Attempt = source.Attempt + 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.ToolExecutions.Add(replacement);
        if (approvalId is { } nextApprovalId)
        {
            var approval = new ApprovalRequestRecord
            {
                Id = nextApprovalId,
                TenantId = source.TenantId,
                WorkspaceId = source.WorkspaceId,
                ProjectId = source.ProjectId,
                SessionId = source.SessionId,
                RunId = source.RunId,
                TaskId = source.TaskId,
                AgentInstanceId = source.AgentInstanceId,
                ExecutionId = nextId,
                Kind = "tool",
                ToolId = source.ToolId,
                Risk = source.Risk,
                RequestHash = source.ParametersHash,
                ParametersReference = source.ParametersReference,
                Summary = $"Retry for tool '{source.ToolId}' after unknown outcome from execution {source.Id}.",
                Status = "pending",
                ExpiresAt = now.Add(_decisionWindow),
                RequestedByPrincipalId = scope.PrincipalId,
                CreatedAt = now,
                UpdatedAt = now
            };
            await PrepareApprovalNonceAsync(approval, cancellationToken).ConfigureAwait(false);
            db.ApprovalRequests.Add(approval);
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ToolExecutionRecoveryResult(
            needsApproval ? "awaiting_approval" : "requested",
            await ToSnapshotAsync(source, cancellationToken).ConfigureAwait(false),
            await ToSnapshotAsync(replacement, cancellationToken).ConfigureAwait(false),
            approvalId);
    }

    public async Task CancelPendingForRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        var now = DateTimeOffset.UtcNow;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var approvals = await db.ApprovalRequests.Where(x => x.RunId == runId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
            && (x.Status == "pending" || x.Status == "approved")).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var approval in approvals)
        {
            approval.Status = "cancelled";
            approval.Decision = "cancelled";
            approval.DecidedAt = now;
            approval.UpdatedAt = now;
        }
        var executions = await db.ToolExecutions.Where(x => x.RunId == runId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
            && (x.Status == "requested" || x.Status == "awaiting_approval")).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var execution in executions)
        {
            execution.Status = "cancelled";
            execution.ErrorCategory = "run_cancelled";
            execution.SafeErrorMessage = "Run was cancelled before tool execution began.";
            execution.CompletedAt = execution.UpdatedAt = now;
        }
        if (approvals.Count != 0 || executions.Count != 0) await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> CreateToolApprovalAsync(
        string sessionId,
        string runId,
        string taskId,
        string toolId,
        string parametersHash,
        string summary,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        var now = DateTimeOffset.UtcNow;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = new ApprovalRequestRecord
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            SessionId = Guid.TryParse(sessionId, out var parsedSession) ? parsedSession : null,
            RunId = Guid.TryParse(runId, out var parsedRun) ? parsedRun : null,
            TaskId = Guid.TryParse(taskId, out var parsedTask) ? parsedTask : null,
            Kind = "tool",
            ToolId = toolId,
            Risk = "elevated",
            RequestHash = parametersHash,
            ParametersReference = string.Empty,
            Summary = Truncate(summary, 4096),
            Status = "pending",
            ExpiresAt = now.Add(_decisionWindow),
            RequestedByPrincipalId = scope.PrincipalId,
            CreatedAt = now,
            UpdatedAt = now
        };
        await PrepareApprovalNonceAsync(row, cancellationToken).ConfigureAwait(false);
        db.ApprovalRequests.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row.Id;
    }

    public async Task<bool> TryConsumeApprovalAsync(Guid approvalId, string executionId, string expectedRequestHash, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(executionId, out var executionGuid)) return false;
        if (string.IsNullOrWhiteSpace(expectedRequestHash)) return false;
        expectedRequestHash = expectedRequestHash.Trim().ToLowerInvariant();
        var scope = _tenant.Current;
        var now = DateTimeOffset.UtcNow;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // SQLite cannot translate DateTimeOffset relational comparisons. Scope,
        // state, and request hash remain in the database CAS; expiry is checked
        // after loading that one candidate. The subsequent status transition is
        // still atomic across competing hosts.
        var candidate = await db.ApprovalRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == approvalId
            && x.TenantId == scope.TenantId
            && x.WorkspaceId == scope.WorkspaceId
            && x.Status == "approved"
            && x.Decision == "approved"
            && x.ConsumedByExecutionId == null
            && x.RequestHash == expectedRequestHash
            && (x.ExecutionId == null || x.ExecutionId == executionGuid)
            && !string.IsNullOrWhiteSpace(x.NonceHash), cancellationToken).ConfigureAwait(false);
        if (candidate is null) return false;

        var protectedNonce = await _nonceMaterials.GetAsync(candidate.NonceSecretReference ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(protectedNonce) || !FixedEquals(candidate.NonceHash, HashNonce(protectedNonce)))
        {
            await db.ApprovalRequests.Where(x => x.Id == approvalId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status == "approved")
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "cancelled")
                    .SetProperty(x => x.Decision, "rejected").SetProperty(x => x.DecisionReason, "approval_nonce_unavailable")
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (candidate.ExpiresAt <= now)
        {
            await db.ApprovalRequests.Where(x => x.Id == approvalId
                && x.TenantId == scope.TenantId
                && x.WorkspaceId == scope.WorkspaceId
                && x.Status == "approved"
                && x.Decision == "approved"
                && x.ConsumedByExecutionId == null
                && x.RequestHash == expectedRequestHash
                && (x.ExecutionId == null || x.ExecutionId == executionGuid)
                && !string.IsNullOrWhiteSpace(x.NonceHash))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(x => x.Status, "expired")
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
            return false;
        }

        var updated = await db.ApprovalRequests.Where(x => x.Id == approvalId
            && x.TenantId == scope.TenantId
            && x.WorkspaceId == scope.WorkspaceId
            && x.Status == "approved"
            && x.Decision == "approved"
            && x.ConsumedByExecutionId == null
            && x.RequestHash == expectedRequestHash
            && (x.ExecutionId == null || x.ExecutionId == executionGuid)
            && !string.IsNullOrWhiteSpace(x.NonceHash))
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.Status, "consumed")
                .SetProperty(x => x.ConsumedByExecutionId, executionGuid)
                .SetProperty(x => x.ConsumedAt, now)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
        return updated == 1;
    }

    public async Task<ToolApprovalDecision> DecideAsync(Guid approvalId, string decision, string? reason, CancellationToken cancellationToken = default)
    {
        var normalized = decision.Trim().ToLowerInvariant();
        if (normalized is not ("approved" or "rejected")) throw new ArgumentException("decision must be approved or rejected", nameof(decision));
        var scope = _tenant.Current;
        var now = DateTimeOffset.UtcNow;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ApprovalRequests.SingleOrDefaultAsync(x => x.Id == approvalId
            && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Approval was not found.");
        if (row.ExpiresAt <= now && row.Status == "pending")
        {
            var expired = await db.ApprovalRequests.Where(x => x.Id == approvalId
                    && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                    && x.Status == "pending")
                .ExecuteUpdateAsync(set => set
                    .SetProperty(x => x.Status, "expired")
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
            if (expired == 1)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            else
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Approval is no longer actionable.");
        }
        if (row.Status != "pending")
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Approval is no longer actionable.");
        }
        var decisionReason = string.IsNullOrWhiteSpace(reason) ? null : Truncate(reason, 4096);
        var changed = await db.ApprovalRequests.Where(x => x.Id == approvalId
                && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                && x.Status == "pending")
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.Status, normalized)
                .SetProperty(x => x.Decision, normalized)
                .SetProperty(x => x.DecisionReason, decisionReason)
                .SetProperty(x => x.DecidedAt, now)
                // The execution window only starts on an approval: after it
                // lapses the approved call hard-fails instead of replaying.
                .SetProperty(x => x.ExecutionWindowExpiresAt, normalized == "approved" ? now.Add(_executionWindow) : (DateTimeOffset?)null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
        if (changed != 1)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("Approval is no longer actionable.");
        }
        db.ApprovalDecisions.Add(new ApprovalDecisionRecord
        {
            Id = Guid.NewGuid(), ApprovalRequestId = row.Id, Decision = normalized, Reason = decisionReason,
            DecidedByPrincipalId = scope.PrincipalId, CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ToolApprovalDecision(row.Id, normalized, row.RunId, row.TaskId, row.ExecutionId, now);
    }

    private async Task<ToolExecutionRecord> GetExecutionForWriteAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var scope = _tenant.Current;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.ToolExecutions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == executionId
            && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Tool execution was not found.");
    }

    private async Task<ToolExecutionRecord?> FindByToolCallKeyAsync(
        ToolExecutionPrepareRequest request,
        CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.ToolExecutions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == request.TenantId
            && x.WorkspaceId == request.WorkspaceId
            && x.RunId == request.RunId
            && x.ToolCallKey == request.ToolCallKey,
            cancellationToken).ConfigureAwait(false);
    }

    private static void AssertSamePreparation(
        ToolExecutionRecord existing,
        ToolExecutionPrepareRequest request,
        bool requiresApproval)
    {
        var matches = existing.ProjectId == request.ProjectId
            && existing.SessionId == request.SessionId
            && existing.TaskId == request.TaskId
            && existing.AgentInstanceId == request.AgentInstanceId
            && string.Equals(existing.ToolId, request.ToolId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.ToolCallKey, request.ToolCallKey, StringComparison.Ordinal)
            && string.Equals(existing.ParametersHash, request.ParametersHash, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.Risk, NormalizeRisk(request.Risk), StringComparison.OrdinalIgnoreCase)
            && existing.MutatesWorkspace == request.MutatesWorkspace
            && existing.RequiresApproval == requiresApproval;
        if (!matches)
        {
            throw new InvalidOperationException($"Tool call key '{request.ToolCallKey}' was reused with different execution data.");
        }
    }

    private async Task SaveExecutionAsync(ToolExecutionRecord detached, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.ToolExecutions.Update(detached);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ContentReference> StoreResultAsync(ToolExecutionRecord execution, string resultJson, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(resultJson);
        return await _content.PutAsync(new ContentWriteRequest(execution.TenantId, execution.WorkspaceId, "tool_results", "application/json", new MemoryStream(bytes)), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolExecutionSnapshot> ToSnapshotAsync(ToolExecutionRecord row, CancellationToken cancellationToken)
    {
        var parameters = await ReadContentAsync(row.ParametersReference, row.ParametersHash, row.ParametersLength, "{}", cancellationToken).ConfigureAwait(false);
        var result = row.ResultReference is null ? null : await ReadContentAsync(row.ResultReference, row.ResultHash ?? string.Empty, row.ResultLength ?? 0, null, cancellationToken).ConfigureAwait(false);
        return ToSnapshot(row, parameters ?? "{}", result);
    }

    private async Task<string?> ReadContentAsync(string? reference, string hash, long length, string? fallback, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference)) return fallback;
        try
        {
            await using var stream = await _content.OpenReadAsync(new ContentReference(reference, hash, length, "application/json"), cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return fallback;
        }
    }

    private static ToolExecutionSnapshot ToSnapshot(ToolExecutionRecord row, string parametersJson, string? resultJson) => new(
        row.Id, row.TenantId, row.WorkspaceId, row.ProjectId ?? Guid.Empty, row.SessionId, row.RunId, row.TaskId, row.AgentInstanceId,
        row.ApprovalId, row.ToolId, row.ToolCallKey, row.Risk, row.MutatesWorkspace, row.RequiresApproval, row.Status, parametersJson,
        row.ParametersHash, row.Attempt, resultJson, row.ErrorCategory, row.SafeErrorMessage, row.CreatedAt, row.UpdatedAt, row.CompletedAt,
        row.PermissionRequestId, row.AuthorizationDecisionId, row.CapabilityLeaseId, row.LeaseUses,
        row.WorkspaceSnapshotId, row.WorkspaceSnapshotHash, row.LaneKey);

    // Action approvals carry a server-generated, one-time nonce. Only its hash
    // and a protected-material reference are persisted; the material is never
    // returned through a DTO, event, log, or Gateway response.
    private async Task PrepareApprovalNonceAsync(ApprovalRequestRecord row, CancellationToken cancellationToken)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        row.Nonce = nonce;
        row.NonceHash = HashNonce(nonce);
        row.NonceSecretReference = $"approval_nonce_{row.TenantId:N}_{row.Id:N}";
        await _nonceMaterials.PutAsync(row.NonceSecretReference, nonce, cancellationToken).ConfigureAwait(false);
    }

    private static string HashNonce(string nonce) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce))).ToLowerInvariant();

    private static bool FixedEquals(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static bool IsExecutionTerminal(string status) => status is "completed" or "failed" or "timed_out" or "cancelled";

    private static void ValidatePrepareRequest(ToolExecutionPrepareRequest request)
    {
        if (request.TenantId == Guid.Empty || request.WorkspaceId == Guid.Empty || request.ProjectId == Guid.Empty
            || request.SessionId == Guid.Empty || request.RunId == Guid.Empty || request.TaskId == Guid.Empty || request.AgentInstanceId == Guid.Empty)
            throw new ArgumentException("Tool execution scope ids must be non-empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ToolId) || string.IsNullOrWhiteSpace(request.ParametersHash))
            throw new ArgumentException("Tool id and parameters hash are required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ToolCallKey) || request.ToolCallKey.Trim().Length > 512)
            throw new ArgumentException("tool_call_key is required and must not exceed 512 characters.", nameof(request));
        if (request.LeaseUses is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(request), "Lease use count must be between 1 and 32.");
    }

    private static string NormalizeRisk(string risk) => risk.Trim().ToLowerInvariant() switch
    {
        "low" or "medium" or "high" or "elevated" or "critical" => risk.Trim().ToLowerInvariant(),
        _ => "medium"
    };

    private static string Truncate(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed[..Math.Min(trimmed.Length, limit)];
    }

    private static string CreateRecoveryToolCallKey(string sourceKey, int attempt)
    {
        var suffix = $":recovery:{attempt}";
        var prefixLength = Math.Max(1, 512 - suffix.Length);
        var prefix = string.IsNullOrWhiteSpace(sourceKey) ? "tool" : sourceKey.Trim();
        return prefix[..Math.Min(prefix.Length, prefixLength)] + suffix;
    }

    private sealed class InMemoryNonceMaterialStore : INonceMaterialStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public Task<string> PutAsync(string reference, string value, CancellationToken cancellationToken = default)
        { _values[reference] = value; return Task.FromResult(reference); }
        public Task<string?> GetAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.TryGetValue(reference, out var value) ? value : null);
        public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
        { _values.Remove(reference); return Task.CompletedTask; }
    }

    private static async Task CancelExecutionAsync(LifecycleDbContext db, ToolExecutionRecord execution, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        execution.Status = "cancelled";
        execution.ErrorCategory = "run_cancelled";
        execution.SafeErrorMessage = "Run was cancelled before tool execution began.";
        execution.CompletedAt = execution.UpdatedAt = now;
        if (execution.ApprovalId is { } approvalId)
        {
            var approval = await db.ApprovalRequests.SingleOrDefaultAsync(x => x.Id == approvalId, cancellationToken).ConfigureAwait(false);
            if (approval is not null && approval.Status is "pending" or "approved")
            {
                approval.Status = "cancelled";
                approval.Decision = "cancelled";
                approval.DecidedAt = now;
                approval.UpdatedAt = now;
            }
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
