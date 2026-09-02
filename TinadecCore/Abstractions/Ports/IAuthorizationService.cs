using System.Text.Json.Serialization;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Evaluates effective capabilities without exposing a specific policy engine or agent runtime.
/// Every evaluation is persisted as an <see cref="AuthorizationDecisionSnapshot"/>.
/// </summary>
public interface IPolicyDecisionPoint
{
    Task<AuthorizationDecisionSnapshot> EvaluateAsync(
        AuthorizationEvaluationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Trusted runtime adapter for frozen configuration and agent lineage. Implementations
/// must derive these facts from Core-owned state, never from an HTTP request body.
/// </summary>
public interface IAuthorizationContextResolver
{
    Task<IReadOnlyList<AuthorizationBoundary>> ResolveBoundariesAsync(
        AuthorizationContextRequest request,
        CancellationToken cancellationToken = default);

    Task<Guid?> ResolveAgentVersionIdAsync(
        Guid tenantId,
        Guid workspaceId,
        Guid agentInstanceId,
        CancellationToken cancellationToken = default);

    Task<bool> IsSelfOrDescendantAsync(
        Guid tenantId,
        Guid workspaceId,
        Guid requesterAgentInstanceId,
        Guid candidateApproverAgentInstanceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Durable control plane for policy bundles, capability grants, delegated approval,
/// permission requests, and short-lived capability leases.
/// </summary>
public interface IAuthorizationService
{
    /// <summary>
    /// Resolves a fixed Core tool claim. The result may be allowed immediately or
    /// park a durable permission request; lease nonces never cross this boundary.
    /// </summary>
    Task<ToolAuthorizationResult> AuthorizeToolAsync(
        ToolAuthorizationCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Consumes a previously issued tool lease inside Core.</summary>
    Task<ToolAuthorizationResult> ConsumeToolLeaseAsync(
        ToolLeaseConsumptionCommand command,
        CancellationToken cancellationToken = default);

    Task<PolicyBundleSnapshot> CreatePolicyBundleAsync(
        CreatePolicyBundleCommand command,
        CancellationToken cancellationToken = default);

    Task<PolicyVersionSnapshot> PublishPolicyVersionAsync(
        PublishPolicyVersionCommand command,
        CancellationToken cancellationToken = default);

    Task<CapabilityGrantSnapshot> GrantCapabilityAsync(
        GrantCapabilityCommand command,
        CancellationToken cancellationToken = default);

    Task<ApprovalDelegationSnapshot> CreateApprovalDelegationAsync(
        CreateApprovalDelegationCommand command,
        CancellationToken cancellationToken = default);

    Task<PermissionResolution> RequestPermissionAsync(
        PermissionRequestCommand command,
        CancellationToken cancellationToken = default);

    Task<PermissionResolution> DecidePermissionAsync(
        PermissionDecisionCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a tenant/workspace-scoped permission request and its latest durable resolution.</summary>
    Task<PermissionResolution?> GetPermissionRequestAsync(
        Guid permissionRequestId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists tenant/workspace-scoped permission requests for operator views.</summary>
    Task<IReadOnlyList<PermissionRequestSnapshot>> ListPermissionRequestsAsync(
        string? status = null,
        Guid? runId = null,
        Guid? taskId = null,
        CancellationToken cancellationToken = default);

    Task<LeaseConsumptionResult> TryConsumeLeaseAsync(
        LeaseConsumptionCommand command,
        CancellationToken cancellationToken = default);

    Task<bool> RevokeGrantAsync(
        Guid grantId,
        string reason,
        CancellationToken cancellationToken = default);

    Task<bool> RevokeDelegationAsync(
        Guid delegationId,
        string reason,
        CancellationToken cancellationToken = default);

    Task<bool> RevokeLeaseAsync(
        Guid leaseId,
        string reason,
        CancellationToken cancellationToken = default);

    Task<bool> RevokePolicyBundleAsync(
        Guid policyBundleId,
        string reason,
        CancellationToken cancellationToken = default);
}

public static class GovernanceOutcomes
{
    public const string Allowed = "allowed";
    public const string Denied = "denied";
    public const string PermissionRequired = "permission_required";
    public const string UserRequired = "user_required";
}

public static class PermissionRequestStatuses
{
    public const string Pending = "pending";
    public const string Evaluating = "evaluating";
    public const string AwaitingDelegate = "awaiting_delegate";
    public const string AwaitingUser = "awaiting_user";
    public const string Granted = "granted";
    public const string Denied = "denied";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
}

public sealed record CapabilityClaim(string Capability, string Action, string Resource);

public sealed record CapabilityRule(
    string Effect,
    string Capability,
    string Action,
    string ResourcePattern);

/// <summary>
/// Immutable policy material captured when a run is admitted.  The runtime stores
/// this value inside the frozen run document so later policy publication or archive
/// cannot change an already admitted tool authorization.
/// </summary>
public sealed record FrozenPolicyBundle(
    Guid BundleId,
    Guid VersionId,
    string Slug,
    int Version,
    string ContentHash,
    IReadOnlyList<CapabilityRule> Rules);

/// <summary>Captures the tenant/workspace policy set for a new durable run.</summary>
public interface IPolicySnapshotProvider
{
    Task<FrozenPolicySnapshot> CaptureAsync(
        Guid tenantId,
        Guid workspaceId,
        CancellationToken cancellationToken = default);
}

public sealed record FrozenPolicySnapshot(
    string SnapshotHash,
    IReadOnlyList<FrozenPolicyBundle> Bundles);

/// <summary>
/// One independently required authorization boundary, such as an AgentVersion,
/// ModeNode, ToolManifest, or TaskRequest. All boundaries must allow the claim;
/// a deny in any boundary wins.
/// </summary>
public sealed record AuthorizationBoundary(
    string Name,
    IReadOnlyList<CapabilityRule> Rules);

public sealed record CreatePolicyBundleCommand(
    string Slug,
    string DisplayName,
    IReadOnlyList<CapabilityRule> Rules,
    bool TenantWide = false);

public sealed record PublishPolicyVersionCommand(
    Guid PolicyBundleId,
    IReadOnlyList<CapabilityRule> Rules);

public sealed record PolicyBundleSnapshot(
    Guid Id,
    Guid TenantId,
    Guid? WorkspaceId,
    string Slug,
    string DisplayName,
    string Status,
    long Revision,
    Guid CurrentVersionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PolicyVersionSnapshot(
    Guid Id,
    Guid PolicyBundleId,
    int Version,
    IReadOnlyList<CapabilityRule> Rules,
    string ContentHash,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record GrantCapabilityCommand(
    Guid SubjectPrincipalId,
    Guid? SubjectAgentInstanceId,
    CapabilityClaim Claim,
    Guid? RunId,
    Guid? TaskId,
    DateTimeOffset ExpiresAt,
    int MaxUses = 1,
    bool Transferable = false,
    Guid? ParentGrantId = null,
    string Reason = "");

public sealed record CapabilityGrantSnapshot(
    Guid Id,
    Guid TenantId,
    Guid WorkspaceId,
    Guid SubjectPrincipalId,
    Guid? SubjectAgentInstanceId,
    CapabilityClaim Claim,
    Guid? RunId,
    Guid? TaskId,
    Guid? ParentGrantId,
    bool Transferable,
    string Status,
    int MaxUses,
    int UseCount,
    DateTimeOffset StartsAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    string? RevokeReason);

public sealed record CreateApprovalDelegationCommand(
    Guid DelegateAgentVersionId,
    Guid DelegateAgentInstanceId,
    IReadOnlyList<CapabilityRule> Rules,
    string MaxRisk,
    decimal MaxCost,
    int MaxUses,
    DateTimeOffset ExpiresAt,
    Guid? RunId = null,
    bool RequireUserReview = false);

public sealed record ApprovalDelegationSnapshot(
    Guid Id,
    Guid TenantId,
    Guid WorkspaceId,
    Guid DelegatedByPrincipalId,
    Guid DelegateAgentVersionId,
    Guid DelegateAgentInstanceId,
    IReadOnlyList<CapabilityRule> Rules,
    string MaxRisk,
    decimal MaxCost,
    Guid? RunId,
    bool RequireUserReview,
    string Status,
    int MaxUses,
    int UseCount,
    DateTimeOffset StartsAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    string? RevokeReason);

public sealed record PermissionRequestCommand(
    Guid SubjectPrincipalId,
    Guid? SubjectAgentInstanceId,
    Guid? ParentAgentInstanceId,
    CapabilityClaim Claim,
    Guid? RunId,
    Guid? TaskId,
    TimeSpan RequestedDuration,
    int RequestedUses,
    string Risk,
    decimal ExpectedCost,
    string Rationale,
    string IdempotencyKey,
    string? PermissionMode = null);

public sealed record PermissionRequestSnapshot(
    Guid Id,
    Guid TenantId,
    Guid WorkspaceId,
    Guid SubjectPrincipalId,
    Guid? SubjectAgentInstanceId,
    Guid? ParentAgentInstanceId,
    CapabilityClaim Claim,
    Guid? RunId,
    Guid? TaskId,
    string Risk,
    decimal ExpectedCost,
    string Status,
    Guid? AuthorizationDecisionId,
    Guid? CapabilityGrantId,
    Guid? CapabilityLeaseId,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PermissionDecisionCommand(
    Guid PermissionRequestId,
    bool Approve,
    Guid? ApproverAgentInstanceId,
    Guid? ApprovalDelegationId,
    string Reason);

public sealed record PermissionResolution(
    PermissionRequestSnapshot Request,
    AuthorizationDecisionSnapshot Decision,
    CapabilityGrantSnapshot? Grant,
    CapabilityLeaseSnapshot? Lease);

public sealed record AuthorizationEvaluationRequest(
    Guid SubjectPrincipalId,
    Guid? SubjectAgentInstanceId,
    CapabilityClaim Claim,
    Guid? RunId = null,
    Guid? TaskId = null,
    Guid? CapabilityLeaseId = null);

public sealed record AuthorizationDecisionSnapshot(
    Guid Id,
    Guid TenantId,
    Guid WorkspaceId,
    Guid SubjectPrincipalId,
    Guid? SubjectAgentInstanceId,
    CapabilityClaim Claim,
    Guid? RunId,
    Guid? TaskId,
    Guid? PermissionRequestId,
    Guid? CapabilityGrantId,
    Guid? CapabilityLeaseId,
    string Outcome,
    string ReasonCode,
    string Reason,
    string DecisionSource,
    string PolicySnapshotHash,
    Guid DecidedByPrincipalId,
    Guid? DecidedByAgentInstanceId,
    DateTimeOffset CreatedAt);

public sealed record CapabilityLeaseSnapshot(
    Guid Id,
    Guid TenantId,
    Guid WorkspaceId,
    Guid SubjectPrincipalId,
    Guid? SubjectAgentInstanceId,
    Guid CapabilityGrantId,
    Guid? PermissionRequestId,
    CapabilityClaim Claim,
    Guid? RunId,
    Guid? TaskId,
    [property: JsonIgnore] string Nonce,
    string PolicySnapshotHash,
    string Status,
    int MaxUses,
    int UseCount,
    DateTimeOffset StartsAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    string? RevokeReason);

public sealed record LeaseConsumptionCommand(
    Guid CapabilityLeaseId,
    string? Nonce,
    Guid SubjectPrincipalId,
    Guid? SubjectAgentInstanceId,
    CapabilityClaim Claim,
    Guid? RunId,
    Guid? TaskId,
    string IdempotencyKey);

public sealed record LeaseConsumptionResult(
    bool Consumed,
    string ReasonCode,
    CapabilityLeaseSnapshot? Lease,
    AuthorizationDecisionSnapshot Decision);

public sealed record AuthorizationContextRequest(
    Guid TenantId,
    Guid WorkspaceId,
    Guid SubjectPrincipalId,
    Guid? SubjectAgentInstanceId,
    CapabilityClaim Claim,
    Guid? RunId,
    Guid? TaskId);

public sealed record ToolAuthorizationCommand(
    Guid SubjectPrincipalId,
    Guid? SubjectAgentInstanceId,
    CapabilityClaim Claim,
    Guid? RunId,
    Guid? TaskId,
    string Risk,
    decimal ExpectedCost,
    int RequestedUses,
    TimeSpan RequestedDuration,
    string Rationale,
    string IdempotencyKey,
    string? PermissionMode = null);

/// <summary>Publicly safe result for tool authorization; lease nonce is never returned.</summary>
public sealed record ToolAuthorizationResult(
    string Status,
    AuthorizationDecisionSnapshot Decision,
    PermissionRequestSnapshot? PermissionRequest = null,
    Guid? CapabilityLeaseId = null,
    [property: JsonIgnore] string? LeaseNonce = null)
{
    public Guid? AuthorizationDecisionId => Decision.Id;
    public Guid? PermissionRequestId => PermissionRequest?.Id;
    public string? ErrorCategory => Decision.Outcome == GovernanceOutcomes.Allowed ? null : Decision.ReasonCode;
    public string? Message => Decision.Reason;
}

public sealed record ToolLeaseConsumptionCommand(
    Guid CapabilityLeaseId,
    string? Nonce,
    Guid SubjectPrincipalId,
    Guid? SubjectAgentInstanceId,
    CapabilityClaim Claim,
    Guid? RunId,
    Guid? TaskId,
    string IdempotencyKey);
