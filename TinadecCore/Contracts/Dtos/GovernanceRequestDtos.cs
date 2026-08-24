using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

/// <summary>Wire request models for the governance control plane.</summary>
/// <remarks>These DTOs contain no ASP.NET or MAF types.</remarks>
public sealed class PermissionDecisionRequestDto
{
    public bool Approve { get; init; }
    public Guid? ApproverAgentInstanceId { get; init; }
    public Guid? ApprovalDelegationId { get; init; }
    public string? Reason { get; init; }
}

public sealed class CreatePolicyBundleRequestDto
{
    public string? Slug { get; init; }
    public string? DisplayName { get; init; }
    public IReadOnlyList<CapabilityRuleDto>? Rules { get; init; }
    public bool TenantWide { get; init; }
}

public sealed class PublishPolicyVersionRequestDto
{
    public IReadOnlyList<CapabilityRuleDto>? Rules { get; init; }
}

public sealed class GrantCapabilityRequestDto
{
    public Guid SubjectPrincipalId { get; init; }
    public Guid? SubjectAgentInstanceId { get; init; }
    public string? Capability { get; init; }
    public string? Action { get; init; }
    public string? Resource { get; init; }
    public Guid? RunId { get; init; }
    public Guid? TaskId { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public int MaxUses { get; init; } = 1;
    public bool Transferable { get; init; }
    public Guid? ParentGrantId { get; init; }
    public string? Reason { get; init; }
}

public sealed class CreateApprovalDelegationRequestDto
{
    public Guid DelegateAgentVersionId { get; init; }
    public Guid DelegateAgentInstanceId { get; init; }
    public IReadOnlyList<CapabilityRuleDto>? Rules { get; init; }
    public string? MaxRisk { get; init; }
    public decimal MaxCost { get; init; }
    public int MaxUses { get; init; } = 1;
    public DateTimeOffset ExpiresAt { get; init; }
    public Guid? RunId { get; init; }
    public bool RequireUserReview { get; init; }
}

public sealed class RevokeRequestDto
{
    public string? Reason { get; init; }
}

public sealed class CapabilityRuleDto
{
    public string? Effect { get; init; }
    public string? Capability { get; init; }
    public string? Action { get; init; }

    [JsonPropertyName("resource_pattern")]
    public string? ResourcePattern { get; init; }
}
