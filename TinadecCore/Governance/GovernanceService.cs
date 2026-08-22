using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Governance;

public sealed class GovernanceService : IAuthorizationService, IPolicyDecisionPoint, IPolicySnapshotProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromHours(24);

    private readonly IDbContextFactory<GovernanceDbContext> _dbFactory;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IAuthorizationContextResolver _authorizationContextResolver;
    private readonly TimeProvider _timeProvider;

    public GovernanceService(
        IDbContextFactory<GovernanceDbContext> dbFactory,
        ITenantContextAccessor tenantAccessor,
        IAuthorizationContextResolver authorizationContextResolver,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _tenantAccessor = tenantAccessor;
        _authorizationContextResolver = authorizationContextResolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Captures the currently published policy versions for run admission.  The
    /// returned hash and bundle content are persisted with the run; authorization
    /// evaluations for that run can therefore ignore later policy mutations.
    /// </summary>
    public async Task<FrozenPolicySnapshot> CaptureAsync(
        Guid tenantId,
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || workspaceId == Guid.Empty)
            return new FrozenPolicySnapshot(CapabilityRuleMatcher.HashText("[]"), []);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var bundles = await db.PolicyBundles.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                && x.Status == "active"
                && ((x.ScopeKind == "tenant" && x.ScopeId == tenantId)
                    || (x.ScopeKind == "workspace" && x.ScopeId == workspaceId)))
            .OrderBy(x => x.ScopeKind)
            .ThenBy(x => x.Slug)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var versionIds = bundles.Select(x => x.CurrentVersionId).ToArray();
        var versions = versionIds.Length == 0
            ? []
            : await db.PolicyVersions.AsNoTracking()
                .Where(x => versionIds.Contains(x.Id) && x.Status == "published")
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var byId = versions.ToDictionary(x => x.Id);
        var snapshot = bundles
            .Where(bundle => byId.ContainsKey(bundle.CurrentVersionId))
            .Select(bundle =>
            {
                var version = byId[bundle.CurrentVersionId];
                return new FrozenPolicyBundle(
                    bundle.Id,
                    version.Id,
                    bundle.Slug,
                    version.Version,
                    version.ContentHash,
                    CapabilityRuleMatcher.DeserializeRules(version.RulesJson));
            })
            .ToArray();
        var canonical = JsonSerializer.Serialize(snapshot, JsonOptions);
        return new FrozenPolicySnapshot(CapabilityRuleMatcher.HashText(canonical), snapshot);
    }

    public async Task<PolicyBundleSnapshot> CreatePolicyBundleAsync(
        CreatePolicyBundleCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var scope = _tenantAccessor.Current;
        if (command.TenantWide && !CanHumanApprove(scope))
            throw new UnauthorizedAccessException("Only a tenant owner or administrator may create a tenant-wide policy bundle.");
        var now = UtcNow;
        var slug = NormalizeSlug(command.Slug);
        var displayName = CapabilityRuleMatcher.Required(command.DisplayName, nameof(command.DisplayName), 256);
        var rules = CapabilityRuleMatcher.NormalizeRules(command.Rules);
        var rulesJson = CapabilityRuleMatcher.SerializeRules(rules);
        var rulesHash = CapabilityRuleMatcher.HashRules(rules);
        var scopeKind = command.TenantWide ? "tenant" : "workspace";
        var scopeId = command.TenantWide ? scope.TenantId : scope.WorkspaceId;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var duplicate = await db.PolicyBundles.AsNoTracking().AnyAsync(x =>
            x.TenantId == scope.TenantId && x.ScopeKind == scopeKind && x.ScopeId == scopeId
            && x.Slug == slug && x.Status != "archived", cancellationToken).ConfigureAwait(false);
        if (duplicate) throw new InvalidOperationException($"Policy bundle '{slug}' already exists in this scope.");

        var versionId = Guid.NewGuid();
        var bundle = new PolicyBundleRecord
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            ScopeKind = scopeKind,
            ScopeId = scopeId,
            Slug = slug,
            DisplayName = displayName,
            Status = "active",
            Revision = 1,
            CurrentVersionId = versionId,
            CreatedByPrincipalId = scope.PrincipalId,
            UpdatedByPrincipalId = scope.PrincipalId,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.PolicyBundles.Add(bundle);
        db.PolicyVersions.Add(new PolicyVersionRecord
        {
            Id = versionId,
            TenantId = scope.TenantId,
            ScopeKind = scopeKind,
            ScopeId = scopeId,
            PolicyBundleId = bundle.Id,
            Version = 1,
            RulesJson = rulesJson,
            ContentHash = rulesHash,
            Status = "published",
            CreatedByPrincipalId = scope.PrincipalId,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToSnapshot(bundle);
    }

    public async Task<PolicyVersionSnapshot> PublishPolicyVersionAsync(
        PublishPolicyVersionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var scope = _tenantAccessor.Current;
        var now = UtcNow;
        var rules = CapabilityRuleMatcher.NormalizeRules(command.Rules);
        var rulesJson = CapabilityRuleMatcher.SerializeRules(rules);
        var rulesHash = CapabilityRuleMatcher.HashRules(rules);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var bundle = await FindPolicyBundleAsync(db, command.PolicyBundleId, scope, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Policy bundle was not found.");
        if (bundle.Status != "active") throw new InvalidOperationException("Only an active policy bundle can be published.");
        var nextVersion = await db.PolicyVersions
            .Where(x => x.PolicyBundleId == bundle.Id)
            .MaxAsync(x => (int?)x.Version, cancellationToken).ConfigureAwait(false) is { } current
            ? current + 1
            : 1;
        var version = new PolicyVersionRecord
        {
            Id = Guid.NewGuid(),
            TenantId = bundle.TenantId,
            ScopeKind = bundle.ScopeKind,
            ScopeId = bundle.ScopeId,
            PolicyBundleId = bundle.Id,
            Version = nextVersion,
            RulesJson = rulesJson,
            ContentHash = rulesHash,
            Status = "published",
            CreatedByPrincipalId = scope.PrincipalId,
            CreatedAt = now
        };
        db.PolicyVersions.Add(version);
        bundle.CurrentVersionId = version.Id;
        bundle.Revision++;
        bundle.UpdatedAt = now;
        bundle.UpdatedByPrincipalId = scope.PrincipalId;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToSnapshot(version);
    }

    public async Task<CapabilityGrantSnapshot> GrantCapabilityAsync(
        GrantCapabilityCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.SubjectPrincipalId == Guid.Empty) throw new ArgumentException("Subject principal id is required.", nameof(command));
        if (!CanHumanApprove(_tenantAccessor.Current))
            throw new UnauthorizedAccessException("Only a tenant owner or administrator may issue a capability grant.");
        ValidateUses(command.MaxUses);
        var scope = _tenantAccessor.Current;
        var claim = CapabilityRuleMatcher.NormalizeScopeClaim(command.Claim);
        var now = UtcNow;
        ValidateExpiry(now, command.ExpiresAt);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        CapabilityGrantRecord? parent = null;
        if (command.ParentGrantId is { } parentGrantId)
        {
            parent = await db.CapabilityGrants.SingleOrDefaultAsync(x =>
                x.Id == parentGrantId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Parent capability grant is outside the current scope or does not exist.");
            ValidateParentGrant(parent, command, claim, now);
            var reserved = await ReserveGrantUsesAsync(db, parent.Id, command.MaxUses, now, cancellationToken).ConfigureAwait(false);
            if (!reserved) throw new InvalidOperationException("Parent capability grant is expired, revoked, or exhausted.");
        }

        var grant = NewGrant(
            scope,
            command.SubjectPrincipalId,
            command.SubjectAgentInstanceId,
            claim,
            command.RunId,
            command.TaskId,
            command.ParentGrantId,
            sourceDelegationId: null,
            transferable: command.Transferable && (parent?.Transferable ?? true),
            command.MaxUses,
            now,
            command.ExpiresAt,
            scope.PrincipalId,
            issuedByAgentInstanceId: null,
            command.Reason);
        db.CapabilityGrants.Add(grant);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToSnapshot(grant);
    }

    public async Task<ApprovalDelegationSnapshot> CreateApprovalDelegationAsync(
        CreateApprovalDelegationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.DelegateAgentInstanceId == Guid.Empty) throw new ArgumentException("Delegate agent instance id is required.", nameof(command));
        if (command.DelegateAgentVersionId == Guid.Empty) throw new ArgumentException("Delegate agent version id is required.", nameof(command));
        ValidateUses(command.MaxUses);
        if (command.MaxCost < 0) throw new ArgumentOutOfRangeException(nameof(command), "Maximum cost cannot be negative.");
        var risk = NormalizeRisk(command.MaxRisk);
        var rules = CapabilityRuleMatcher.NormalizeRules(command.Rules);
        var now = UtcNow;
        ValidateExpiry(now, command.ExpiresAt);
        var scope = _tenantAccessor.Current;
        if (!CanHumanApprove(scope))
            throw new UnauthorizedAccessException("Only a tenant owner or administrator may create an approval delegation.");
        var resolvedVersionId = await _authorizationContextResolver.ResolveAgentVersionIdAsync(
            scope.TenantId, scope.WorkspaceId, command.DelegateAgentInstanceId, cancellationToken).ConfigureAwait(false);
        if (resolvedVersionId != command.DelegateAgentVersionId)
            throw new InvalidOperationException("Delegate agent instance is not bound to the requested immutable agent version.");
        var record = new ApprovalDelegationRecord
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            DelegatedByPrincipalId = scope.PrincipalId,
            DelegateAgentVersionId = command.DelegateAgentVersionId,
            DelegateAgentInstanceId = command.DelegateAgentInstanceId,
            RulesJson = CapabilityRuleMatcher.SerializeRules(rules),
            RulesHash = CapabilityRuleMatcher.HashRules(rules),
            MaxRisk = risk,
            MaxCost = command.MaxCost,
            RunId = command.RunId,
            RequireUserReview = command.RequireUserReview,
            Status = "active",
            MaxUses = command.MaxUses,
            UseCount = 0,
            Revision = 1,
            StartsAt = now,
            ExpiresAt = command.ExpiresAt,
            StartsAtUnixMilliseconds = now.ToUnixTimeMilliseconds(),
            ExpiresAtUnixMilliseconds = command.ExpiresAt.ToUnixTimeMilliseconds(),
            CreatedAt = now,
            UpdatedAt = now
        };
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.ApprovalDelegations.Add(record);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToSnapshot(record);
    }

    public async Task<AuthorizationDecisionSnapshot> EvaluateAsync(
        AuthorizationEvaluationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SubjectPrincipalId == Guid.Empty) throw new ArgumentException("Subject principal id is required.", nameof(request));
        var claim = CapabilityRuleMatcher.NormalizeClaim(request.Claim);
        var scope = _tenantAccessor.Current;
        var now = UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var resolvedBoundaries = await _authorizationContextResolver.ResolveBoundariesAsync(new AuthorizationContextRequest(
            scope.TenantId, scope.WorkspaceId, request.SubjectPrincipalId, request.SubjectAgentInstanceId,
            claim, request.RunId, request.TaskId), cancellationToken).ConfigureAwait(false);
        var boundaries = await LoadEffectiveBoundariesAsync(db, resolvedBoundaries, scope, cancellationToken).ConfigureAwait(false);
        var boundaryResult = EvaluateBoundaries(boundaries, claim);
        if (!boundaryResult.Allowed)
        {
            return await PersistDecisionAsync(db, NewDecision(scope, request.SubjectPrincipalId, request.SubjectAgentInstanceId,
                claim, GovernanceOutcomes.Denied, boundaryResult.ReasonCode, boundaryResult.Reason, "pdp",
                boundaryResult.Hash, request.RunId, request.TaskId), cancellationToken).ConfigureAwait(false);
        }

        if (request.CapabilityLeaseId is not { } leaseId)
        {
            return await PersistDecisionAsync(db, NewDecision(scope, request.SubjectPrincipalId, request.SubjectAgentInstanceId,
                claim, GovernanceOutcomes.PermissionRequired, "capability_lease_required",
                "A matching active capability lease is required.", "pdp", boundaryResult.Hash,
                request.RunId, request.TaskId), cancellationToken).ConfigureAwait(false);
        }

        var lease = await db.CapabilityLeases.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == leaseId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
            cancellationToken).ConfigureAwait(false);
        var leaseFailure = ValidateLease(lease, request.SubjectPrincipalId, request.SubjectAgentInstanceId, claim,
            request.RunId, request.TaskId, now);
        return await PersistDecisionAsync(db, NewDecision(scope, request.SubjectPrincipalId, request.SubjectAgentInstanceId,
            claim, leaseFailure is null ? GovernanceOutcomes.Allowed : GovernanceOutcomes.Denied,
            leaseFailure?.Code ?? "active_capability_lease", leaseFailure?.Reason ?? "The capability lease permits this action.",
            "pdp", boundaryResult.Hash, request.RunId, request.TaskId, capabilityLeaseId: lease?.Id,
            capabilityGrantId: lease?.CapabilityGrantId), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ToolAuthorizationResult> AuthorizeToolAsync(
        ToolAuthorizationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var claim = CapabilityRuleMatcher.NormalizeClaim(command.Claim);
        var resolution = await RequestPermissionAsync(new PermissionRequestCommand(
            command.SubjectPrincipalId,
            command.SubjectAgentInstanceId,
            ParentAgentInstanceId: null,
            claim,
            command.RunId,
            command.TaskId,
            command.RequestedDuration,
            command.RequestedUses,
            command.Risk,
            command.ExpectedCost,
            command.Rationale,
            command.IdempotencyKey), cancellationToken).ConfigureAwait(false);
        var status = resolution.Decision.Outcome switch
        {
            GovernanceOutcomes.Allowed => "allowed",
            GovernanceOutcomes.PermissionRequired => "awaiting_delegate",
            GovernanceOutcomes.UserRequired => "awaiting_user",
            _ => "blocked"
        };
        return new ToolAuthorizationResult(status, resolution.Decision, resolution.Request,
            resolution.Lease?.Id, resolution.Lease?.Nonce);
    }

    public async Task<ToolAuthorizationResult> ConsumeToolLeaseAsync(
        ToolLeaseConsumptionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        // This is a Core-internal consumption path. The public lease snapshot never
        // carries the nonce; binding the lease to the full execution claim and the
        // idempotency key keeps dispatch replay-safe without exposing the secret.
        var consumed = await ConsumeLeaseAsync(new LeaseConsumptionCommand(
            command.CapabilityLeaseId,
            command.Nonce,
            command.SubjectPrincipalId,
            command.SubjectAgentInstanceId,
            command.Claim,
            command.RunId,
            command.TaskId,
            command.IdempotencyKey), requireNonce: true, cancellationToken).ConfigureAwait(false);
        return new ToolAuthorizationResult(
            consumed.Consumed ? "allowed" : "blocked",
            consumed.Decision,
            PermissionRequest: null,
            consumed.Lease?.Id, consumed.Lease?.Nonce);
    }

    public async Task<PermissionResolution> RequestPermissionAsync(
        PermissionRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.SubjectPrincipalId == Guid.Empty) throw new ArgumentException("Subject principal id is required.", nameof(command));
        if (command.RequestedDuration <= TimeSpan.Zero || command.RequestedDuration > MaximumLeaseDuration)
            throw new ArgumentOutOfRangeException(nameof(command), $"Requested duration must be positive and no longer than {MaximumLeaseDuration}.");
        ValidateUses(command.RequestedUses);
        if (command.ExpectedCost < 0) throw new ArgumentOutOfRangeException(nameof(command), "Expected cost cannot be negative.");
        var risk = NormalizeRisk(command.Risk);
        var claim = CapabilityRuleMatcher.NormalizeClaim(command.Claim);
        var scope = _tenantAccessor.Current;
        var now = UtcNow;
        var expiresAt = now.Add(command.RequestedDuration);
        var idempotencyKey = CapabilityRuleMatcher.Required(command.IdempotencyKey, nameof(command.IdempotencyKey), 256);
        var boundaries = NormalizeBoundaries(await _authorizationContextResolver.ResolveBoundariesAsync(new AuthorizationContextRequest(
            scope.TenantId, scope.WorkspaceId, command.SubjectPrincipalId, command.SubjectAgentInstanceId,
            claim, command.RunId, command.TaskId), cancellationToken).ConfigureAwait(false));
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // Resolve and hash the effective boundaries before the idempotency lookup.
        // A replay must compare the complete request, including the policy
        // snapshot, without referring to a local value declared later in the
        // transaction setup.
        var effectiveBoundaries = await LoadEffectiveBoundariesAsync(db, boundaries, scope, cancellationToken).ConfigureAwait(false);
        var boundaryResult = EvaluateBoundaries(effectiveBoundaries, claim);
        var requestHash = CapabilityRuleMatcher.HashText(JsonSerializer.Serialize(new
        {
            command.SubjectPrincipalId,
            command.SubjectAgentInstanceId,
            command.ParentAgentInstanceId,
            Claim = claim,
            Boundaries = effectiveBoundaries,
            command.RunId,
            command.TaskId,
            DurationMilliseconds = (long)command.RequestedDuration.TotalMilliseconds,
            command.RequestedUses,
            Risk = risk,
            command.ExpectedCost,
            Rationale = command.Rationale?.Trim() ?? string.Empty
        }, JsonOptions));
        var existingRequest = await db.PermissionRequests.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.IdempotencyKey == idempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (existingRequest is not null)
        {
            if (!FixedTimeEquals(existingRequest.RequestHash, requestHash))
                throw new InvalidOperationException("Permission request idempotency key was reused with different input.");
            return await LoadResolutionAsync(db, existingRequest, cancellationToken).ConfigureAwait(false);
        }
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        AuthorizationDecisionRecord resolutionDecision;
        CapabilityGrantRecord? resolutionGrant = null;
        CapabilityLeaseRecord? resolutionLease = null;
        var issuedNonce = string.Empty;
        var record = new PermissionRequestRecord
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            SubjectPrincipalId = command.SubjectPrincipalId,
            SubjectAgentInstanceId = command.SubjectAgentInstanceId,
            ParentAgentInstanceId = command.ParentAgentInstanceId,
            Capability = claim.Capability,
            Action = claim.Action,
            Resource = claim.Resource,
            // Persist the effective boundaries, including the frozen policy
            // versions, so a later decision or lease consumption never reloads
            // policy published after this request was admitted.
            BoundariesJson = CapabilityRuleMatcher.SerializeBoundaries(effectiveBoundaries),
            RunId = command.RunId,
            TaskId = command.TaskId,
            Risk = risk,
            ExpectedCost = command.ExpectedCost,
            Rationale = Truncate(command.Rationale, 4096),
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            RequestedUses = command.RequestedUses,
            PolicySnapshotHash = boundaryResult.Hash,
            Status = PermissionRequestStatuses.Evaluating,
            Revision = 1,
            ExpiresAt = expiresAt,
            ExpiresAtUnixMilliseconds = expiresAt.ToUnixTimeMilliseconds(),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.PermissionRequests.Add(record);

        if (!boundaryResult.Allowed)
        {
            record.Status = PermissionRequestStatuses.Denied;
            var denied = NewDecision(scope, command.SubjectPrincipalId, command.SubjectAgentInstanceId, claim,
                GovernanceOutcomes.Denied, boundaryResult.ReasonCode, boundaryResult.Reason, "pdp", boundaryResult.Hash,
                command.RunId, command.TaskId, permissionRequestId: record.Id);
            db.AuthorizationDecisions.Add(denied);
            record.AuthorizationDecisionId = denied.Id;
            resolutionDecision = denied;
        }
        else
        {
            var grant = await FindMatchingGrantAsync(db, scope, command.SubjectPrincipalId, command.SubjectAgentInstanceId,
                claim, command.RunId, command.TaskId, command.RequestedUses, now, cancellationToken).ConfigureAwait(false);
            if (grant is not null && await ReserveGrantUsesAsync(db, grant.Id, command.RequestedUses, now, cancellationToken).ConfigureAwait(false))
            {
                grant.UseCount += command.RequestedUses;
                if (grant.UseCount >= grant.MaxUses) grant.Status = "exhausted";
                // A lease cannot outlive the grant that issued it. This matters
                // when a caller requests a duration longer than the remaining
                // grant TTL: the source grant remains the hard upper bound.
                var leaseExpiresAt = grant.ExpiresAt < expiresAt ? grant.ExpiresAt : expiresAt;
                var (lease, nonce) = NewLease(scope, grant, record.Id, claim, command.RunId, command.TaskId,
                    command.RequestedUses, now, leaseExpiresAt, boundaryResult.Hash);
                db.CapabilityLeases.Add(lease);
                record.Status = PermissionRequestStatuses.Granted;
                record.CapabilityGrantId = grant.Id;
                record.CapabilityLeaseId = lease.Id;
                var allowed = NewDecision(scope, command.SubjectPrincipalId, command.SubjectAgentInstanceId, claim,
                    GovernanceOutcomes.Allowed, "existing_grant", "An existing grant issued a scoped capability lease.",
                    "pdp", boundaryResult.Hash, command.RunId, command.TaskId, record.Id, grant.Id, lease.Id);
                db.AuthorizationDecisions.Add(allowed);
                record.AuthorizationDecisionId = allowed.Id;
                resolutionDecision = allowed;
                resolutionGrant = grant;
                resolutionLease = lease;
                issuedNonce = nonce;
            }
            else
            {
                var delegated = await HasEligibleDelegationAsync(db, scope, claim, command.RunId, risk,
                    command.ExpectedCost, now, cancellationToken).ConfigureAwait(false);
                record.Status = delegated ? PermissionRequestStatuses.AwaitingDelegate : PermissionRequestStatuses.AwaitingUser;
                var decision = NewDecision(scope, command.SubjectPrincipalId, command.SubjectAgentInstanceId, claim,
                    delegated ? GovernanceOutcomes.PermissionRequired : GovernanceOutcomes.UserRequired,
                    delegated ? "delegated_approval_available" : "user_approval_required",
                    delegated ? "An active delegation may decide this request." : "No eligible delegation can decide this request.",
                    "pdp", boundaryResult.Hash, command.RunId, command.TaskId, permissionRequestId: record.Id);
                db.AuthorizationDecisions.Add(decision);
                record.AuthorizationDecisionId = decision.Id;
                resolutionDecision = decision;
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PermissionResolution(ToSnapshot(record), ToSnapshot(resolutionDecision),
                resolutionGrant is null ? null : ToSnapshot(resolutionGrant),
                resolutionLease is null ? null : ToSnapshot(resolutionLease, issuedNonce));
        }
        catch (DbUpdateException)
        {
            // A concurrent admission may win the unique tenant/workspace/key
            // constraint after the initial lookup. Re-read that durable winner so
            // retries remain idempotent instead of surfacing a transient 500.
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await using var retryDb = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var winner = await retryDb.PermissionRequests.AsNoTracking().SingleOrDefaultAsync(x =>
                x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                && x.IdempotencyKey == idempotencyKey, cancellationToken).ConfigureAwait(false);
            if (winner is null) throw;
            if (!FixedTimeEquals(winner.RequestHash, requestHash))
                throw new InvalidOperationException("Permission request idempotency key was reused with different input.");
            return await LoadResolutionAsync(retryDb, winner, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<PermissionResolution> DecidePermissionAsync(
        PermissionDecisionCommand command,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await DecidePermissionCoreAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another approver won the request revision race. The durable winner
            // is the only authorization fact callers should observe; re-read it
            // from a fresh context instead of exposing a transient 500 or issuing
            // a second grant/lease.
            return await ReadConcurrentDecisionAsync(command.PermissionRequestId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PermissionResolution> DecidePermissionCoreAsync(
        PermissionDecisionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var scope = _tenantAccessor.Current;
        var now = UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var request = await db.PermissionRequests.SingleOrDefaultAsync(x =>
            x.Id == command.PermissionRequestId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Permission request was not found.");

        if (IsTerminalRequest(request.Status))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return await LoadResolutionAsync(db, request, cancellationToken).ConfigureAwait(false);
        }

        var claim = ClaimOf(request);
        if (request.ExpiresAtUnixMilliseconds <= now.ToUnixTimeMilliseconds())
        {
            request.Status = PermissionRequestStatuses.Expired;
            request.Revision++;
            request.UpdatedAt = now;
            var expired = NewDecision(scope, request.SubjectPrincipalId, request.SubjectAgentInstanceId, claim,
                GovernanceOutcomes.Denied, "permission_request_expired", "The permission request has expired.",
                "pdp", request.PolicySnapshotHash, request.RunId, request.TaskId, request.Id);
            db.AuthorizationDecisions.Add(expired);
            request.AuthorizationDecisionId = expired.Id;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PermissionResolution(ToSnapshot(request), ToSnapshot(expired), null, null);
        }

        var selfOrRelated = request.SubjectAgentInstanceId is { } requesterAgentId
            && command.ApproverAgentInstanceId is { } approverAgentId
            && await _authorizationContextResolver.IsSelfOrDescendantAsync(scope.TenantId, scope.WorkspaceId,
                requesterAgentId, approverAgentId, cancellationToken).ConfigureAwait(false);
        if (selfOrRelated || (request.SubjectAgentInstanceId is null && command.ApproverAgentInstanceId is null
                && request.SubjectPrincipalId == scope.PrincipalId))
        {
            return await DenyRequestAsync(db, transaction, request, scope, claim, "self_approval_forbidden",
                "The requester and approver cannot be the same agent or belong to the same ancestor chain.",
                command.ApproverAgentInstanceId, command.ApprovalDelegationId, now, cancellationToken).ConfigureAwait(false);
        }

        ApprovalDelegationRecord? delegation = null;
        if (command.ApprovalDelegationId is { } delegationId)
        {
            delegation = await db.ApprovalDelegations.SingleOrDefaultAsync(x =>
                x.Id == delegationId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
                cancellationToken).ConfigureAwait(false);
            var delegationFailure = ValidateDelegation(delegation, command.ApproverAgentInstanceId, request, claim, now);
            if (delegationFailure is not null)
            {
                return await DenyRequestAsync(db, transaction, request, scope, claim, delegationFailure.Value.Code,
                    delegationFailure.Value.Reason, command.ApproverAgentInstanceId, delegationId, now, cancellationToken).ConfigureAwait(false);
            }

            if (delegation!.RequireUserReview)
            {
                request.Status = PermissionRequestStatuses.AwaitingUser;
                request.Revision++;
                request.UpdatedAt = now;
                request.EscalationChainJson = AppendEscalation(request.EscalationChainJson, "delegation_requires_user", now);
                var escalated = NewDecision(scope, request.SubjectPrincipalId, request.SubjectAgentInstanceId, claim,
                    GovernanceOutcomes.UserRequired, "delegation_requires_user", "The delegation requires user review.",
                    "delegation", request.PolicySnapshotHash, request.RunId, request.TaskId, request.Id,
                    decidedByAgentInstanceId: command.ApproverAgentInstanceId, approvalDelegationId: delegation.Id);
                db.AuthorizationDecisions.Add(escalated);
                request.AuthorizationDecisionId = escalated.Id;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new PermissionResolution(ToSnapshot(request), ToSnapshot(escalated), null, null);
            }
        }
        else if (command.ApproverAgentInstanceId is not null || !CanHumanApprove(scope))
        {
            return await DenyRequestAsync(db, transaction, request, scope, claim, "unauthorized_approver",
                "The approver has no valid user authority or approval delegation.", command.ApproverAgentInstanceId,
                null, now, cancellationToken).ConfigureAwait(false);
        }

        var storedBoundaries = CapabilityRuleMatcher.DeserializeBoundaries(request.BoundariesJson);
        var boundaryResult = EvaluateBoundaries(storedBoundaries, claim);
        if (!boundaryResult.Allowed)
        {
            return await DenyRequestAsync(db, transaction, request, scope, claim, boundaryResult.ReasonCode,
                boundaryResult.Reason, command.ApproverAgentInstanceId, command.ApprovalDelegationId, now,
                cancellationToken, boundaryResult.Hash).ConfigureAwait(false);
        }

        if (!command.Approve)
        {
            return await DenyRequestAsync(db, transaction, request, scope, claim, "approver_rejected",
                Truncate(command.Reason, 4096, "The approver rejected the request."), command.ApproverAgentInstanceId,
                command.ApprovalDelegationId, now, cancellationToken, boundaryResult.Hash).ConfigureAwait(false);
        }

        if (delegation is not null)
        {
            var consumed = await ReserveDelegationUseAsync(db, delegation.Id, request.Id, scope, now, cancellationToken).ConfigureAwait(false);
            if (!consumed)
            {
                return await DenyRequestAsync(db, transaction, request, scope, claim, "delegation_unavailable",
                    "The approval delegation is expired, revoked, or exhausted.", command.ApproverAgentInstanceId,
                    delegation.Id, now, cancellationToken, boundaryResult.Hash).ConfigureAwait(false);
            }
        }

        var delegatedExpiresAt = delegation is not null && delegation.ExpiresAt < request.ExpiresAt
            ? delegation.ExpiresAt
            : request.ExpiresAt;
        var grant = NewGrant(scope, request.SubjectPrincipalId, request.SubjectAgentInstanceId, claim,
            request.RunId, request.TaskId, parentGrantId: null, sourceDelegationId: delegation?.Id,
            transferable: false, request.RequestedUses, now, delegatedExpiresAt, scope.PrincipalId,
            command.ApproverAgentInstanceId, command.Reason);
        // The lease reserves every use from this request-scoped grant. The lease remains
        // usable while the grant record becomes exhausted and cannot mint another lease.
        grant.UseCount = grant.MaxUses;
        grant.Status = "exhausted";
        var (lease, nonce) = NewLease(scope, grant, request.Id, claim, request.RunId, request.TaskId,
            request.RequestedUses, now, delegatedExpiresAt, boundaryResult.Hash);
        db.CapabilityGrants.Add(grant);
        db.CapabilityLeases.Add(lease);
        request.Status = PermissionRequestStatuses.Granted;
        request.Revision++;
        request.CapabilityGrantId = grant.Id;
        request.CapabilityLeaseId = lease.Id;
        request.UpdatedAt = now;
        var allowedDecision = NewDecision(scope, request.SubjectPrincipalId, request.SubjectAgentInstanceId, claim,
            GovernanceOutcomes.Allowed, delegation is null ? "user_approved" : "delegated_approval",
            Truncate(command.Reason, 4096, "Permission approved."), delegation is null ? "user" : "delegation",
            boundaryResult.Hash, request.RunId, request.TaskId, request.Id, grant.Id, lease.Id,
            command.ApproverAgentInstanceId, delegation?.Id);
        db.AuthorizationDecisions.Add(allowedDecision);
        request.AuthorizationDecisionId = allowedDecision.Id;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PermissionResolution(ToSnapshot(request), ToSnapshot(allowedDecision), ToSnapshot(grant), ToSnapshot(lease, nonce));
    }

    private async Task<PermissionResolution> ReadConcurrentDecisionAsync(
        Guid permissionRequestId,
        CancellationToken cancellationToken)
    {
        var scope = _tenantAccessor.Current;
        // A competing transaction may still be committing when the concurrency
        // exception is observed. Short bounded retries keep the endpoint
        // idempotent without holding the failed transaction open.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var request = await db.PermissionRequests.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == permissionRequestId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
                cancellationToken).ConfigureAwait(false);
            if (request is not null && request.AuthorizationDecisionId is not null)
                return await LoadResolutionAsync(db, request, cancellationToken).ConfigureAwait(false);

            if (attempt < 2)
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }

        throw new DbUpdateConcurrencyException("The permission request changed concurrently and its final decision is not yet visible.");
    }

    public Task<LeaseConsumptionResult> TryConsumeLeaseAsync(
        LeaseConsumptionCommand command,
        CancellationToken cancellationToken = default) =>
        ConsumeLeaseAsync(command, requireNonce: true, cancellationToken);

    public async Task<PermissionResolution?> GetPermissionRequestAsync(
        Guid permissionRequestId,
        CancellationToken cancellationToken = default)
    {
        if (permissionRequestId == Guid.Empty) return null;
        var scope = _tenantAccessor.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var request = await db.PermissionRequests.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == permissionRequestId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
            cancellationToken).ConfigureAwait(false);
        return request is null ? null : await LoadResolutionAsync(db, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PermissionRequestSnapshot>> ListPermissionRequestsAsync(
        string? status = null,
        Guid? runId = null,
        Guid? taskId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenantAccessor.Current;
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.PermissionRequests.AsNoTracking().Where(x =>
            x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId);
        if (normalizedStatus is not null) query = query.Where(x => x.Status == normalizedStatus);
        if (runId is not null) query = query.Where(x => x.RunId == runId);
        if (taskId is not null) query = query.Where(x => x.TaskId == taskId);
        // SQLite cannot translate DateTimeOffset ordering. Keep filtering
        // server-side and apply the bounded deterministic order in memory.
        var rows = await query.ToArrayAsync(cancellationToken).ConfigureAwait(false);
        Array.Sort(rows, static (left, right) => right.CreatedAt.CompareTo(left.CreatedAt));
        if (rows.Length > 200) rows = rows[..200];
        return rows.Select(ToSnapshot).ToArray();
    }

    private async Task<LeaseConsumptionResult> ConsumeLeaseAsync(
        LeaseConsumptionCommand command,
        bool requireNonce,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.SubjectPrincipalId == Guid.Empty) throw new ArgumentException("Subject principal id is required.", nameof(command));
        var idempotencyKey = CapabilityRuleMatcher.Required(command.IdempotencyKey, nameof(command.IdempotencyKey), 256);
        var nonceHash = string.IsNullOrWhiteSpace(command.Nonce) ? null : CapabilityRuleMatcher.HashNonce(command.Nonce);
        var claim = CapabilityRuleMatcher.NormalizeClaim(command.Claim);
        var inputHash = CapabilityRuleMatcher.HashText(JsonSerializer.Serialize(new
        {
            command.CapabilityLeaseId,
            command.SubjectPrincipalId,
            command.SubjectAgentInstanceId,
            Claim = claim,
            command.RunId,
            command.TaskId,
            NonceHash = nonceHash ?? string.Empty
        }, JsonOptions));
        var scope = _tenantAccessor.Current;
        var now = UtcNow;
        var nowUnix = now.ToUnixTimeMilliseconds();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var priorReceipt = await db.LeaseConsumptions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
            && x.CapabilityLeaseId == command.CapabilityLeaseId && x.IdempotencyKey == idempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (priorReceipt is not null)
        {
            if (!FixedTimeEquals(priorReceipt.InputHash, inputHash))
                throw new InvalidOperationException("Lease-consumption idempotency key was reused with different input.");
            if (priorReceipt.AuthorizationDecisionId is not { } priorDecisionId)
                throw new InvalidOperationException("A prior lease consumption is incomplete and cannot be replayed.");
            var priorDecision = await db.AuthorizationDecisions.AsNoTracking().SingleAsync(x =>
                x.Id == priorDecisionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
                cancellationToken).ConfigureAwait(false);
            var priorLease = await db.CapabilityLeases.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == command.CapabilityLeaseId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
                cancellationToken).ConfigureAwait(false);
            return new LeaseConsumptionResult(priorDecision.Outcome == GovernanceOutcomes.Allowed,
                priorDecision.ReasonCode, priorLease is null ? null : ToSnapshot(priorLease), ToSnapshot(priorDecision));
        }
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var receipt = new LeaseConsumptionRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            CapabilityLeaseId = command.CapabilityLeaseId, IdempotencyKey = idempotencyKey,
            InputHash = inputHash, Status = "pending", CreatedAt = now, UpdatedAt = now
        };
        db.LeaseConsumptions.Add(receipt);
        var lease = await db.CapabilityLeases.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == command.CapabilityLeaseId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
            cancellationToken).ConfigureAwait(false);
        PermissionRequestRecord? leaseRequest = null;
        if (lease?.PermissionRequestId is { } permissionRequestId)
        {
            leaseRequest = await db.PermissionRequests.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == permissionRequestId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
                cancellationToken).ConfigureAwait(false);
        }
        var leaseFailure = ValidateLease(lease, command.SubjectPrincipalId, command.SubjectAgentInstanceId,
            claim, command.RunId, command.TaskId, now);
        if (leaseFailure is null && requireNonce)
        {
            leaseFailure = nonceHash is null
                ? ("lease_nonce_required", "The capability lease nonce is required.")
                : !FixedTimeEquals(lease!.NonceHash, nonceHash)
                    ? ("lease_nonce_mismatch", "The lease nonce does not match.")
                    : null;
        }

        if (leaseFailure is null)
        {
            var sourceGrant = await db.CapabilityGrants.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == lease!.CapabilityGrantId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
                cancellationToken).ConfigureAwait(false);
            if (sourceGrant is null || sourceGrant.Status == "revoked")
                leaseFailure = ("source_grant_revoked", "The capability lease source grant was revoked or removed.");
        }

        if (leaseFailure is null)
        {
            // A lease is always issued for a permission request in the new
            // governance path. Re-evaluate the exact persisted boundaries rather
            // than loading the current policy set, otherwise publishing a policy
            // after approval could retroactively change an admitted action.
            var frozenBoundaries = leaseRequest is null
                ? []
                : CapabilityRuleMatcher.DeserializeBoundaries(leaseRequest.BoundariesJson);
            var boundaryResult = EvaluateBoundaries(frozenBoundaries, claim);
            if (leaseRequest is null)
                boundaryResult = new BoundaryEvaluation(false, "permission_request_missing", "The capability lease has no persisted permission request boundary.", string.Empty);
            if (!boundaryResult.Allowed) leaseFailure = (boundaryResult.ReasonCode, boundaryResult.Reason);
        }

        if (leaseFailure is not null)
        {
            var denied = NewDecision(scope, command.SubjectPrincipalId, command.SubjectAgentInstanceId, claim,
                GovernanceOutcomes.Denied, leaseFailure.Value.Code, leaseFailure.Value.Reason, "lease",
                lease?.PolicySnapshotHash ?? string.Empty, command.RunId, command.TaskId,
                capabilityLeaseId: lease?.Id, capabilityGrantId: lease?.CapabilityGrantId);
            denied.IdempotencyKey = idempotencyKey;
            denied.EvaluationInputHash = inputHash;
            db.AuthorizationDecisions.Add(denied);
            receipt.AuthorizationDecisionId = denied.Id;
            receipt.Status = "completed";
            receipt.UpdatedAt = now;
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return await ReadLeaseConsumptionAsync(scope, command.CapabilityLeaseId, idempotencyKey, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new LeaseConsumptionResult(false, denied.ReasonCode, lease is null ? null : ToSnapshot(lease), ToSnapshot(denied));
        }

        var activeLease = lease!;
        var affected = await db.CapabilityLeases
            .Where(x => x.Id == activeLease.Id && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                && x.Status == "active" && x.StartsAtUnixMilliseconds <= nowUnix && x.ExpiresAtUnixMilliseconds > nowUnix
                && x.UseCount < x.MaxUses)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.UseCount, x => x.UseCount + 1)
                .SetProperty(x => x.Revision, x => x.Revision + 1)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            var raced = NewDecision(scope, command.SubjectPrincipalId, command.SubjectAgentInstanceId, claim,
                GovernanceOutcomes.Denied, "lease_unavailable", "The capability lease was consumed or revoked concurrently.",
                "lease", activeLease.PolicySnapshotHash, command.RunId, command.TaskId,
                capabilityLeaseId: activeLease.Id, capabilityGrantId: activeLease.CapabilityGrantId);
            raced.IdempotencyKey = idempotencyKey;
            raced.EvaluationInputHash = inputHash;
            db.AuthorizationDecisions.Add(raced);
            receipt.AuthorizationDecisionId = raced.Id;
            receipt.Status = "completed";
            receipt.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new LeaseConsumptionResult(false, raced.ReasonCode, ToSnapshot(activeLease), ToSnapshot(raced));
        }

        await db.CapabilityLeases
            .Where(x => x.Id == activeLease.Id && x.UseCount >= x.MaxUses)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "exhausted"), cancellationToken).ConfigureAwait(false);
        var consumedLease = await db.CapabilityLeases.AsNoTracking().SingleAsync(x => x.Id == activeLease.Id, cancellationToken).ConfigureAwait(false);
        var allowed = NewDecision(scope, command.SubjectPrincipalId, command.SubjectAgentInstanceId, claim,
            GovernanceOutcomes.Allowed, "lease_consumed", "The capability lease was consumed.", "lease",
            consumedLease.PolicySnapshotHash, command.RunId, command.TaskId,
            capabilityLeaseId: consumedLease.Id, capabilityGrantId: consumedLease.CapabilityGrantId);
        allowed.IdempotencyKey = idempotencyKey;
        allowed.EvaluationInputHash = inputHash;
        db.AuthorizationDecisions.Add(allowed);
        receipt.AuthorizationDecisionId = allowed.Id;
        receipt.Status = "completed";
        receipt.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return await ReadLeaseConsumptionAsync(scope, command.CapabilityLeaseId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LeaseConsumptionResult(true, allowed.ReasonCode, ToSnapshot(consumedLease), ToSnapshot(allowed));
    }

    private async Task<LeaseConsumptionResult> ReadLeaseConsumptionAsync(
        TenantContext scope,
        Guid leaseId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var receipt = await db.LeaseConsumptions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
            && x.CapabilityLeaseId == leaseId && x.IdempotencyKey == idempotencyKey,
            cancellationToken).ConfigureAwait(false)
            ?? throw new DbUpdateException("A concurrent lease consumption did not leave a durable receipt.");
        if (receipt.AuthorizationDecisionId is not { } decisionId)
            throw new InvalidOperationException("A prior lease consumption is incomplete and cannot be replayed.");
        var decision = await db.AuthorizationDecisions.AsNoTracking().SingleAsync(x =>
            x.Id == decisionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
            cancellationToken).ConfigureAwait(false);
        var lease = await db.CapabilityLeases.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == leaseId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
            cancellationToken).ConfigureAwait(false);
        return new LeaseConsumptionResult(decision.Outcome == GovernanceOutcomes.Allowed,
            decision.ReasonCode, lease is null ? null : ToSnapshot(lease), ToSnapshot(decision));
    }

    public Task<bool> RevokeGrantAsync(Guid grantId, string reason, CancellationToken cancellationToken = default) =>
        RevokeAsync("grant", grantId, reason, cancellationToken);

    public Task<bool> RevokeDelegationAsync(Guid delegationId, string reason, CancellationToken cancellationToken = default) =>
        RevokeAsync("delegation", delegationId, reason, cancellationToken);

    public Task<bool> RevokeLeaseAsync(Guid leaseId, string reason, CancellationToken cancellationToken = default) =>
        RevokeAsync("lease", leaseId, reason, cancellationToken);

    /// <summary>
    /// Archives a policy bundle in the current tenant/workspace scope. Archiving is
    /// deliberately separate from publishing: an archived bundle is no longer
    /// loaded into future PDP evaluations, while already-admitted runs continue to
    /// use their persisted policy snapshot.
    /// </summary>
    public async Task<bool> RevokePolicyBundleAsync(
        Guid policyBundleId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (policyBundleId == Guid.Empty) return false;
        if (!CanHumanApprove(_tenantAccessor.Current))
            throw new UnauthorizedAccessException("Only a tenant owner or administrator may archive a policy bundle.");

        var normalizedReason = CapabilityRuleMatcher.Required(reason, nameof(reason), 4096);
        var scope = _tenantAccessor.Current;
        var now = UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var bundle = await db.PolicyBundles.SingleOrDefaultAsync(x =>
            x.Id == policyBundleId && x.TenantId == scope.TenantId
            && ((x.ScopeKind == "tenant" && x.ScopeId == scope.TenantId)
                || (x.ScopeKind == "workspace" && x.ScopeId == scope.WorkspaceId)), cancellationToken)
            .ConfigureAwait(false);
        if (bundle is null || bundle.Status == "archived")
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        bundle.Status = "archived";
        bundle.Revision++;
        bundle.UpdatedAt = now;
        bundle.UpdatedByPrincipalId = scope.PrincipalId;
        bundle.ArchivedAt = now;
        var currentVersion = await db.PolicyVersions.SingleOrDefaultAsync(x =>
            x.Id == bundle.CurrentVersionId && x.TenantId == scope.TenantId, cancellationToken)
            .ConfigureAwait(false);
        if (currentVersion is not null) currentVersion.Status = "archived";
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _ = normalizedReason; // The reason is accepted for audit/API compatibility; the bundle row has no reason column.
        return true;
    }

    private DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    private async Task<bool> RevokeAsync(string kind, Guid id, string reason, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty) return false;
        var normalizedReason = CapabilityRuleMatcher.Required(reason, nameof(reason), 4096);
        var scope = _tenantAccessor.Current;
        var now = UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return kind switch
        {
            "grant" => await db.CapabilityGrants.Where(x => x.Id == id && x.TenantId == scope.TenantId
                    && x.WorkspaceId == scope.WorkspaceId && x.Status != "revoked")
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "revoked")
                    .SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokeReason, normalizedReason)
                    .SetProperty(x => x.Revision, x => x.Revision + 1).SetProperty(x => x.UpdatedAt, now), cancellationToken)
                .ConfigureAwait(false) == 1,
            "delegation" => await db.ApprovalDelegations.Where(x => x.Id == id && x.TenantId == scope.TenantId
                    && x.WorkspaceId == scope.WorkspaceId && x.Status != "revoked")
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "revoked")
                    .SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokeReason, normalizedReason)
                    .SetProperty(x => x.Revision, x => x.Revision + 1).SetProperty(x => x.UpdatedAt, now), cancellationToken)
                .ConfigureAwait(false) == 1,
            "lease" => await db.CapabilityLeases.Where(x => x.Id == id && x.TenantId == scope.TenantId
                    && x.WorkspaceId == scope.WorkspaceId && x.Status != "revoked")
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "revoked")
                    .SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokeReason, normalizedReason)
                    .SetProperty(x => x.Revision, x => x.Revision + 1).SetProperty(x => x.UpdatedAt, now), cancellationToken)
                .ConfigureAwait(false) == 1,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static async Task<PolicyBundleRecord?> FindPolicyBundleAsync(
        GovernanceDbContext db,
        Guid id,
        TenantContext scope,
        CancellationToken cancellationToken) =>
        await db.PolicyBundles.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == scope.TenantId
            && ((x.ScopeKind == "tenant" && x.ScopeId == scope.TenantId)
                || (x.ScopeKind == "workspace" && x.ScopeId == scope.WorkspaceId)), cancellationToken).ConfigureAwait(false);

    private static IReadOnlyList<AuthorizationBoundary> NormalizeBoundaries(IReadOnlyList<AuthorizationBoundary> boundaries)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        return boundaries.Select(boundary => new AuthorizationBoundary(
                CapabilityRuleMatcher.Required(boundary.Name, nameof(boundary.Name), 256),
                CapabilityRuleMatcher.NormalizeRulesAllowEmpty(boundary.Rules)))
            .OrderBy(boundary => boundary.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<IReadOnlyList<AuthorizationBoundary>> LoadEffectiveBoundariesAsync(
        GovernanceDbContext db,
        IReadOnlyList<AuthorizationBoundary> supplied,
        TenantContext scope,
        CancellationToken cancellationToken)
    {
        // A run-admitted resolver includes policy:* boundaries captured in its
        // frozen configuration. In that case those boundaries are authoritative;
        // loading current bundles here would make hot policy publication affect an
        // already admitted run. Legacy callers without a snapshot retain the
        // compatibility behavior of loading the active set.
        if (supplied.Any(boundary => boundary.Name.StartsWith("policy:", StringComparison.OrdinalIgnoreCase)
            || boundary.Name.StartsWith("policy_snapshot:", StringComparison.OrdinalIgnoreCase)))
        {
            return NormalizeBoundaries(supplied);
        }

        var bundles = await db.PolicyBundles.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.Status == "active"
                && ((x.ScopeKind == "tenant" && x.ScopeId == scope.TenantId)
                    || (x.ScopeKind == "workspace" && x.ScopeId == scope.WorkspaceId)))
            .OrderBy(x => x.ScopeKind).ThenBy(x => x.Slug)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var versionIds = bundles.Select(x => x.CurrentVersionId).ToArray();
        var versions = versionIds.Length == 0
            ? []
            : await db.PolicyVersions.AsNoTracking().Where(x => versionIds.Contains(x.Id) && x.Status == "published")
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var byId = versions.ToDictionary(x => x.Id);
        var result = new List<AuthorizationBoundary>(bundles.Length + supplied.Count);
        foreach (var bundle in bundles)
        {
            if (!byId.TryGetValue(bundle.CurrentVersionId, out var version))
            {
                result.Add(new AuthorizationBoundary($"policy:{bundle.Slug}:missing", []));
                continue;
            }
            result.Add(new AuthorizationBoundary($"policy:{bundle.Slug}:v{version.Version}",
                CapabilityRuleMatcher.DeserializeRules(version.RulesJson)));
        }
        result.AddRange(NormalizeBoundaries(supplied));
        return result;
    }

    private static BoundaryEvaluation EvaluateBoundaries(IReadOnlyList<AuthorizationBoundary> boundaries, CapabilityClaim claim)
    {
        var hash = CapabilityRuleMatcher.HashBoundaries(boundaries);
        if (boundaries.Count == 0)
            return new BoundaryEvaluation(false, "missing_authorization_boundary", "No authorization boundary was supplied or published.", hash);
        foreach (var boundary in boundaries)
        {
            if (CapabilityRuleMatcher.HasDeny(boundary.Rules, claim))
                return new BoundaryEvaluation(false, "explicit_deny", $"Authorization boundary '{boundary.Name}' explicitly denies the capability.", hash);
        }
        foreach (var boundary in boundaries)
        {
            if (!CapabilityRuleMatcher.HasAllow(boundary.Rules, claim))
                return new BoundaryEvaluation(false, "boundary_not_allowed", $"Authorization boundary '{boundary.Name}' does not allow the capability.", hash);
        }
        return new BoundaryEvaluation(true, "boundaries_allow", "Every authorization boundary allows the capability.", hash);
    }

    private static async Task<CapabilityGrantRecord?> FindMatchingGrantAsync(
        GovernanceDbContext db,
        TenantContext scope,
        Guid subjectPrincipalId,
        Guid? subjectAgentInstanceId,
        CapabilityClaim claim,
        Guid? runId,
        Guid? taskId,
        int requestedUses,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nowUnix = now.ToUnixTimeMilliseconds();
        var candidates = await db.CapabilityGrants.AsNoTracking().Where(x =>
                x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                && x.SubjectPrincipalId == subjectPrincipalId
                && (x.SubjectAgentInstanceId == null || x.SubjectAgentInstanceId == subjectAgentInstanceId)
                && x.Status == "active" && x.StartsAtUnixMilliseconds <= nowUnix && x.ExpiresAtUnixMilliseconds > nowUnix
                && x.UseCount <= x.MaxUses - requestedUses
                && (x.RunId == null || x.RunId == runId) && (x.TaskId == null || x.TaskId == taskId))
            .OrderByDescending(x => x.SubjectAgentInstanceId != null)
            .ThenBy(x => x.ExpiresAtUnixMilliseconds)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return candidates.FirstOrDefault(x => CapabilityRuleMatcher.Covers(ClaimOf(x), claim));
    }

    private static async Task<bool> HasEligibleDelegationAsync(
        GovernanceDbContext db,
        TenantContext scope,
        CapabilityClaim claim,
        Guid? runId,
        string risk,
        decimal expectedCost,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nowUnix = now.ToUnixTimeMilliseconds();
        var candidates = await db.ApprovalDelegations.AsNoTracking().Where(x =>
                x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                && x.Status == "active" && x.StartsAtUnixMilliseconds <= nowUnix && x.ExpiresAtUnixMilliseconds > nowUnix
                && x.UseCount < x.MaxUses && (x.RunId == null || x.RunId == runId))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return candidates.Any(x => !x.RequireUserReview && RiskRank(risk) <= RiskRank(x.MaxRisk)
            && expectedCost <= x.MaxCost && DelegationAllows(x, claim));
    }

    private static void ValidateParentGrant(
        CapabilityGrantRecord parent,
        GrantCapabilityCommand command,
        CapabilityClaim requested,
        DateTimeOffset now)
    {
        if (!parent.Transferable) throw new InvalidOperationException("Parent capability grant is not transferable.");
        if (parent.Status != "active" || parent.StartsAtUnixMilliseconds > now.ToUnixTimeMilliseconds()
            || parent.ExpiresAtUnixMilliseconds <= now.ToUnixTimeMilliseconds())
            throw new InvalidOperationException("Parent capability grant is not active.");
        if (!CapabilityRuleMatcher.Covers(ClaimOf(parent), requested))
            throw new InvalidOperationException("Child capability exceeds the parent grant ceiling.");
        if (command.ExpiresAt > parent.ExpiresAt)
            throw new InvalidOperationException("Child capability cannot outlive the parent grant.");
        if (parent.RunId is not null && parent.RunId != command.RunId)
            throw new InvalidOperationException("Child capability exceeds the parent run scope.");
        if (parent.TaskId is not null && parent.TaskId != command.TaskId)
            throw new InvalidOperationException("Child capability exceeds the parent task scope.");
    }

    private static async Task<bool> ReserveGrantUsesAsync(
        GovernanceDbContext db,
        Guid grantId,
        int uses,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nowUnix = now.ToUnixTimeMilliseconds();
        var affected = await db.CapabilityGrants.Where(x => x.Id == grantId && x.Status == "active"
                && x.StartsAtUnixMilliseconds <= nowUnix && x.ExpiresAtUnixMilliseconds > nowUnix
                && x.UseCount <= x.MaxUses - uses)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UseCount, x => x.UseCount + uses)
                .SetProperty(x => x.Revision, x => x.Revision + 1).SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (affected == 1)
        {
            await db.CapabilityGrants.Where(x => x.Id == grantId && x.UseCount >= x.MaxUses)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "exhausted"), cancellationToken)
                .ConfigureAwait(false);
        }
        return affected == 1;
    }

    private static async Task<bool> ReserveDelegationUseAsync(
        GovernanceDbContext db,
        Guid delegationId,
        Guid permissionRequestId,
        TenantContext scope,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (await db.DelegationUses.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId
                && x.WorkspaceId == scope.WorkspaceId && x.ApprovalDelegationId == delegationId
                && x.PermissionRequestId == permissionRequestId, cancellationToken).ConfigureAwait(false))
            return false;
        var receipt = new DelegationUseRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            ApprovalDelegationId = delegationId, PermissionRequestId = permissionRequestId, CreatedAt = now
        };
        db.DelegationUses.Add(receipt);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            db.Entry(receipt).State = EntityState.Detached;
            return false;
        }
        var nowUnix = now.ToUnixTimeMilliseconds();
        var affected = await db.ApprovalDelegations.Where(x => x.Id == delegationId && x.Status == "active"
                && x.StartsAtUnixMilliseconds <= nowUnix && x.ExpiresAtUnixMilliseconds > nowUnix
                && x.UseCount < x.MaxUses)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UseCount, x => x.UseCount + 1)
                .SetProperty(x => x.Revision, x => x.Revision + 1).SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (affected == 1)
        {
            await db.ApprovalDelegations.Where(x => x.Id == delegationId && x.UseCount >= x.MaxUses)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "exhausted"), cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await db.DelegationUses.Where(x => x.Id == receipt.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        return affected == 1;
    }

    private static (string Code, string Reason)? ValidateDelegation(
        ApprovalDelegationRecord? delegation,
        Guid? approverAgentInstanceId,
        PermissionRequestRecord request,
        CapabilityClaim claim,
        DateTimeOffset now)
    {
        if (delegation is null) return ("delegation_not_found", "The approval delegation was not found in this scope.");
        if (approverAgentInstanceId is null || delegation.DelegateAgentInstanceId != approverAgentInstanceId)
            return ("delegation_actor_mismatch", "The approving agent does not own this delegation.");
        var nowUnix = now.ToUnixTimeMilliseconds();
        if (delegation.Status != "active" || delegation.StartsAtUnixMilliseconds > nowUnix
            || delegation.ExpiresAtUnixMilliseconds <= nowUnix || delegation.UseCount >= delegation.MaxUses)
            return ("delegation_unavailable", "The approval delegation is expired, revoked, or exhausted.");
        if (delegation.RunId is not null && delegation.RunId != request.RunId)
            return ("delegation_ceiling_exceeded", "The request is outside the delegation run scope.");
        if (RiskRank(request.Risk) > RiskRank(delegation.MaxRisk) || request.ExpectedCost > delegation.MaxCost)
            return ("delegation_ceiling_exceeded", "The request exceeds the delegation risk or cost ceiling.");
        if (!DelegationAllows(delegation, claim))
            return ("delegation_ceiling_exceeded", "The request exceeds the delegated capability, action, or resource scope.");
        return null;
    }

    private static bool DelegationAllows(ApprovalDelegationRecord delegation, CapabilityClaim claim)
    {
        var rules = CapabilityRuleMatcher.DeserializeRules(delegation.RulesJson);
        return !CapabilityRuleMatcher.HasDeny(rules, claim) && CapabilityRuleMatcher.HasAllow(rules, claim);
    }

    private static (string Code, string Reason)? ValidateLease(
        CapabilityLeaseRecord? lease,
        Guid subjectPrincipalId,
        Guid? subjectAgentInstanceId,
        CapabilityClaim claim,
        Guid? runId,
        Guid? taskId,
        DateTimeOffset now)
    {
        if (lease is null) return ("lease_not_found", "The capability lease was not found in this scope.");
        if (lease.SubjectPrincipalId != subjectPrincipalId || lease.SubjectAgentInstanceId != subjectAgentInstanceId)
            return ("lease_subject_mismatch", "The capability lease belongs to another subject.");
        if (!ClaimsEqual(ClaimOf(lease), claim)) return ("lease_scope_mismatch", "The action does not match the lease capability scope.");
        if (lease.RunId != runId || lease.TaskId != taskId) return ("lease_scope_mismatch", "The action does not match the lease run/task scope.");
        var nowUnix = now.ToUnixTimeMilliseconds();
        if (lease.Status == "revoked") return ("lease_revoked", "The capability lease was revoked.");
        if (lease.ExpiresAtUnixMilliseconds <= nowUnix) return ("lease_expired", "The capability lease has expired.");
        if (lease.StartsAtUnixMilliseconds > nowUnix) return ("lease_not_started", "The capability lease is not active yet.");
        if (lease.Status != "active" || lease.UseCount >= lease.MaxUses) return ("lease_exhausted", "The capability lease is exhausted.");
        return null;
    }

    private static bool CanHumanApprove(TenantContext scope) =>
        string.Equals(scope.Role, "owner", StringComparison.OrdinalIgnoreCase)
        || string.Equals(scope.Role, "admin", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalRequest(string status) => status is PermissionRequestStatuses.Granted
        or PermissionRequestStatuses.Denied or PermissionRequestStatuses.Expired or PermissionRequestStatuses.Cancelled;

    private async Task<PermissionResolution> DenyRequestAsync(
        GovernanceDbContext db,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        PermissionRequestRecord request,
        TenantContext scope,
        CapabilityClaim claim,
        string reasonCode,
        string reason,
        Guid? decidedByAgentInstanceId,
        Guid? approvalDelegationId,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string? policyHash = null)
    {
        request.Status = PermissionRequestStatuses.Denied;
        request.Revision++;
        request.UpdatedAt = now;
        var decision = NewDecision(scope, request.SubjectPrincipalId, request.SubjectAgentInstanceId, claim,
            GovernanceOutcomes.Denied, reasonCode, reason, approvalDelegationId is null ? "user" : "delegation",
            policyHash ?? request.PolicySnapshotHash, request.RunId, request.TaskId, request.Id,
            decidedByAgentInstanceId: decidedByAgentInstanceId, approvalDelegationId: approvalDelegationId);
        db.AuthorizationDecisions.Add(decision);
        request.AuthorizationDecisionId = decision.Id;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PermissionResolution(ToSnapshot(request), ToSnapshot(decision), null, null);
    }

    private static async Task<PermissionResolution> LoadResolutionAsync(
        GovernanceDbContext db,
        PermissionRequestRecord request,
        CancellationToken cancellationToken)
    {
        var decision = request.AuthorizationDecisionId is { } decisionId
            ? await db.AuthorizationDecisions.AsNoTracking().SingleAsync(x => x.Id == decisionId, cancellationToken).ConfigureAwait(false)
            : throw new InvalidOperationException("A permission request has no authorization decision.");
        var grant = request.CapabilityGrantId is { } grantId
            ? await db.CapabilityGrants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == grantId, cancellationToken).ConfigureAwait(false)
            : null;
        var lease = request.CapabilityLeaseId is { } leaseId
            ? await db.CapabilityLeases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == leaseId, cancellationToken).ConfigureAwait(false)
            : null;
        return new PermissionResolution(ToSnapshot(request), ToSnapshot(decision), grant is null ? null : ToSnapshot(grant), lease is null ? null : ToSnapshot(lease, lease.Nonce));
    }

    private static async Task<AuthorizationDecisionSnapshot> PersistDecisionAsync(
        GovernanceDbContext db,
        AuthorizationDecisionRecord decision,
        CancellationToken cancellationToken)
    {
        db.AuthorizationDecisions.Add(decision);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToSnapshot(decision);
    }

    private static CapabilityGrantRecord NewGrant(
        TenantContext scope,
        Guid subjectPrincipalId,
        Guid? subjectAgentInstanceId,
        CapabilityClaim claim,
        Guid? runId,
        Guid? taskId,
        Guid? parentGrantId,
        Guid? sourceDelegationId,
        bool transferable,
        int maxUses,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        Guid issuedByPrincipalId,
        Guid? issuedByAgentInstanceId,
        string reason) => new()
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            SubjectPrincipalId = subjectPrincipalId, SubjectAgentInstanceId = subjectAgentInstanceId,
            Capability = claim.Capability, Action = claim.Action, Resource = claim.Resource,
            RunId = runId, TaskId = taskId, ParentGrantId = parentGrantId, SourceDelegationId = sourceDelegationId,
            Transferable = transferable, Status = "active", MaxUses = maxUses, UseCount = 0, Revision = 1,
            IssuedByPrincipalId = issuedByPrincipalId, IssuedByAgentInstanceId = issuedByAgentInstanceId,
            Reason = Truncate(reason, 4096), StartsAt = now, ExpiresAt = expiresAt,
            StartsAtUnixMilliseconds = now.ToUnixTimeMilliseconds(), ExpiresAtUnixMilliseconds = expiresAt.ToUnixTimeMilliseconds(),
            CreatedAt = now, UpdatedAt = now
        };

    private static (CapabilityLeaseRecord Lease, string Nonce) NewLease(
        TenantContext scope,
        CapabilityGrantRecord grant,
        Guid? permissionRequestId,
        CapabilityClaim claim,
        Guid? runId,
        Guid? taskId,
        int maxUses,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        string policyHash)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return (new CapabilityLeaseRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            SubjectPrincipalId = grant.SubjectPrincipalId, SubjectAgentInstanceId = grant.SubjectAgentInstanceId,
            CapabilityGrantId = grant.Id, PermissionRequestId = permissionRequestId,
            Capability = claim.Capability, Action = claim.Action, Resource = claim.Resource,
            RunId = runId, TaskId = taskId, NonceHash = CapabilityRuleMatcher.HashNonce(nonce), Nonce = nonce,
            PolicySnapshotHash = policyHash, Status = "active", MaxUses = maxUses, UseCount = 0, Revision = 1,
            StartsAt = now, ExpiresAt = expiresAt, StartsAtUnixMilliseconds = now.ToUnixTimeMilliseconds(),
            ExpiresAtUnixMilliseconds = expiresAt.ToUnixTimeMilliseconds(), CreatedAt = now, UpdatedAt = now
        }, nonce);
    }

    private AuthorizationDecisionRecord NewDecision(
        TenantContext scope,
        Guid subjectPrincipalId,
        Guid? subjectAgentInstanceId,
        CapabilityClaim claim,
        string outcome,
        string reasonCode,
        string reason,
        string source,
        string policyHash,
        Guid? runId,
        Guid? taskId,
        Guid? permissionRequestId = null,
        Guid? capabilityGrantId = null,
        Guid? capabilityLeaseId = null,
        Guid? decidedByAgentInstanceId = null,
        Guid? approvalDelegationId = null) => new()
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            SubjectPrincipalId = subjectPrincipalId, SubjectAgentInstanceId = subjectAgentInstanceId,
            Capability = claim.Capability, Action = claim.Action, Resource = claim.Resource,
            RunId = runId, TaskId = taskId,
            PermissionRequestId = permissionRequestId, CapabilityGrantId = capabilityGrantId,
            CapabilityLeaseId = capabilityLeaseId, ApprovalDelegationId = approvalDelegationId,
            Outcome = outcome, ReasonCode = reasonCode, Reason = Truncate(reason, 4096),
            DecisionSource = source, PolicySnapshotHash = policyHash,
            EvaluationInputHash = policyHash,
            DecidedByPrincipalId = scope.PrincipalId, DecidedByAgentInstanceId = decidedByAgentInstanceId,
            CreatedAt = UtcNow
        };

    private static string AppendEscalation(string json, string reason, DateTimeOffset now)
    {
        var entries = JsonSerializer.Deserialize<List<EscalationEntry>>(json, JsonOptions) ?? [];
        entries.Add(new EscalationEntry(reason, now));
        return JsonSerializer.Serialize(entries, JsonOptions);
    }

    private static bool FixedTimeEquals(string leftHex, string rightHex)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(leftHex), Convert.FromHexString(rightHex));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool ClaimsEqual(CapabilityClaim left, CapabilityClaim right) =>
        string.Equals(left.Capability, right.Capability, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Action, right.Action, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Resource, right.Resource, StringComparison.OrdinalIgnoreCase);

    private static void ValidateExpiry(DateTimeOffset now, DateTimeOffset expiresAt)
    {
        if (expiresAt <= now) throw new ArgumentOutOfRangeException(nameof(expiresAt), "Expiry must be in the future.");
    }

    private static void ValidateUses(int uses)
    {
        if (uses is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(uses), "Use count must be between 1 and 10000.");
    }

    private static string NormalizeSlug(string value)
    {
        var slug = CapabilityRuleMatcher.Required(value, nameof(value), 128).ToLowerInvariant();
        if (slug.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ArgumentException("Policy bundle slug may contain only ASCII letters, digits, '.', '_' and '-'.", nameof(value));
        return slug;
    }

    private static string NormalizeRisk(string value)
    {
        var risk = CapabilityRuleMatcher.Required(value, nameof(value), 32).ToLowerInvariant();
        return risk is "low" or "medium" or "high" or "critical"
            ? risk
            : throw new ArgumentException("Risk must be low, medium, high, or critical.", nameof(value));
    }

    private static int RiskRank(string risk) => risk.ToLowerInvariant() switch
    {
        "low" => 0,
        "medium" => 1,
        "high" => 2,
        "critical" => 3,
        _ => int.MaxValue
    };

    private static string Truncate(string? value, int maxLength, string fallback = "")
    {
        var result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return result.Length <= maxLength ? result : result[..maxLength];
    }

    private static CapabilityClaim ClaimOf(CapabilityGrantRecord record) => new(record.Capability, record.Action, record.Resource);
    private static CapabilityClaim ClaimOf(PermissionRequestRecord record) => new(record.Capability, record.Action, record.Resource);
    private static CapabilityClaim ClaimOf(CapabilityLeaseRecord record) => new(record.Capability, record.Action, record.Resource);

    private static PolicyBundleSnapshot ToSnapshot(PolicyBundleRecord record) => new(
        record.Id, record.TenantId, record.ScopeKind == "workspace" ? record.ScopeId : null,
        record.Slug, record.DisplayName, record.Status, record.Revision, record.CurrentVersionId,
        record.CreatedAt, record.UpdatedAt);

    private static PolicyVersionSnapshot ToSnapshot(PolicyVersionRecord record) => new(
        record.Id, record.PolicyBundleId, record.Version, CapabilityRuleMatcher.DeserializeRules(record.RulesJson),
        record.ContentHash, record.Status, record.CreatedAt);

    private static CapabilityGrantSnapshot ToSnapshot(CapabilityGrantRecord record) => new(
        record.Id, record.TenantId, record.WorkspaceId, record.SubjectPrincipalId, record.SubjectAgentInstanceId,
        ClaimOf(record), record.RunId, record.TaskId, record.ParentGrantId, record.Transferable, record.Status,
        record.MaxUses, record.UseCount, record.StartsAt, record.ExpiresAt, record.RevokedAt, record.RevokeReason);

    private static ApprovalDelegationSnapshot ToSnapshot(ApprovalDelegationRecord record) => new(
        record.Id, record.TenantId, record.WorkspaceId, record.DelegatedByPrincipalId,
        record.DelegateAgentVersionId, record.DelegateAgentInstanceId,
        CapabilityRuleMatcher.DeserializeRules(record.RulesJson), record.MaxRisk, record.MaxCost, record.RunId,
        record.RequireUserReview, record.Status, record.MaxUses, record.UseCount, record.StartsAt, record.ExpiresAt,
        record.RevokedAt, record.RevokeReason);

    private static PermissionRequestSnapshot ToSnapshot(PermissionRequestRecord record) => new(
        record.Id, record.TenantId, record.WorkspaceId, record.SubjectPrincipalId, record.SubjectAgentInstanceId,
        record.ParentAgentInstanceId, ClaimOf(record), record.RunId, record.TaskId, record.Risk, record.ExpectedCost,
        record.Status, record.AuthorizationDecisionId, record.CapabilityGrantId, record.CapabilityLeaseId,
        record.ExpiresAt, record.CreatedAt, record.UpdatedAt);

    private static AuthorizationDecisionSnapshot ToSnapshot(AuthorizationDecisionRecord record) => new(
        record.Id, record.TenantId, record.WorkspaceId, record.SubjectPrincipalId, record.SubjectAgentInstanceId,
        new CapabilityClaim(record.Capability, record.Action, record.Resource), record.RunId, record.TaskId,
        record.PermissionRequestId,
        record.CapabilityGrantId, record.CapabilityLeaseId, record.Outcome, record.ReasonCode, record.Reason,
        record.DecisionSource, record.PolicySnapshotHash, record.DecidedByPrincipalId,
        record.DecidedByAgentInstanceId, record.CreatedAt);

    private static CapabilityLeaseSnapshot ToSnapshot(CapabilityLeaseRecord record, string? nonce = null) => new(
        record.Id, record.TenantId, record.WorkspaceId, record.SubjectPrincipalId, record.SubjectAgentInstanceId,
        record.CapabilityGrantId, record.PermissionRequestId, ClaimOf(record), record.RunId, record.TaskId, nonce ?? record.Nonce,
        record.PolicySnapshotHash, record.Status, record.MaxUses, record.UseCount, record.StartsAt, record.ExpiresAt,
        record.RevokedAt, record.RevokeReason);

    private sealed record BoundaryEvaluation(bool Allowed, string ReasonCode, string Reason, string Hash);
    private sealed record EscalationEntry(string Reason, DateTimeOffset At);
}
