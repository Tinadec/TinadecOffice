namespace TinadecCore.Governance;

public sealed class PolicyBundleRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string ScopeKind { get; set; } = "workspace";
    public Guid ScopeId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public long Revision { get; set; }
    public Guid CurrentVersionId { get; set; }
    public Guid CreatedByPrincipalId { get; set; }
    public Guid UpdatedByPrincipalId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

public sealed class PolicyVersionRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string ScopeKind { get; set; } = "workspace";
    public Guid ScopeId { get; set; }
    public Guid PolicyBundleId { get; set; }
    public int Version { get; set; }
    public string RulesJson { get; set; } = "[]";
    public string ContentHash { get; set; } = string.Empty;
    public string Status { get; set; } = "published";
    public Guid CreatedByPrincipalId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CapabilityGrantRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SubjectPrincipalId { get; set; }
    public Guid? SubjectAgentInstanceId { get; set; }
    public string Capability { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public Guid? RunId { get; set; }
    public Guid? TaskId { get; set; }
    public Guid? ParentGrantId { get; set; }
    public Guid? SourceDelegationId { get; set; }
    public bool Transferable { get; set; }
    public string Status { get; set; } = "active";
    public int MaxUses { get; set; }
    public int UseCount { get; set; }
    public long Revision { get; set; }
    public Guid IssuedByPrincipalId { get; set; }
    public Guid? IssuedByAgentInstanceId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public long StartsAtUnixMilliseconds { get; set; }
    public long ExpiresAtUnixMilliseconds { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ApprovalDelegationRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid DelegatedByPrincipalId { get; set; }
    public Guid DelegateAgentVersionId { get; set; }
    public Guid DelegateAgentInstanceId { get; set; }
    public string RulesJson { get; set; } = "[]";
    public string RulesHash { get; set; } = string.Empty;
    public string MaxRisk { get; set; } = "low";
    public decimal MaxCost { get; set; }
    public Guid? RunId { get; set; }
    public bool RequireUserReview { get; set; }
    public string Status { get; set; } = "active";
    public int MaxUses { get; set; }
    public int UseCount { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public long StartsAtUnixMilliseconds { get; set; }
    public long ExpiresAtUnixMilliseconds { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PermissionRequestRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SubjectPrincipalId { get; set; }
    public Guid? SubjectAgentInstanceId { get; set; }
    public Guid? ParentAgentInstanceId { get; set; }
    public string Capability { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string BoundariesJson { get; set; } = "[]";
    public Guid? RunId { get; set; }
    public Guid? TaskId { get; set; }
    public string Risk { get; set; } = "low";
    public decimal ExpectedCost { get; set; }
    public string Rationale { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public int RequestedUses { get; set; }
    public string PolicySnapshotHash { get; set; } = string.Empty;
    public string EscalationChainJson { get; set; } = "[]";
    public string Status { get; set; } = "pending";
    public long Revision { get; set; }
    public Guid? AuthorizationDecisionId { get; set; }
    public Guid? CapabilityGrantId { get; set; }
    public Guid? CapabilityLeaseId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public long ExpiresAtUnixMilliseconds { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AuthorizationDecisionRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SubjectPrincipalId { get; set; }
    public Guid? SubjectAgentInstanceId { get; set; }
    public string Capability { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public Guid? RunId { get; set; }
    public Guid? TaskId { get; set; }
    public Guid? PermissionRequestId { get; set; }
    public Guid? CapabilityGrantId { get; set; }
    public Guid? CapabilityLeaseId { get; set; }
    public Guid? ApprovalDelegationId { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string DecisionSource { get; set; } = string.Empty;
    public string PolicySnapshotHash { get; set; } = string.Empty;
    public string? IdempotencyKey { get; set; }
    public string EvaluationInputHash { get; set; } = string.Empty;
    public Guid DecidedByPrincipalId { get; set; }
    public Guid? DecidedByAgentInstanceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LeaseConsumptionRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid CapabilityLeaseId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string InputHash { get; set; } = string.Empty;
    public Guid? AuthorizationDecisionId { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DelegationUseRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ApprovalDelegationId { get; set; }
    public Guid PermissionRequestId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CapabilityLeaseRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SubjectPrincipalId { get; set; }
    public Guid? SubjectAgentInstanceId { get; set; }
    public Guid CapabilityGrantId { get; set; }
    public Guid? PermissionRequestId { get; set; }
    public string Capability { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public Guid? RunId { get; set; }
    public Guid? TaskId { get; set; }
    public string NonceHash { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public string PolicySnapshotHash { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public int MaxUses { get; set; }
    public int UseCount { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public long StartsAtUnixMilliseconds { get; set; }
    public long ExpiresAtUnixMilliseconds { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
