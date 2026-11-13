using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Lifecycle;
using TinadecCore.Persistence;
using TinadecCore.Tools;

namespace TinadecCore.Runtime;

/// <summary>
/// Implements the explicit user tool transport. It shares the governance PDP,
/// capability lease and lifecycle approval records with agent dispatch while
/// keeping the user action itself outside the run/task/agent graph.
/// </summary>
public sealed class UserToolActionService : IUserToolActionService, IUserToolActionRecovery
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<LifecycleDbContext> _dbFactory;
    private readonly ITenantContextAccessor _tenant;
    private readonly ISessionLocator _sessions;
    private readonly IToolProvider _provider;
    private readonly IAuthorizationService _authorization;
    private readonly IWorkspaceSnapshotService _snapshots;
    private readonly IContentStore _content;
    private readonly INonceMaterialStore _nonceMaterials;
    private readonly IToolConfigurationResolver? _toolSettings;
    private readonly IMcpResourceRegistry? _mcpResources;
    private readonly IToolSkillResourceService? _skillResources;
    private readonly IManagedMcpProgramService? _mcpPrograms;
    private readonly IToolExecutionContextLifecycle? _contextLifecycle;
    private readonly IScopeStorageLocations? _storageLocations;
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);

    public UserToolActionService(
        IDbContextFactory<LifecycleDbContext> dbFactory,
        ITenantContextAccessor tenant,
        ISessionLocator sessions,
        IToolProvider provider,
        IAuthorizationService authorization,
        IWorkspaceSnapshotService snapshots,
        IContentStore content,
        INonceMaterialStore nonceMaterials,
        IToolConfigurationResolver? toolSettings = null,
        IMcpResourceRegistry? mcpResources = null,
        IToolExecutionContextLifecycle? contextLifecycle = null,
        IToolSkillResourceService? skillResources = null,
        IManagedMcpProgramService? mcpPrograms = null,
        IScopeStorageLocations? storageLocations = null)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _sessions = sessions;
        _provider = provider;
        _authorization = authorization;
        _snapshots = snapshots;
        _content = content;
        _nonceMaterials = nonceMaterials;
        _toolSettings = toolSettings;
        _mcpResources = mcpResources;
        _skillResources = skillResources;
        _mcpPrograms = mcpPrograms;
        _contextLifecycle = contextLifecycle;
        _storageLocations = storageLocations;
    }

    public async Task<UserToolActionResult> CreateAsync(UserToolActionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ProjectId == Guid.Empty && request.ToolId is not ("skill_resource_update" or "mcp_program_update")) throw new ArgumentException("project_id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ToolId)) throw new ArgumentException("tool_id is required.", nameof(request));
        if (!TryNormalizeParameters(request.ParametersJson, out var parametersJson))
            throw new ArgumentException("params must be a JSON object or null.", nameof(request));

        var scope = _tenant.Current;
        var project = request.ProjectId == Guid.Empty ? null : await _sessions.FindProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Project was not found.");
        if (project is not null) EnsureProjectScope(project, scope);
        var trustedTool = await ReadTrustedToolAsync(request.ToolId.Trim(), project?.RootPath ?? "", cancellationToken).ConfigureAwait(false);
        var descriptor = trustedTool.Descriptor;
        EnsureParametersMatchDescriptor(parametersJson, descriptor);
        if (descriptor.Id == "mcp_resource_update") ValidateManagedMcpParameters(parametersJson, project!.ProjectId);
        if (descriptor.Id == "skill_resource_update") ValidateManagedSkillParameters(parametersJson);
        if (descriptor.Id == "skill_project_package") ValidateSkillProjectPackage(parametersJson);
        if (descriptor.Id == "mcp_program_update") ValidateMcpProgramParameters(parametersJson, request.ProjectId);
        var idempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey);
        var parametersHash = ToolParametersHash.Compute(parametersJson);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (idempotencyKey is not null)
        {
            var existing = await db.UserToolActions.SingleOrDefaultAsync(x =>
                x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.IdempotencyKey == idempotencyKey,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.ParametersHash, parametersHash, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.ToolId, descriptor.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("User tool action idempotency key was reused with different input.");
                return await ToResultAsync(existing, cancellationToken).ConfigureAwait(false);
            }
        }

        var stored = await PutContentAsync(scope, "user-tool-parameters", parametersJson, cancellationToken).ConfigureAwait(false);
        var frozenDescriptorJson = JsonSerializer.Serialize(ToFrozen(descriptor), JsonOptions);
        var frozenDescriptor = await PutContentAsync(scope, "user-tool-descriptor", frozenDescriptorJson, cancellationToken).ConfigureAwait(false);
        var toolContext = descriptor.Id is "mcp_resource_update" or "skill_resource_update" or "skill_project_package" or "mcp_program_update" || _toolSettings is null ? null : await _toolSettings.ResolveAsync(project!.ProjectId, null, cancellationToken).ConfigureAwait(false);
        if (toolContext is not null && !ToolSettingsSchema.IsEnabled(toolContext.Settings, descriptor.Id)) throw new UnauthorizedAccessException("This tool is disabled in shared settings.");
        if (toolContext is null && _storageLocations is { } locations)
            toolContext = new ToolExecutionContextDto { StorageId = locations.StorageId, StorageRoot = locations.Root,
                ProjectRoot = locations.ProjectRoot, Settings = JsonSerializer.SerializeToElement(new { }), SettingsHash = "scope-binding" };
        var frozenToolContext = toolContext is null ? null : await PutContentAsync(scope, "user-tool-configuration", JsonSerializer.Serialize(toolContext, JsonOptions), cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var actionId = Guid.NewGuid();
        var action = new UserToolActionRecord
        {
            Id = actionId, AuditReference = $"user-tool-action:{actionId:N}", TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            ProjectId = project?.ProjectId ?? Guid.Empty, PrincipalId = scope.PrincipalId, ToolId = descriptor.Id,
            ProviderProtocolVersion = trustedTool.ProtocolVersion, ProviderManifestHash = trustedTool.ManifestHash,
            ToolDescriptorReference = frozenDescriptor.Value, ToolDescriptorHash = frozenDescriptor.Sha256,
            ToolDescriptorLength = frozenDescriptor.Length,
            ToolConfigurationReference = frozenToolContext?.Value ?? string.Empty, ToolConfigurationHash = frozenToolContext?.Sha256 ?? string.Empty, ToolConfigurationLength = frozenToolContext?.Length ?? 0,
            ParametersReference = stored.Value, ParametersLength = stored.Length, ParametersHash = parametersHash,
            Risk = NormalizeRisk(descriptor.Risk), MutatesWorkspace = descriptor.MutatesWorkspace,
            RequiresApproval = descriptor.RequiresApproval || descriptor.MutatesWorkspace,
            NonReversible = IsInherentlyNonReversible(descriptor.Id),
            CompensationGuidance = CompensationGuidanceFor(descriptor.Id),
            Status = NeedsPrewriteSnapshot(descriptor) ? UserToolActionStatuses.SnapshotRequired : "requested",
            IdempotencyKey = idempotencyKey, CreatedAt = now, UpdatedAt = now
        };
        db.UserToolActions.Add(action);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (action.Status == UserToolActionStatuses.SnapshotRequired)
        {
            try
            {
                var snapshot = await _snapshots.CreateAsync(new WorkspaceSnapshotCreateRequest(
                    project!.ProjectId, $"user-action:{action.Id:N}:prewrite"), cancellationToken).ConfigureAwait(false);
                action.SnapshotId = snapshot.Id;
                action.SnapshotHash = snapshot.WorkspaceHash;
                action.Status = "requested";
                action.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                await BlockAsync(action, RunErrorTaxonomy.SnapshotFailed, SafeMessage(ex.Message), cancellationToken).ConfigureAwait(false);
                return await ToResultAsync(action, cancellationToken).ConfigureAwait(false);
            }
        }

        return await AuthorizeAndMaybeRunAsync(action, descriptor, project, cancellationToken).ConfigureAwait(false);
    }

    public Task<UserToolActionResult?> GetAsync(Guid actionId, CancellationToken cancellationToken = default) =>
        SqliteTransientRetry.ReadAsync(async token =>
        {
            var action = await FindAsync(actionId, token).ConfigureAwait(false);
            return action is null ? null : await ToResultAsync(action, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<IReadOnlyList<UserToolActionResult>> ListAsync(string? status = null, CancellationToken cancellationToken = default) =>
        SqliteTransientRetry.ReadAsync(async token =>
        {
            var scope = _tenant.Current;
            await using var db = await _dbFactory.CreateDbContextAsync(token).ConfigureAwait(false);
            var query = db.UserToolActions.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId);
            if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status.Trim().ToLowerInvariant());
            var rows = await query.ToArrayAsync(token).ConfigureAwait(false);
            Array.Sort(rows, static (left, right) => right.CreatedAt.CompareTo(left.CreatedAt));
            if (rows.Length > 200) rows = rows[..200];
            var results = new List<UserToolActionResult>(rows.Length);
            foreach (var row in rows) results.Add(await ToResultAsync(row, token).ConfigureAwait(false));
            return (IReadOnlyList<UserToolActionResult>)results;
        }, cancellationToken);

    public async Task<UserToolActionResult> ResumeAsync(Guid actionId, CancellationToken cancellationToken = default)
    {
        var action = await FindAsync(actionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("User tool action was not found.");
        if (IsTerminal(action.Status) || action.Status == UserToolActionStatuses.SnapshotRequired)
            return await ToResultAsync(action, cancellationToken).ConfigureAwait(false);
        var scope = _tenant.Current;
        var project = action.ToolId is "skill_resource_update" or "mcp_program_update" && action.ProjectId == Guid.Empty ? null : await _sessions.FindProjectAsync(action.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Project was not found.");
        if (project is not null) EnsureProjectScope(project, scope);
        var descriptorValidation = await ValidateFrozenToolAsync(action, project?.RootPath ?? "", cancellationToken).ConfigureAwait(false);
        if (descriptorValidation.Error is not null)
            return await BlockAsync(action, descriptorValidation.Error.Value.Code, descriptorValidation.Error.Value.Message, cancellationToken).ConfigureAwait(false);
        var descriptor = descriptorValidation.Descriptor!;

        if (action.PermissionRequestId is { } permissionId)
        {
            var resolution = await _authorization.GetPermissionRequestAsync(permissionId, cancellationToken).ConfigureAwait(false);
            if (resolution is null) return await BlockAsync(action, "permission_request_missing", "Permission request was not found.", cancellationToken).ConfigureAwait(false);
            action.AuthorizationDecisionId = resolution.Decision.Id;
            action.CapabilityLeaseId = resolution.Lease?.Id;
            if (resolution.Decision.Outcome != GovernanceOutcomes.Allowed)
            {
                action.Status = resolution.Request.Status switch
                {
                    PermissionRequestStatuses.AwaitingDelegate => UserToolActionStatuses.AwaitingDelegate,
                    PermissionRequestStatuses.AwaitingUser => UserToolActionStatuses.AwaitingUser,
                    _ => UserToolActionStatuses.Blocked
                };
                if (action.Status == UserToolActionStatuses.Blocked)
                { action.ErrorCategory = resolution.Decision.ReasonCode; action.SafeErrorMessage = resolution.Decision.Reason; action.CompletedAt = DateTimeOffset.UtcNow; }
                action.UpdatedAt = DateTimeOffset.UtcNow;
                await SaveAsync(action, cancellationToken).ConfigureAwait(false);
                return await ToResultAsync(action, cancellationToken).ConfigureAwait(false);
            }
            action.Status = action.RequiresApproval ? UserToolActionStatuses.AwaitingApproval : "authorized";
            action.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveAsync(action, cancellationToken).ConfigureAwait(false);
        }

        if (action.RequiresApproval)
        {
            if (action.ActionApprovalId is null)
            {
                action.ActionApprovalId = await CreateActionApprovalAsync(action, cancellationToken).ConfigureAwait(false);
                action.Status = UserToolActionStatuses.AwaitingApproval;
                action.UpdatedAt = DateTimeOffset.UtcNow;
                await SaveAsync(action, cancellationToken).ConfigureAwait(false);
                return await ToResultAsync(action, cancellationToken).ConfigureAwait(false);
            }
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var approval = await db.ApprovalRequests.SingleOrDefaultAsync(x => x.Id == action.ActionApprovalId
                && x.UserToolActionId == action.Id && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
            if (approval is null) return await BlockAsync(action, "approval_missing", "Action approval was not found.", cancellationToken).ConfigureAwait(false);
            if (approval.Status == "pending") { action.Status = UserToolActionStatuses.AwaitingApproval; await SaveAsync(action, cancellationToken).ConfigureAwait(false); return await ToResultAsync(action, cancellationToken).ConfigureAwait(false); }
            if (approval.Status == "consumed")
            {
                action.Status = UserToolActionStatuses.OutcomeUnknown;
                action.ErrorCategory = "outcome_unknown";
                action.SafeErrorMessage = "The action approval was consumed before the tool outcome was recorded.";
                action.CompletedAt = null;
                action.UpdatedAt = DateTimeOffset.UtcNow;
                await SaveAsync(action, cancellationToken).ConfigureAwait(false);
                return await ToResultAsync(action, cancellationToken).ConfigureAwait(false);
            }
            if (approval.Status != "approved" || approval.ExpiresAt <= DateTimeOffset.UtcNow)
                return await BlockAsync(action, approval.Status == "expired" ? "approval_expired" : "not_approved", "The action approval was not approved.", cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteAsync(action, project, descriptor, cancellationToken).ConfigureAwait(false);
    }

    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        await _recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private async Task RecoverCoreAsync(CancellationToken cancellationToken)
    {
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // The process may have exited at any point after claiming an action.
        // Since Core cannot prove whether the provider observed the request, a
        // running action is never replayed automatically.
        var now = DateTimeOffset.UtcNow;
        await db.UserToolActions
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                && x.Status == UserToolActionStatuses.Running)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.Status, UserToolActionStatuses.OutcomeUnknown)
                .SetProperty(x => x.ErrorCategory, "outcome_unknown")
                .SetProperty(x => x.SafeErrorMessage, "The host stopped while the tool action was running; its external outcome is unknown.")
                .SetProperty(x => x.CompletedAt, (DateTimeOffset?)null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);

        var candidateRows = await db.UserToolActions.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                && (x.Status == "requested" || x.Status == "authorized"
                    || x.Status == UserToolActionStatuses.AwaitingDelegate
                    || x.Status == UserToolActionStatuses.AwaitingUser
                    || x.Status == UserToolActionStatuses.AwaitingApproval))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var candidates = candidateRows.OrderBy(x => x.CreatedAt).Select(x => x.Id).ToArray();

        foreach (var actionId in candidates)
        {
            try
            {
                await ResumeAsync(actionId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // One corrupt action must not prevent recovery of the rest. A
                // later scan or explicit resume will deterministically retry it.
            }
        }
    }

    public async Task<UserToolActionResult> OverrideSnapshotAsync(Guid actionId, string reason, CancellationToken cancellationToken = default)
    {
        var action = await FindAsync(actionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("User tool action was not found.");
        var scope = _tenant.Current;
        if (action.PrincipalId != scope.PrincipalId) throw new UnauthorizedAccessException("Only the initiating user may override a snapshot failure.");
        if (!action.MutatesWorkspace || action.ErrorCategory != RunErrorTaxonomy.SnapshotFailed || IsTerminal(action.Status) && action.Status != UserToolActionStatuses.Blocked)
            throw new InvalidOperationException("Only a blocked snapshot failure can be overridden.");
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A snapshot override reason is required.", nameof(reason));
        action.SnapshotOverride = true;
        action.SnapshotOverrideReason = SafeMessage(reason);
        action.NonReversible = true;
        action.CompensationGuidance = "Inspect the resulting workspace state and apply a manual compensating change; Core has no snapshot to restore for this action.";
        action.ErrorCategory = RunErrorTaxonomy.SnapshotOverride;
        action.SafeErrorMessage = "User explicitly accepted a non-reversible write without a workspace snapshot.";
        action.Status = "requested";
        action.CompletedAt = null;
        action.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(action, cancellationToken).ConfigureAwait(false);
        var project = await _sessions.FindProjectAsync(action.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Project was not found.");
        EnsureProjectScope(project, scope);
        var descriptorValidation = await ValidateFrozenToolAsync(action, project.RootPath, cancellationToken).ConfigureAwait(false);
        if (descriptorValidation.Error is not null)
            return await BlockAsync(action, descriptorValidation.Error.Value.Code, descriptorValidation.Error.Value.Message, cancellationToken).ConfigureAwait(false);
        var descriptor = descriptorValidation.Descriptor!;
        return await AuthorizeAndMaybeRunAsync(action, descriptor, project, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserToolActionResult> DecideRecoveryAsync(
        Guid actionId,
        string decision,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var normalized = decision?.Trim().ToLowerInvariant();
        if (normalized is not ("mark_completed" or "mark_failed"))
            throw new ArgumentException("decision must be mark_completed or mark_failed", nameof(decision));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A recovery decision reason is required.", nameof(reason));

        var action = await FindAsync(actionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("User tool action was not found.");
        var scope = _tenant.Current;
        if (action.PrincipalId != scope.PrincipalId)
            throw new UnauthorizedAccessException("Only the initiating user may decide an unknown tool outcome.");
        if (action.RecoveryDecision is not null)
        {
            if (string.Equals(action.RecoveryDecision, normalized, StringComparison.Ordinal))
                return await ToResultAsync(action, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The user tool action already has a different recovery decision.");
        }
        if (action.Status != UserToolActionStatuses.OutcomeUnknown)
            throw new InvalidOperationException("Only an outcome_unknown user tool action accepts a recovery decision.");

        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var updated = await db.UserToolActions.Where(x => x.Id == action.Id
                && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                && x.PrincipalId == scope.PrincipalId && x.Status == UserToolActionStatuses.OutcomeUnknown
                && x.RecoveryDecision == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.Status, normalized == "mark_completed" ? UserToolActionStatuses.Completed : UserToolActionStatuses.Failed)
                .SetProperty(x => x.RecoveryDecision, normalized)
                .SetProperty(x => x.RecoveryReason, SafeMessage(reason))
                .SetProperty(x => x.RecoveredAt, now)
                .SetProperty(x => x.ErrorCategory, normalized == "mark_completed" ? null : RunErrorTaxonomy.RecoveryMarkedFailed)
                .SetProperty(x => x.SafeErrorMessage, normalized == "mark_completed" ? null : SafeMessage(reason))
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
        if (updated != 1)
        {
            var concurrent = await FindAsync(action.Id, cancellationToken).ConfigureAwait(false);
            if (concurrent is not null && string.Equals(concurrent.RecoveryDecision, normalized, StringComparison.Ordinal))
                return await ToResultAsync(concurrent, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The user tool action recovery state changed concurrently.");
        }

        var recovered = await FindAsync(action.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("User tool action was not found after recovery.");
        return await ToResultAsync(recovered, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UserToolActionResult> AuthorizeAndMaybeRunAsync(UserToolActionRecord action, ToolManifestEntryDto descriptor, ProjectReference? project, CancellationToken cancellationToken)
    {
        if (action.PermissionRequestId is null)
        {
            var authorization = await _authorization.AuthorizeToolAsync(new ToolAuthorizationCommand(
                action.PrincipalId, null, new CapabilityClaim("tool.invoke", descriptor.MutatesWorkspace ? "mutate" : "read", $"tool://{descriptor.Id}"),
                null, null, action.Risk, 0m, 1, TimeSpan.FromMinutes(30), $"User requested tool '{descriptor.Id}'.", $"user-action-auth:{action.Id:N}"), cancellationToken).ConfigureAwait(false);
            action.PermissionRequestId = authorization.PermissionRequestId;
            action.AuthorizationDecisionId = authorization.AuthorizationDecisionId;
            action.CapabilityLeaseId = authorization.CapabilityLeaseId;
            action.Status = authorization.Status switch
            {
                "allowed" when action.RequiresApproval => UserToolActionStatuses.AwaitingApproval,
                "allowed" => "authorized",
                "awaiting_delegate" => UserToolActionStatuses.AwaitingDelegate,
                "awaiting_user" => UserToolActionStatuses.AwaitingUser,
                _ => UserToolActionStatuses.Blocked
            };
            if (action.Status == UserToolActionStatuses.Blocked)
            { action.ErrorCategory = authorization.ErrorCategory; action.SafeErrorMessage = authorization.Message; action.CompletedAt = DateTimeOffset.UtcNow; }
            action.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveAsync(action, cancellationToken).ConfigureAwait(false);
        }
        if (action.Status == UserToolActionStatuses.AwaitingApproval)
        {
            if (action.ActionApprovalId is null)
            {
                action.ActionApprovalId = await CreateActionApprovalAsync(action, cancellationToken).ConfigureAwait(false);
                await SaveAsync(action, cancellationToken).ConfigureAwait(false);
            }
            return await ToResultAsync(action, cancellationToken).ConfigureAwait(false);
        }
        if (action.Status is UserToolActionStatuses.AwaitingDelegate or UserToolActionStatuses.AwaitingUser or UserToolActionStatuses.Blocked)
            return await ToResultAsync(action, cancellationToken).ConfigureAwait(false);
        return await ExecuteAsync(action, project, descriptor, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UserToolActionResult> ExecuteAsync(UserToolActionRecord action, ProjectReference? project, ToolManifestEntryDto descriptor, CancellationToken cancellationToken)
    {
        var parameters = await ReadContentAsync(action.ParametersReference, action.ParametersHash, action.ParametersLength, "application/json", cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureParametersMatchDescriptor(parameters, descriptor);
        }
        catch (ArgumentException ex)
        {
            return await BlockAsync(action, "parameters_schema_mismatch", ex.Message, cancellationToken).ConfigureAwait(false);
        }
        if (action.MutatesWorkspace && action.SnapshotId is { } snapshotId)
        {
            try
            {
                var validation = await _snapshots.ValidateAsync(snapshotId, cancellationToken).ConfigureAwait(false);
                if (!validation.IsValid)
                    return await BlockAsync(action, "snapshot_changed", "The pre-write workspace snapshot no longer matches the current workspace.", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException or DirectoryNotFoundException)
            {
                return await BlockAsync(action, "snapshot_unavailable", SafeMessage(ex.Message), cancellationToken).ConfigureAwait(false);
            }
        }

        if (action.CapabilityLeaseId is not { } leaseId)
            return await BlockAsync(action, "capability_lease_required", "A matching capability lease is required before the tool can execute.", cancellationToken).ConfigureAwait(false);

        // Claim the action before consuming either one-time authorization fact.
        // Concurrent resume calls therefore have a single provider-call owner.
        await using (var claimDb = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var claimed = await claimDb.UserToolActions.Where(x => x.Id == action.Id
                    && x.TenantId == action.TenantId && x.WorkspaceId == action.WorkspaceId
                    && (x.Status == "authorized" || x.Status == UserToolActionStatuses.AwaitingApproval))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(x => x.Status, UserToolActionStatuses.Running)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            if (claimed != 1)
            {
                var current = await FindAsync(action.Id, cancellationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("User tool action was not found.");
                return await ToResultAsync(current, cancellationToken).ConfigureAwait(false);
            }
        }
        action.Status = UserToolActionStatuses.Running;
        action.UpdatedAt = DateTimeOffset.UtcNow;

        if (action.RequiresApproval)
        {
            await using var approvalDb = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var approval = await approvalDb.ApprovalRequests.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == action.ActionApprovalId && x.UserToolActionId == action.Id,
                cancellationToken).ConfigureAwait(false);
            if (approval is null || !await TryConsumeUserApprovalAsync(approval, action, cancellationToken).ConfigureAwait(false))
                return await BlockAsync(action, "approval_nonce_invalid", "The action approval could not be consumed.", cancellationToken).ConfigureAwait(false);
        }

        // User actions keep the lease secret entirely inside Core. The
        // authorization port reloads protected nonce material and performs the
        // atomic, idempotent one-use CAS immediately before the provider call.
        var consumedLease = await _authorization.ConsumeToolLeaseAsync(new ToolLeaseConsumptionCommand(
            CapabilityLeaseId: leaseId,
            Nonce: null,
            SubjectPrincipalId: action.PrincipalId,
            SubjectAgentInstanceId: null,
            Claim: new CapabilityClaim("tool.invoke", descriptor.MutatesWorkspace ? "mutate" : "read", $"tool://{descriptor.Id}"),
            RunId: null,
            TaskId: null,
            IdempotencyKey: $"user-tool-action-lease:{action.Id:N}"), cancellationToken).ConfigureAwait(false);
        if (consumedLease.Status != "allowed")
            return await BlockAsync(action, consumedLease.ErrorCategory ?? "lease_not_authorized", consumedLease.Message ?? "The capability lease could not be consumed.", cancellationToken).ConfigureAwait(false);
        action.AuthorizationDecisionId = consumedLease.AuthorizationDecisionId;
        action.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(action, cancellationToken).ConfigureAwait(false);

        ToolWireResponseDto response;
        try
        {
            using var document = JsonDocument.Parse(parameters);
            if (descriptor.Id == "mcp_resource_update") response = await ExecuteManagedMcpAsync(document.RootElement, action.ProjectId, cancellationToken).ConfigureAwait(false);
            else if (descriptor.Id == "skill_resource_update") response = await ExecuteManagedSkillAsync(document.RootElement, cancellationToken).ConfigureAwait(false);
            else if (descriptor.Id == "skill_project_package") response = await ExecuteSkillProjectPackageAsync(document.RootElement, project!, cancellationToken).ConfigureAwait(false);
            else if (descriptor.Id == "mcp_program_update") response = await ExecuteMcpProgramAsync(document.RootElement, cancellationToken).ConfigureAwait(false);
            else
            {
                ToolExecutionContextDto? context = null;
                if (_toolSettings is not null && action.ToolConfigurationLength > 0)
                {
                    var json = await ReadContentAsync(action.ToolConfigurationReference, action.ToolConfigurationHash, action.ToolConfigurationLength, "application/json", cancellationToken).ConfigureAwait(false);
                    var frozenContext = JsonSerializer.Deserialize<ToolExecutionContextDto>(json, JsonOptions) ?? throw new InvalidDataException("Frozen user action configuration is unavailable.");
                    context = await _toolSettings.MaterializeForCallAsync(frozenContext, [descriptor.Id], cancellationToken, $"user-action:{action.Id:N}", descriptor.Id).ConfigureAwait(false);
                }
                response = await _provider.CallAsync(project!.RootPath, new ToolWireRequestDto
                { ToolId = descriptor.Id, SessionId = $"user-action:{action.Id:N}", Approved = true, Params = document.RootElement.Clone(), ExecutionContext = context },
                    context is null ? TimeSpan.FromSeconds(120) : ToolSettingsSchema.WireBudget(context.Settings, descriptor.Id, document.RootElement, TimeSpan.FromSeconds(120)), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ToolSettingsException ex)
        {
            action.Status = "failed"; action.ErrorCategory = ex.Code; action.SafeErrorMessage = ex.Message; action.CompletedAt = action.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveAsync(action, cancellationToken).ConfigureAwait(false); return await ToResultAsync(action, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            action.Status = action.MutatesWorkspace ? UserToolActionStatuses.OutcomeUnknown : "failed";
            action.ErrorCategory = ex is TimeoutException ? "timeout" : "process_exit";
            action.SafeErrorMessage = SafeMessage(ex.Message);
            action.CompletedAt = action.Status == UserToolActionStatuses.OutcomeUnknown ? null : DateTimeOffset.UtcNow;
            action.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveAsync(action, cancellationToken).ConfigureAwait(false);
            return await ToResultAsync(action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_contextLifecycle is not null) await _contextLifecycle.ReleaseAsync($"user-action:{action.Id:N}", CancellationToken.None).ConfigureAwait(false);
        }
        if (!response.IsSuccess)
        {
            action.Status = "failed";
            action.ErrorCategory = "tool_error";
            action.SafeErrorMessage = SafeMessage(response.Error);
            action.CompletedAt = DateTimeOffset.UtcNow;
            action.UpdatedAt = action.CompletedAt.Value;
            await SaveAsync(action, cancellationToken).ConfigureAwait(false);
            return await ToResultAsync(action, cancellationToken).ConfigureAwait(false);
        }
        var resultJson = response.Result?.GetRawText() ?? "null";
        var stored = await PutContentAsync(_tenant.Current, "user-tool-result", resultJson, cancellationToken).ConfigureAwait(false);
        action.ResultReference = stored.Value; action.ResultHash = stored.Sha256; action.ResultLength = stored.Length;
        action.Status = UserToolActionStatuses.Completed; action.ErrorCategory = null; action.SafeErrorMessage = null;
        action.CompletedAt = action.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(action, cancellationToken).ConfigureAwait(false);
        return await ToResultAsync(action, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid> CreateActionApprovalAsync(UserToolActionRecord action, CancellationToken cancellationToken)
    {
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.ApprovalRequests.SingleOrDefaultAsync(x => x.UserToolActionId == action.Id
            && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return existing.Id;
        var now = DateTimeOffset.UtcNow;
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        // A reviewer cannot decide "User requested 'write_file'" without knowing which
        // file. The parameters are read back exactly once here, at mint, so the
        // approvals list stays a pure projection; a content failure leaves the row
        // without evidence rather than blocking an approval that must exist.
        var argumentsDigest = string.Empty;
        try
        {
            var mintParameters = await ReadContentAsync(action.ParametersReference, action.ParametersHash,
                action.ParametersLength, "application/json", cancellationToken).ConfigureAwait(false);
            argumentsDigest = ApprovalEvidenceProjector.Encode(
                ApprovalEvidenceProjector.Project(action.ToolId, mintParameters));
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException)
        {
        }
        var row = new ApprovalRequestRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, ProjectId = action.ProjectId,
            UserToolActionId = action.Id, Kind = "user_tool", ToolId = action.ToolId, Risk = action.Risk,
            RequestHash = action.ParametersHash, NonceHash = ToolParametersHash.Compute(nonce),
            NonceSecretReference = $"approval_nonce_{scope.TenantId:N}_{Guid.NewGuid():N}", ParametersReference = action.ParametersReference,
            ArgumentsDigest = argumentsDigest,
            Summary = $"User requested '{action.ToolId}'.", Status = "pending", ExpiresAt = now.AddMinutes(30),
            RequestedByPrincipalId = scope.PrincipalId, CreatedAt = now, UpdatedAt = now, Nonce = nonce
        };
        await _nonceMaterials.PutAsync(row.NonceSecretReference, nonce, cancellationToken).ConfigureAwait(false);
        db.ApprovalRequests.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row.Id;
    }

    private async Task<bool> TryConsumeUserApprovalAsync(ApprovalRequestRecord approval, UserToolActionRecord action, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (approval.Status != "approved"
            || !string.Equals(approval.Decision, "approved", StringComparison.OrdinalIgnoreCase)
            || approval.ExpiresAt <= now
            || approval.Kind != "user_tool"
            || approval.RequestedByPrincipalId != action.PrincipalId
            || approval.ToolId != action.ToolId
            || approval.Risk != action.Risk
            || approval.ProjectId != action.ProjectId
            || approval.RequestHash != action.ParametersHash
            || approval.ParametersReference != action.ParametersReference
            || approval.UserToolActionId != action.Id
            || approval.RunId is not null
            || approval.TaskId is not null
            || approval.AgentInstanceId is not null
            || approval.ExecutionId is not null)
            return false;

        var nonce = await _nonceMaterials.GetAsync(approval.NonceSecretReference ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(nonce) || !FixedEquals(approval.NonceHash, ToolParametersHash.Compute(nonce))) return false;
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var updated = await db.ApprovalRequests.Where(x => x.Id == approval.Id && x.UserToolActionId == action.Id
            && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status == "approved"
            && x.Decision == "approved" && x.ConsumedByExecutionId == null && x.Kind == "user_tool"
            && x.RequestedByPrincipalId == action.PrincipalId && x.ProjectId == action.ProjectId
            && x.ToolId == action.ToolId && x.Risk == action.Risk && x.RequestHash == action.ParametersHash
            && x.ParametersReference == action.ParametersReference)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "consumed")
                .SetProperty(x => x.ConsumedByExecutionId, action.Id)
                .SetProperty(x => x.ConsumedAt, DateTimeOffset.UtcNow).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        return updated == 1;
    }

    private async Task<UserToolActionRecord?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.UserToolActions.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UserToolActionResult> BlockAsync(UserToolActionRecord action, string code, string message, CancellationToken cancellationToken)
    {
        action.Status = UserToolActionStatuses.Blocked; action.ErrorCategory = code; action.SafeErrorMessage = SafeMessage(message);
        action.CompletedAt = DateTimeOffset.UtcNow; action.UpdatedAt = action.CompletedAt.Value;
        await SaveAsync(action, cancellationToken).ConfigureAwait(false);
        return await ToResultAsync(action, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveAsync(UserToolActionRecord action, CancellationToken cancellationToken)
    { await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false); db.UserToolActions.Update(action); await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }

    private async Task<UserToolActionResult> ToResultAsync(UserToolActionRecord action, CancellationToken cancellationToken)
    {
        string? result = null;
        if (!string.IsNullOrWhiteSpace(action.ResultReference))
        {
            try { result = await ReadContentAsync(action.ResultReference, action.ResultHash ?? string.Empty, action.ResultLength ?? 0, "application/json", cancellationToken).ConfigureAwait(false); }
            catch (InvalidDataException) { }
        }
        return new UserToolActionResult(action.Id, action.AuditReference, action.TenantId, action.WorkspaceId, action.ProjectId, action.PrincipalId,
            action.ToolId, action.Status, action.Risk, action.MutatesWorkspace, action.RequiresApproval,
            action.PermissionRequestId, action.AuthorizationDecisionId, action.ActionApprovalId, action.SnapshotId,
            action.SnapshotHash, action.SnapshotOverride, action.SnapshotOverrideReason,
            action.NonReversible, action.CompensationGuidance,
            action.RecoveryDecision, action.RecoveryReason, action.RecoveredAt, result,
            action.ErrorCategory, action.SafeErrorMessage, action.CreatedAt, action.UpdatedAt, action.CompletedAt);
    }

    private async Task<ContentReference> PutContentAsync(TenantContext scope, string kind, string content, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false);
        return await _content.PutAsync(new ContentWriteRequest(scope.TenantId, scope.WorkspaceId, kind, "application/json", stream), cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ReadContentAsync(string reference, string hash, long length, string mediaType, CancellationToken cancellationToken)
    {
        await using var stream = await _content.OpenReadAsync(new ContentReference(reference, hash, length, mediaType), cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TrustedTool> ReadTrustedToolAsync(string toolId, string workspaceRoot, CancellationToken cancellationToken)
    {
        if (toolId == "mcp_resource_update") return new TrustedTool(2, ToolManifestHasher.Compute([ManagedMcpDescriptor]), ManagedMcpDescriptor);
        if (toolId == "skill_resource_update") return new TrustedTool(2, ToolManifestHasher.Compute([ManagedSkillDescriptor]), ManagedSkillDescriptor);
        if (toolId == "skill_project_package") return new TrustedTool(2, ToolManifestHasher.Compute([ProjectSkillDescriptor]), ProjectSkillDescriptor);
        if (toolId == "mcp_program_update") return new TrustedTool(2, ToolManifestHasher.Compute([ManagedMcpProgramDescriptor]), ManagedMcpProgramDescriptor);
        var manifest = await _provider.GetManifestAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
        if (manifest.ProtocolVersion != 2)
            throw new InvalidDataException("Tool Provider must expose manifest protocol v2.");
        var computedHash = ToolManifestHasher.Compute(manifest.Tools);
        if (string.IsNullOrWhiteSpace(manifest.ManifestHash)
            || !FixedEquals(manifest.ManifestHash, computedHash))
            throw new InvalidDataException("Tool Provider manifest identity is invalid.");
        var descriptor = manifest.Tools.SingleOrDefault(x => string.Equals(x.Id, toolId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Tool '{toolId}' was not found.");
        if (descriptor.InputSchema.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Tool '{toolId}' has an invalid input schema.");
        return new TrustedTool(manifest.ProtocolVersion, computedHash, descriptor);
    }

    private async Task<FrozenToolValidation> ValidateFrozenToolAsync(
        UserToolActionRecord action,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        if (_storageLocations is { } locations)
        {
            if (string.IsNullOrWhiteSpace(action.ToolConfigurationReference))
                return FrozenToolValidation.Failed("frozen_storage_scope_mismatch", "The historical action has no current storage scope binding.");
            try
            {
                var scopeJson = await ReadContentAsync(action.ToolConfigurationReference, action.ToolConfigurationHash, action.ToolConfigurationLength, "application/json", cancellationToken);
                var scopeBinding = JsonSerializer.Deserialize<ToolExecutionContextDto>(scopeJson, JsonOptions);
                if (scopeBinding?.StorageId != locations.StorageId || scopeBinding.StorageRoot != locations.Root)
                    return FrozenToolValidation.Failed("frozen_storage_scope_mismatch", "The historical action belongs to a different storage scope; its approval cannot be resumed here.");
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
            { return FrozenToolValidation.Failed("frozen_storage_scope_mismatch", "The action's storage scope binding is unavailable."); }
        }
        if (action.ProviderProtocolVersion != 2
            || string.IsNullOrWhiteSpace(action.ProviderManifestHash)
            || string.IsNullOrWhiteSpace(action.ToolDescriptorReference)
            || string.IsNullOrWhiteSpace(action.ToolDescriptorHash)
            || action.ToolDescriptorLength <= 0)
            return FrozenToolValidation.Failed("manifest_binding_missing", "The action has no trusted frozen Tool Provider manifest binding.");

        FrozenToolManifestEntry frozen;
        try
        {
            var json = await ReadContentAsync(action.ToolDescriptorReference, action.ToolDescriptorHash,
                action.ToolDescriptorLength, "application/json", cancellationToken).ConfigureAwait(false);
            frozen = JsonSerializer.Deserialize<FrozenToolManifestEntry>(json, JsonOptions)
                ?? throw new InvalidDataException("Frozen tool descriptor is empty.");
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            return FrozenToolValidation.Failed("tool_descriptor_unavailable", SafeMessage(ex.Message));
        }
        if (!string.Equals(frozen.Id, action.ToolId, StringComparison.OrdinalIgnoreCase)
            || NormalizeRisk(frozen.Risk) != action.Risk
            || frozen.MutatesWorkspace != action.MutatesWorkspace
            || (frozen.RequiresApproval || frozen.MutatesWorkspace) != action.RequiresApproval)
            return FrozenToolValidation.Failed("tool_descriptor_binding_changed", "The persisted action no longer matches its frozen tool descriptor.");
        if (action.ToolId == "mcp_resource_update")
            return FixedEquals(action.ProviderManifestHash, ToolManifestHasher.Compute([ManagedMcpDescriptor])) && ToolManifestHasher.Equivalent(ManagedMcpDescriptor, frozen)
                ? new FrozenToolValidation(ManagedMcpDescriptor, null) : FrozenToolValidation.Failed("tool_descriptor_changed", "The managed resource descriptor changed.");
        if (action.ToolId == "skill_resource_update")
            return FixedEquals(action.ProviderManifestHash, ToolManifestHasher.Compute([ManagedSkillDescriptor])) && ToolManifestHasher.Equivalent(ManagedSkillDescriptor, frozen)
                ? new FrozenToolValidation(ManagedSkillDescriptor, null) : FrozenToolValidation.Failed("tool_descriptor_changed", "The managed skill descriptor changed.");
        if (action.ToolId == "skill_project_package")
            return FixedEquals(action.ProviderManifestHash, ToolManifestHasher.Compute([ProjectSkillDescriptor])) && ToolManifestHasher.Equivalent(ProjectSkillDescriptor, frozen)
                ? new FrozenToolValidation(ProjectSkillDescriptor, null) : FrozenToolValidation.Failed("tool_descriptor_changed", "The project skill descriptor changed.");
        if (action.ToolId == "mcp_program_update")
            return FixedEquals(action.ProviderManifestHash, ToolManifestHasher.Compute([ManagedMcpProgramDescriptor])) && ToolManifestHasher.Equivalent(ManagedMcpProgramDescriptor, frozen)
                ? new FrozenToolValidation(ManagedMcpProgramDescriptor, null) : FrozenToolValidation.Failed("tool_descriptor_changed", "The managed program descriptor changed.");

        ToolManifestDto manifest;
        try
        {
            manifest = await _provider.GetManifestAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            return FrozenToolValidation.Failed("tool_manifest_unavailable", SafeMessage(ex.Message));
        }
        var computedHash = ToolManifestHasher.Compute(manifest.Tools);
        if (manifest.ProtocolVersion != action.ProviderProtocolVersion
            || string.IsNullOrWhiteSpace(manifest.ManifestHash)
            || !FixedEquals(manifest.ManifestHash, computedHash)
            || !FixedEquals(action.ProviderManifestHash, computedHash))
            return FrozenToolValidation.Failed("tool_manifest_changed", "The Tool Provider manifest changed after this action was admitted.");

        var live = manifest.Tools.SingleOrDefault(x => string.Equals(x.Id, action.ToolId, StringComparison.OrdinalIgnoreCase));
        if (live is null || !ToolManifestHasher.Equivalent(live, frozen))
            return FrozenToolValidation.Failed("tool_descriptor_changed", "The tool descriptor changed after this action was admitted.");
        return new FrozenToolValidation(live, null);
    }

    private static FrozenToolManifestEntry ToFrozen(ToolManifestEntryDto descriptor) => new(
        descriptor.Id,
        descriptor.Description,
        descriptor.InputSchema.Clone(),
        descriptor.Risk,
        descriptor.MutatesWorkspace,
        descriptor.RequiresApproval,
        descriptor.RetrySafety,
        descriptor.ConfirmationFields.ToArray());

    private static void EnsureParametersMatchDescriptor(string parametersJson, ToolManifestEntryDto descriptor)
    {
        var schema = descriptor.InputSchema;
        using var parameters = JsonDocument.Parse(parametersJson);
        var root = parameters.RootElement;
        if (root.ValueKind == JsonValueKind.Null)
            throw new ArgumentException("Tool parameters must be a JSON object.");
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Tool parameters must be a JSON object.");
        if (schema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("The tool input schema is invalid.");

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !root.TryGetProperty(item.GetString()!, out _))
                    throw new ArgumentException($"Tool parameter '{item.GetString()}' is required by the frozen schema.");
            }
        }

        var hasProperties = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object;
        var allowsAdditional = !schema.TryGetProperty("additionalProperties", out var additional)
            || additional.ValueKind != JsonValueKind.False;
        foreach (var property in root.EnumerateObject())
        {
            if (!hasProperties || !properties.TryGetProperty(property.Name, out var propertySchema))
            {
                if (!allowsAdditional) throw new ArgumentException($"Tool parameter '{property.Name}' is not allowed by the frozen schema.");
                continue;
            }
            if (!MatchesJsonType(property.Value, propertySchema))
                throw new ArgumentException($"Tool parameter '{property.Name}' does not match the frozen schema.");
        }

        foreach (var confirmationField in descriptor.ConfirmationFields)
        {
            if (string.IsNullOrWhiteSpace(confirmationField)
                || !root.TryGetProperty(confirmationField, out var confirmation)
                || confirmation.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(confirmation.GetString()))
                throw new ArgumentException($"Tool parameter '{confirmationField}' must contain explicit user confirmation.");
        }
    }

    private static bool MatchesJsonType(JsonElement value, JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return true;
        return type.GetString() switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true
        };
    }

    private static bool NeedsPrewriteSnapshot(ToolManifestEntryDto descriptor) => descriptor.Id is not "mcp_resource_update" and not "skill_resource_update" and not "mcp_program_update" && descriptor.MutatesWorkspace && RiskRank(descriptor.Risk) >= 2;

    private static readonly ToolManifestEntryDto ManagedMcpProgramDescriptor = new()
    {
        Id = "mcp_program_update", Description = "Explicitly install or uninstall the pinned program of a registered MCP resource.", Risk = "high", MutatesWorkspace = true,
        RequiresApproval = true, RetrySafety = "unsafe", ConfirmationFields = ["action", "manager", "package", "version", "plan_hash"],
        InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { action = new { type = "string" }, resource_id = new { type = "string" },
            expected_revision = new { type = "integer" }, manager = new { type = "string" }, package = new { type = "string" }, version = new { type = "string" },
            package_root = new { type = "string" }, plan_hash = new { type = "string" }, server_arguments = new { type = "array" } },
            required = new[] { "action", "resource_id", "expected_revision", "manager", "package", "version", "package_root", "plan_hash", "server_arguments" } })
    };

    private static void ValidateMcpProgramParameters(string parameters, Guid projectId)
    {
        var data = JsonSerializer.Deserialize<ManagedMcpProgramPreviewDto>(parameters, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
            ?? throw new ArgumentException("Invalid MCP program action.");
        if (data.ResourceId == Guid.Empty || data.Action is not ("install" or "uninstall") || data.ExpectedRevision < 0 || (data.ProjectId ?? Guid.Empty) != projectId
            || data.Manager is not ("npm" or "uv") || data.PlanHash.Length != 64 || data.PlanHash.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Invalid MCP program action or scope.");
    }

    private async Task<ToolWireResponseDto> ExecuteMcpProgramAsync(JsonElement parameters, CancellationToken ct)
    {
        if (_mcpPrograms is null) throw new ToolSettingsException("mcp_program_manager_unavailable", "Managed MCP programs are unavailable.", 503);
        var input = parameters.Deserialize<ManagedMcpProgramPreviewDto>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
        var result = await _mcpPrograms.ApplyApprovedAsync(input, ct);
        return new() { IsSuccess = true, Result = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) };
    }

    private static readonly ToolManifestEntryDto ManagedMcpDescriptor = new()
    {
        Id = "mcp_resource_update", Description = "Update the Core managed MCP resource registry.", Risk = "high", MutatesWorkspace = true, RequiresApproval = true, RetrySafety = "unsafe", ConfirmationFields = ["action", "id", "command"],
        InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { action = new { type = "string" }, resource_id = new { type = "string" }, project_id = new { type = "string" }, expected_revision = new { type = "integer" }, id = new { type = "string" }, name = new { type = "string" }, enabled = new { type = "boolean" }, command = new { type = "string" }, args = new { type = "array" }, env = new { type = "object" }, cwd = new { type = "string" } }, required = new[] { "action", "expected_revision" } })
    };
    private static readonly ToolManifestEntryDto ManagedSkillDescriptor = new()
    {
        Id = "skill_resource_update", Description = "Update a Core managed shared Skill package.", Risk = "high", MutatesWorkspace = true, RequiresApproval = true, RetrySafety = "unsafe", ConfirmationFields = ["action", "name"],
        InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { action = new { type = "string" }, resource_id = new { type = "string" }, expected_revision = new { type = "integer" }, expected_package_hash = new { type = "string" }, expected_package_digest = new { type = "string" }, scope = new { type = "string" }, name = new { type = "string" }, content = new { type = "string" }, files = new { type = "object" }, replace_files = new { type = "boolean" }, source = new { type = "string" }, version = new { type = "string" }, commit = new { type = "string" } }, required = new[] { "action", "expected_revision", "scope", "name", "content" } })
    };
    private static readonly ToolManifestEntryDto ProjectSkillDescriptor = new()
    {
        Id = "skill_project_package", Description = "Write a complete reviewed Skill package through project file tools.", Risk = "high", MutatesWorkspace = true, RequiresApproval = true, RetrySafety = "unsafe", ConfirmationFields = [],
        InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { name = new { type = "string" }, content = new { type = "string" }, files = new { type = "object" }, expected_file_hashes = new { type = "object" } }, required = new[] { "name", "content", "files", "expected_file_hashes" } })
    };
    private static void ValidateSkillProjectPackage(string parameters)
    {
        using var doc = JsonDocument.Parse(parameters);
        var data = doc.RootElement.Deserialize<ToolSkillProjectPackageDto>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) ?? throw new ArgumentException("Invalid Skill package.");
        if (!WorkspaceSkillPolicy.ValidateName(data.Name, out _) || data.Files.Count > 256 || data.Action is not ("save" or "delete")) throw new ArgumentException("Invalid Skill package name, action or file count.");
        foreach (var (path, encoded) in data.Files)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') || Path.IsPathRooted(path) || path.Split('/').Any(x => x is "" or "." or "..")) throw new ArgumentException("Invalid Skill package path.");
            try { _ = Convert.FromBase64String(encoded); } catch (FormatException) { throw new ArgumentException("Skill package assets must be base64."); }
        }
    }
    private async Task<ToolWireResponseDto> ExecuteSkillProjectPackageAsync(JsonElement input, ProjectReference project, CancellationToken ct)
    {
        ValidateSkillProjectPackage(input.GetRawText());
        var data = input.Deserialize<ToolSkillProjectPackageDto>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
        var packageRoot = data.PackageRoot ?? Path.GetDirectoryName(WorkspaceSkillPolicy.AbsolutePathFor(project.RootPath, data.Name))!;
        var skillRoot = Path.Combine(project.RootPath, WorkspaceSkillPolicy.SkillRoots[0]);
        if (!WorkspaceSkillDiscovery.IsContained(skillRoot, packageRoot) || new DirectoryInfo(packageRoot).Name != data.Name)
            throw new ToolSettingsException("skill_path_escape", "Skill package root must be a named package inside .tinadec/skills.");
        foreach (var (path, expected) in data.ExpectedFileHashes)
        {
            var absolute = Path.GetFullPath(Path.Combine(packageRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!WorkspaceSkillDiscovery.IsContained(project.RootPath, absolute) || !WorkspaceSkillDiscovery.IsContained(packageRoot, absolute)) throw new ToolSettingsException("skill_path_escape", "Skill file leaves its package root.");
            var response = await _provider.CallAsync(project.RootPath, new ToolWireRequestDto { ToolId = "read_file", SessionId = "skill-package-preflight", Params = JsonSerializer.SerializeToElement(new { filepath = absolute }) }, TimeSpan.FromSeconds(30), ct);
            var actual = response.Result is { } result && result.TryGetProperty("file_hash", out var hash) && hash.ValueKind == JsonValueKind.String ? hash.GetString() : null;
            if (string.IsNullOrEmpty(expected) ? File.Exists(absolute) : actual != expected) throw new ToolSettingsException("skill_hash_conflict", "A package file changed since it was reviewed.", 412);
        }
        if (data.Action == "delete")
        {
            foreach (var (path, expected) in data.ExpectedFileHashes)
            {
                var absolute = Path.GetFullPath(Path.Combine(packageRoot, path.Replace('/', Path.DirectorySeparatorChar)));
                if (!WorkspaceSkillDiscovery.IsContained(project.RootPath, absolute) || !WorkspaceSkillDiscovery.IsContained(packageRoot, absolute)) throw new ToolSettingsException("skill_path_escape", "Skill file leaves its package root.");
                if (expected is null) throw new ToolSettingsException("skill_hash_conflict", "Delete requires a file hash for every package file.", 412);
                var response = await _provider.CallAsync(project.RootPath, new ToolWireRequestDto { ToolId = "delete_file", SessionId = "skill-package", Approved = true, Params = JsonSerializer.SerializeToElement(new { filepath = absolute, file_hash = expected }) }, TimeSpan.FromSeconds(120), ct);
                if (!response.IsSuccess || response.Result is { } payload && payload.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) return new() { IsSuccess = false, Error = "A package file could not be deleted; inspect the retained snapshot." };
            }
            RemoveEmptySkillDirectories(packageRoot);
            return new() { IsSuccess = true, Result = JsonSerializer.SerializeToElement(new { deleted = true, name = data.Name }) };
        }
        var entries = new SortedDictionary<string,string>(StringComparer.Ordinal);
        foreach (var (key,value) in data.Files) entries[key] = value;
        entries["SKILL.md"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(data.Content));
        var fileManifest = await _provider.GetManifestAsync(project.RootPath, ct);
        if (!fileManifest.Tools.Any(x => x.Id == "write_file")) throw new ToolSettingsException("project_skill_writer_unavailable", "Project file writer is unavailable.");
        // Validate every root and hash before any byte is written. The approved workspace snapshot
        // and each tool's own hash gate then protect the gap between this read and its write.
        foreach (var path in entries.Keys)
        {
            var absolute = Path.GetFullPath(Path.Combine(packageRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!WorkspaceSkillDiscovery.IsContained(project.RootPath, absolute) || !WorkspaceSkillDiscovery.IsContained(packageRoot, absolute)) throw new ToolSettingsException("skill_path_escape", "Skill file or parent link leaves its package root.");
            if (!data.ExpectedFileHashes.ContainsKey(path)) throw new ToolSettingsException("skill_hash_conflict", "Every reviewed package file requires a hash or explicit create precondition.", 412);
        }
        foreach (var (removed, hash) in data.ExpectedFileHashes.Where(x => !entries.ContainsKey(x.Key)))
        {
            var response = await _provider.CallAsync(project.RootPath, new ToolWireRequestDto { ToolId = "delete_file", SessionId = "skill-package", Approved = true, Params = JsonSerializer.SerializeToElement(new { filepath = Path.Combine(packageRoot, removed.Replace('/',Path.DirectorySeparatorChar)), file_hash = hash }) }, TimeSpan.FromSeconds(120), ct);
            if (!response.IsSuccess || response.Result is { } payload && payload.TryGetProperty("success",out var success) && success.ValueKind == JsonValueKind.False) return new() { IsSuccess = false, Error = "An old package asset could not be removed; inspect the retained snapshot." };
        }
        foreach (var (path, encoded) in entries)
        {
            var parameters = new Dictionary<string,object?> { ["filepath"] = Path.Combine(packageRoot, path.Replace('/', Path.DirectorySeparatorChar)), ["content_base64"] = encoded };
            if (data.ExpectedFileHashes[path] is { Length: > 0 } hash) parameters["file_hash"] = hash;
            var response = await _provider.CallAsync(project.RootPath, new ToolWireRequestDto { ToolId = "write_file", SessionId = "skill-package", Approved = true, Params = JsonSerializer.SerializeToElement(parameters) }, TimeSpan.FromSeconds(120), ct);
            if (!response.IsSuccess || response.Result is { } result && result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) return new() { IsSuccess = false, Error = "The package file write was refused; inspect the retained snapshot and partial result." };
        }
        return new() { IsSuccess = true, Result = JsonSerializer.SerializeToElement(new { name = data.Name, package_files = entries.Keys.ToArray() }) };
    }
    private static void RemoveEmptySkillDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new ToolSettingsException("skill_path_escape", "Skill package directories cannot be links.");
        foreach (var directory in Directory.EnumerateDirectories(root)) RemoveEmptySkillDirectories(directory);
        if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
    }
    private static void ValidateManagedMcpParameters(string parameters, Guid projectId)
    {
        using var doc = JsonDocument.Parse(parameters); var input = doc.RootElement;
        if (input.GetProperty("action").GetString() is not ("save" or "delete") || !input.GetProperty("expected_revision").TryGetInt64(out var revision) || revision < 0) throw new ArgumentException("Invalid managed MCP action.");
        if (input.TryGetProperty("project_id", out var project) && project.ValueKind != JsonValueKind.Null && project.GetGuid() != projectId) throw new UnauthorizedAccessException("Managed MCP action must target its own project.");
        if (input.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object && env.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.Null && (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString() != "********"))) throw new ArgumentException("Set MCP credentials through the protected resource settings endpoint.");
    }
    private static void ValidateManagedSkillParameters(string parameters)
    {
        using var doc = JsonDocument.Parse(parameters); var input = doc.RootElement;
        if (!input.TryGetProperty("action", out var action) || action.GetString() is not ("save" or "delete")
            || !input.TryGetProperty("expected_revision", out var revision) || !revision.TryGetInt64(out var value) || value < 0
            || !input.TryGetProperty("scope", out var scope) || scope.GetString() != "shared"
            || !input.TryGetProperty("name", out var name) || string.IsNullOrWhiteSpace(name.GetString())
            || !input.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Invalid managed Skill action.");
        if (input.TryGetProperty("files", out var files) && files.ValueKind != JsonValueKind.Object) throw new ArgumentException("Skill assets must be an object.");
        if (input.TryGetProperty("replace_files", out var replace) && replace.ValueKind != JsonValueKind.True) throw new ArgumentException("Skill asset replacement must be explicit.");
    }
    private async Task<ToolWireResponseDto> ExecuteManagedMcpAsync(JsonElement input, Guid projectId, CancellationToken ct)
    {
        if (_mcpResources is null) throw new ToolSettingsException("mcp_registry_unavailable", "Managed MCP registry is unavailable.", 503);
        var resourceId = input.TryGetProperty("resource_id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetGuid() : (Guid?)null;
        var revision = input.GetProperty("expected_revision").GetInt64();
        if (resourceId is { } existingId)
        {
            var existing = await _mcpResources.GetAsync(existingId, ct) ?? throw new ToolSettingsException("mcp_resource_not_found", "MCP resource not found.", 404);
            if (existing.ProjectId != projectId) throw new UnauthorizedAccessException("The managed action cannot change a different resource scope.");
        }
        if (input.GetProperty("action").GetString() == "delete")
        {
            if (resourceId is null) throw new ToolSettingsException("invalid_mcp_action", "Delete requires resource_id.");
            await _mcpResources.DeleteAsync(resourceId.Value, revision, ct);
            return new() { IsSuccess = true, Result = JsonSerializer.SerializeToElement(new { resource_id = resourceId, deleted = true }) };
        }
        var data = input.Deserialize<McpResourceWriteDto>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) ?? throw new ToolSettingsException("invalid_mcp_action", "Invalid MCP resource.");
        var saved = await _mcpResources.SaveAsync(resourceId, new() { ProjectId = projectId, Id = data.Id, Name = data.Name, Enabled = data.Enabled, Command = data.Command, Args = data.Args, Env = data.Env, Cwd = data.Cwd }, revision, ct);
        return new() { IsSuccess = true, Result = JsonSerializer.SerializeToElement(saved, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) };
    }
    private async Task<ToolWireResponseDto> ExecuteManagedSkillAsync(JsonElement input, CancellationToken ct)
    {
        if (_skillResources is null) throw new ToolSettingsException("skill_registry_unavailable", "Managed Skill registry is unavailable.", 503);
        var data = input.Deserialize<ToolSkillResourceUpdateDto>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
            ?? throw new ToolSettingsException("invalid_skill_action", "Invalid managed Skill resource.");
        if (data.Action is not ("save" or "delete") || !string.Equals(data.Scope, "shared", StringComparison.Ordinal))
            throw new ToolSettingsException("invalid_skill_action", "Only shared Skill saves are supported by the managed action.");
        if (data.Action == "delete")
        {
            if (data.ResourceId is null) throw new ToolSettingsException("invalid_skill_action", "Delete requires resource_id.");
            var existing = await _skillResources.GetAsync(data.ResourceId.Value, null, ct) ?? throw new ToolSettingsException("skill_not_found", "Skill not found.", 404);
            if (existing.Revision != data.ExpectedRevision || existing.PackageHash != data.ExpectedPackageHash) throw new ToolSettingsException("skill_hash_conflict", "Skill changed since deletion was reviewed.", 412);
            await _skillResources.DeleteAsync(data.ResourceId.Value, null, data.ExpectedRevision, ct);
            return new() { IsSuccess = true, Result = JsonSerializer.SerializeToElement(new { resource_id = data.ResourceId, deleted = true }) };
        }
        var saved = await _skillResources.SaveAsync(data.ExpectedRevision == 0 ? null : data.ResourceId, null, new ToolSkillWriteDto
        {
            Scope = "shared", ReservedResourceId = data.ExpectedRevision == 0 ? data.ResourceId : null, Name = data.Name, Content = data.Content, Files = data.Files, ReplaceFiles = data.ReplaceFiles, ExpectedPackageDigest = data.ExpectedPackageDigest,
            Source = data.Source, Version = data.Version, Commit = data.Commit, Enabled = data.Enabled,
        }, data.ExpectedRevision, ct);
        return new() { IsSuccess = true, Result = JsonSerializer.SerializeToElement(saved, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) };
    }
    private static bool IsInherentlyNonReversible(string toolId) =>
        string.Equals(toolId, "git_push", StringComparison.OrdinalIgnoreCase);
    private static string? CompensationGuidanceFor(string toolId) =>
        string.Equals(toolId, "git_push", StringComparison.OrdinalIgnoreCase)
            ? "A remote push cannot be rolled back by a workspace snapshot. Inspect the remote ref and, when policy permits, create and push a compensating revert commit."
            : null;
    private static bool IsTerminal(string status) => status is UserToolActionStatuses.Completed or UserToolActionStatuses.Blocked or UserToolActionStatuses.OutcomeUnknown or UserToolActionStatuses.Failed;
    private static int RiskRank(string risk) => risk.ToLowerInvariant() switch { "low" => 0, "medium" => 1, "high" => 2, "critical" => 3, _ => int.MaxValue };
    private static string NormalizeRisk(string risk) => risk.ToLowerInvariant() is "low" or "medium" or "high" or "critical" ? risk.ToLowerInvariant() : "high";
    private static string? NormalizeIdempotencyKey(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, 256)];
    private static bool TryNormalizeParameters(string value, out string normalized)
    {
        normalized = "null";
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(value) ? "null" : value);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) return false;
            normalized = document.RootElement.GetRawText(); return true;
        }
        catch (JsonException) { return false; }
    }
    private static void EnsureProjectScope(ProjectReference project, TenantContext scope)
    { if (project.TenantId != scope.TenantId || project.WorkspaceId != scope.WorkspaceId) throw new UnauthorizedAccessException("Project is outside the current tenant/workspace."); }
    private static bool FixedEquals(string? left, string? right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    private static string SafeMessage(string? message) => string.IsNullOrWhiteSpace(message) ? "User tool action failed." : message.Trim()[..Math.Min(message.Trim().Length, 4096)];

    private sealed record TrustedTool(int ProtocolVersion, string ManifestHash, ToolManifestEntryDto Descriptor);
    private sealed record FrozenToolValidation(ToolManifestEntryDto? Descriptor, (string Code, string Message)? Error)
    {
        public static FrozenToolValidation Failed(string code, string message) => new(null, (code, message));
    }
}
