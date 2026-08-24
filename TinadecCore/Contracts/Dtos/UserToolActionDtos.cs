using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

public sealed class UserToolActionCreateRequestDto
{
    [JsonPropertyName("project_id")]
    public Guid ProjectId { get; init; }

    [JsonPropertyName("tool_id")]
    public string ToolId { get; init; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }

    [JsonPropertyName("idempotency_key")]
    public string? IdempotencyKey { get; init; }
}

public sealed class UserToolActionSnapshotOverrideRequestDto
{
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;
}

public sealed class UserToolActionRecoveryDecisionRequestDto
{
    [JsonPropertyName("decision")]
    public string Decision { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;
}

public sealed class UserToolActionDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("audit_reference")] public string AuditReference { get; init; } = string.Empty;
    [JsonPropertyName("tenant_id")] public Guid TenantId { get; init; }
    [JsonPropertyName("workspace_id")] public Guid WorkspaceId { get; init; }
    [JsonPropertyName("project_id")] public Guid ProjectId { get; init; }
    [JsonPropertyName("principal_id")] public Guid PrincipalId { get; init; }
    [JsonPropertyName("tool_id")] public string ToolId { get; init; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("risk")] public string Risk { get; init; } = "low";
    [JsonPropertyName("mutates_workspace")] public bool MutatesWorkspace { get; init; }
    [JsonPropertyName("requires_approval")] public bool RequiresApproval { get; init; }
    [JsonPropertyName("permission_request_id")] public Guid? PermissionRequestId { get; init; }
    [JsonPropertyName("authorization_decision_id")] public Guid? AuthorizationDecisionId { get; init; }
    [JsonPropertyName("action_approval_id")] public Guid? ActionApprovalId { get; init; }
    [JsonPropertyName("snapshot_id")] public Guid? SnapshotId { get; init; }
    [JsonPropertyName("snapshot_hash")] public string? SnapshotHash { get; init; }
    [JsonPropertyName("snapshot_override")] public bool SnapshotOverride { get; init; }
    [JsonPropertyName("snapshot_override_reason")] public string? SnapshotOverrideReason { get; init; }
    [JsonPropertyName("non_reversible")] public bool NonReversible { get; init; }
    [JsonPropertyName("compensation_guidance")] public string? CompensationGuidance { get; init; }
    [JsonPropertyName("recovery_decision")] public string? RecoveryDecision { get; init; }
    [JsonPropertyName("recovery_reason")] public string? RecoveryReason { get; init; }
    [JsonPropertyName("recovered_at")] public DateTimeOffset? RecoveredAt { get; init; }
    [JsonPropertyName("result")] public JsonElement? Result { get; init; }
    [JsonPropertyName("error_category")] public string? ErrorCategory { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; init; }
    [JsonPropertyName("completed_at")] public DateTimeOffset? CompletedAt { get; init; }
}
