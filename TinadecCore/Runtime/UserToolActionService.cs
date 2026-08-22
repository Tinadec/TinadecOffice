using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
public sealed class UserToolActionService : IUserToolActionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<LifecycleDbContext> _dbFactory;
    private readonly ITenantContextAccessor _tenant;
    private readonly ISessionLocator _sessions;
    private readonly IToolRegistry _tools;
    private readonly IToolProvider _provider;
    private readonly IAuthorizationService _authorization;
    private readonly IWorkspaceSnapshotService _snapshots;
    private readonly IContentStore _content;
    private readonly INonceMaterialStore _nonceMaterials;

    public UserToolActionService(
        IDbContextFactory<LifecycleDbContext> dbFactory,
        ITenantContextAccessor tenant,
        ISessionLocator sessions,
        IToolRegistry tools,
        IToolProvider provider,
        IAuthorizationService authorization,
        IWorkspaceSnapshotService snapshots,
        IContentStore content,
        INonceMaterialStore nonceMaterials)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _sessions = sessions;
        _tools = tools;
        _provider = provider;
        _authorization = authorization;
        _snapshots = snapshots;
        _content = content;
        _nonceMaterials = nonceMaterials;
    }

    public async Task<UserToolActionResult> CreateAsync(UserToolActionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ProjectId == Guid.Empty) throw new ArgumentException("project_id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ToolId)) throw new ArgumentException("tool_id is required.", nameof(request));
        if (!TryNormalizeParameters(request.ParametersJson, out var parametersJson))
            throw new ArgumentException("params must be a JSON object or null.", nameof(request));

        var scope = _tenant.Current;
        var project = await _sessions.FindProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Project was not found.");
        EnsureProjectScope(project, scope);
        var descriptor = await _tools.FindToolAsync(request.ToolId.Trim(), project.RootPath, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Tool '{request.ToolId}' was not found.");
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
        var now = DateTimeOffset.UtcNow;
        var action = new UserToolActionRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            ProjectId = project.ProjectId, PrincipalId = scope.PrincipalId, ToolId = descriptor.Id,
            ParametersReference = stored.Value, ParametersLength = stored.Length, ParametersHash = parametersHash,
            Risk = NormalizeRisk(descriptor.Risk), MutatesWorkspace = descriptor.MutatesWorkspace,
            RequiresApproval = descriptor.RequiresApproval || descriptor.MutatesWorkspace,
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
                    project.ProjectId, $"user-action:{action.Id:N}:prewrite"), cancellationToken).ConfigureAwait(false);
                action.SnapshotId = snapshot.Id;
                action.SnapshotHash = snapshot.WorkspaceHash;
                action.Status = "requested";
                action.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                await BlockAsync(action, "snapshot_failed", SafeMessage(ex.Message), cancellationToken).ConfigureAwait(false);
                return await ToResultAsync(action, cancellationToken).ConfigureAwait(false);
            }
        }

        return await AuthorizeAndMaybeRunAsync(action, descriptor, project, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserToolActionResult?> GetAsync(Guid actionId, CancellationToken cancellationToken = default)
    {
        var action = await FindAsync(actionId, cancellationToken).ConfigureAwait(false);
        return action is null ? null : await ToResultAsync(action, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UserToolActionResult>> ListAsync(string? status = null, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.UserToolActions.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status.Trim().ToLowerInvariant());
        var rows = await query.ToArrayAsync(cancellationToken).ConfigureAwait(false);
        Array.Sort(rows, static (left, right) => right.CreatedAt.CompareTo(left.CreatedAt));
        if (rows.Length > 200) rows = rows[..200];
        var results = new List<UserToolActionResult>(rows.Length);
        foreach (var row in rows) results.Add(await ToResultAsync(row, cancellationToken).ConfigureAwait(false));
        return results;
    }

    public async Task<UserToolActionResult> ResumeAsync(Guid actionId, CancellationToken cancellationToken = default)
    {
        var action = await FindAsync(actionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("User tool action was not found.");
        if (IsTerminal(action.Status) || action.Status == UserToolActionStatuses.SnapshotRequired)
            return await ToResultAsync(action, cancellationToken).ConfigureAwait(false);
        var scope = _tenant.Current;
        var project = await _sessions.FindProjectAsync(action.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Project was not found.");
        EnsureProjectScope(project, scope);
        var descriptor = await _tools.FindToolAsync(action.ToolId, project.RootPath, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Tool '{action.ToolId}' was not found.");

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
            if (!await TryConsumeUserApprovalAsync(approval, action, cancellationToken).ConfigureAwait(false))
                return await BlockAsync(action, "approval_nonce_invalid", "The action approval could not be consumed.", cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteAsync(action, project, descriptor, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserToolActionResult> OverrideSnapshotAsync(Guid actionId, string reason, CancellationToken cancellationToken = default)
    {
        var action = await FindAsync(actionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("User tool action was not found.");
        var scope = _tenant.Current;
        if (action.PrincipalId != scope.PrincipalId) throw new UnauthorizedAccessException("Only the initiating user may override a snapshot failure.");
        if (!action.MutatesWorkspace || action.ErrorCategory != "snapshot_failed" || IsTerminal(action.Status) && action.Status != UserToolActionStatuses.Blocked)
            throw new InvalidOperationException("Only a blocked snapshot failure can be overridden.");
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A snapshot override reason is required.", nameof(reason));
        action.SnapshotOverride = true;
        action.SnapshotOverrideReason = SafeMessage(reason);
        action.ErrorCategory = "snapshot_override";
        action.SafeErrorMessage = "User explicitly accepted a non-reversible write without a workspace snapshot.";
        action.Status = "requested";
        action.CompletedAt = null;
        action.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(action, cancellationToken).ConfigureAwait(false);
        var project = await _sessions.FindProjectAsync(action.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Project was not found.");
        EnsureProjectScope(project, scope);
        var descriptor = await _tools.FindToolAsync(action.ToolId, project.RootPath, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Tool '{action.ToolId}' was not found.");
        return await AuthorizeAndMaybeRunAsync(action, descriptor, project, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UserToolActionResult> AuthorizeAndMaybeRunAsync(UserToolActionRecord action, ToolManifestEntryDto descriptor, ProjectReference project, CancellationToken cancellationToken)
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
                "allowed" => UserToolActionStatuses.Running,
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

    private async Task<UserToolActionResult> ExecuteAsync(UserToolActionRecord action, ProjectReference project, ToolManifestEntryDto descriptor, CancellationToken cancellationToken)
    {
        var parameters = await ReadContentAsync(action.ParametersReference, action.ParametersHash, action.ParametersLength, "application/json", cancellationToken).ConfigureAwait(false);
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
        action.Status = UserToolActionStatuses.Running;
        action.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(action, cancellationToken).ConfigureAwait(false);
        ToolWireResponseDto response;
        try
        {
            using var document = JsonDocument.Parse(parameters);
            response = await _provider.CallAsync(project.RootPath, new ToolWireRequestDto
            { ToolId = descriptor.Id, SessionId = $"user-action:{action.Id:N}", Approved = true, Params = document.RootElement.Clone() },
                TimeSpan.FromSeconds(120), cancellationToken).ConfigureAwait(false);
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
        var row = new ApprovalRequestRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, ProjectId = action.ProjectId,
            UserToolActionId = action.Id, Kind = "user_tool", ToolId = action.ToolId, Risk = action.Risk,
            RequestHash = action.ParametersHash, NonceHash = ToolParametersHash.Compute(nonce),
            NonceSecretReference = $"approval_nonce_{scope.TenantId:N}_{Guid.NewGuid():N}", ParametersReference = action.ParametersReference,
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
        var nonce = await _nonceMaterials.GetAsync(approval.NonceSecretReference ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(nonce) || !FixedEquals(approval.NonceHash, ToolParametersHash.Compute(nonce))) return false;
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var updated = await db.ApprovalRequests.Where(x => x.Id == approval.Id && x.UserToolActionId == action.Id
            && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status == "approved"
            && x.ConsumedByExecutionId == null && x.RequestHash == action.ParametersHash)
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
        return new UserToolActionResult(action.Id, action.TenantId, action.WorkspaceId, action.ProjectId, action.PrincipalId,
            action.ToolId, action.Status, action.Risk, action.MutatesWorkspace, action.RequiresApproval,
            action.PermissionRequestId, action.AuthorizationDecisionId, action.ActionApprovalId, action.SnapshotId,
            action.SnapshotHash, result, action.ErrorCategory, action.SafeErrorMessage, action.CreatedAt, action.UpdatedAt, action.CompletedAt);
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

    private static bool NeedsPrewriteSnapshot(ToolManifestEntryDto descriptor) => descriptor.MutatesWorkspace && RiskRank(descriptor.Risk) >= 2;
    private static bool IsTerminal(string status) => status is UserToolActionStatuses.Completed or UserToolActionStatuses.Blocked or UserToolActionStatuses.OutcomeUnknown or "failed";
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
}
