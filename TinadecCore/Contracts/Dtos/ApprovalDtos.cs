using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

/// <summary>Read-only effective approval policy summary returned by the control plane.</summary>
public sealed class ApprovalPolicySummaryDto
{
    [JsonPropertyName("policy_version")]
    public string PolicyVersion { get; init; } = "tool-approval-v1";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("risk_max")]
    public string RiskMax { get; init; } = "medium";

    [JsonPropertyName("delegated_risk_max")]
    public string DelegatedRiskMax { get; init; } = "medium";

    [JsonPropertyName("max_per_run")]
    public int MaxPerRun { get; init; }

    [JsonPropertyName("release_read_only_in_ask_mode")]
    public bool ReleaseReadOnlyInAskMode { get; init; }

    [JsonPropertyName("human_only_tools")]
    public IReadOnlyList<string> HumanOnlyTools { get; init; } = [];

    [JsonPropertyName("delegation_blocked_tools")]
    public IReadOnlyList<string> DelegationBlockedTools { get; init; } = [];
}

/// <summary>Optional atomic standing rule to create or reuse when approving a pending request.</summary>
public sealed class ApprovalRememberRuleInputDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("pattern")]
    public string? Pattern { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
}

/// <summary>Human decision on a pending approval.</summary>
public sealed class ApprovalDecisionRequestDto
{
    [JsonPropertyName("decision")]
    public string Decision { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>
    /// How far this decision reaches. <c>once</c> (the default) decides only the
    /// request in hand. <c>run</c> means "always allow this tool for this session":
    /// the decision additionally mints a run-scoped capability grant for that tool,
    /// so later calls of it stop asking, and the run's other pending requests for
    /// the same tool are released with it. The scope is always per tool — a
    /// wildcard is never minted from a decision.
    /// </summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("remember_rule")]
    public ApprovalRememberRuleInputDto? RememberRule { get; init; }
}

/// <summary>Decision outcome returned by <c>POST /api/v1/approvals/{id}/decision</c>.</summary>
public sealed class ApprovalDecisionResponseDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("decided_at")]
    public DateTimeOffset? DecidedAt { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("run_scope_released")]
    public int? RunScopeReleased { get; init; }

    [JsonPropertyName("pre_authorization_id")]
    public Guid? PreAuthorizationId { get; init; }

    [JsonPropertyName("capability_grant_id")]
    public Guid? CapabilityGrantId { get; init; }

    [JsonPropertyName("created_rule_id")]
    public Guid? CreatedRuleId { get; init; }

    [JsonPropertyName("decision_reason")]
    public string? DecisionReason { get; init; }

    [JsonPropertyName("decided_by_device_id")]
    public string? DecidedByDeviceId { get; init; }

    [JsonPropertyName("decided_by_device_name")]
    public string? DecidedByDeviceName { get; init; }

    [JsonPropertyName("user_tool_action_id")]
    public Guid? UserToolActionId { get; init; }

    [JsonPropertyName("action")]
    public object? Action { get; init; }
}

/// <summary>Batch human decision over up to 50 pending approvals.</summary>
public sealed class BatchApprovalDecisionRequestDto
{
    [JsonPropertyName("approval_ids")]
    public IReadOnlyList<Guid> ApprovalIds { get; init; } = [];

    [JsonPropertyName("decision")]
    public string Decision { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
}

/// <summary>Per-approval outcome in a batch decision response.</summary>
public sealed class BatchApprovalDecisionItemDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>Batch approval decision response.</summary>
public sealed class BatchApprovalDecisionResponseDto
{
    [JsonPropertyName("results")]
    public IReadOnlyList<BatchApprovalDecisionItemDto> Results { get; init; } = [];
}

/// <summary>
/// Shared human-review payload used by memory and generated-agent candidate routes.
/// It belongs to Contracts so API endpoint types do not leak across module boundaries.
/// </summary>
public sealed class ReviewDecisionRequest
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>Approval projection returned by the control plane.</summary>
public sealed class ApprovalResponseDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("session_id")]
    public Guid? SessionId { get; init; }

    [JsonPropertyName("run_id")]
    public Guid? RunId { get; init; }

    [JsonPropertyName("task_id")]
    public Guid? TaskId { get; init; }

    [JsonPropertyName("project_id")]
    public Guid? ProjectId { get; init; }

    [JsonPropertyName("agent_instance_id")]
    public Guid? AgentInstanceId { get; init; }

    [JsonPropertyName("execution_id")]
    public Guid? ExecutionId { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("tool_id")]
    public string ToolId { get; init; } = string.Empty;

    [JsonPropertyName("risk")]
    public string Risk { get; init; } = "low";

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// Set when <see cref="Summary"/> is one of Core's fixed English sentences rather than a projected fact:
    /// <c>user_requested</c>, <c>retry_after_unknown</c>, <c>permission_request</c>. Clients word it with
    /// <see cref="ToolId"/>; Summary stays the diagnostic text.
    /// </summary>
    [JsonPropertyName("summary_code")]
    public string? SummaryCode { get; init; }

    /// <summary>
    /// Redacted, bounded projection of the tool call parameters — the payload a
    /// reviewer needs to decide. Never the raw parameters: file bodies and MCP
    /// payloads come back as a size plus hash, and secret-shaped keys are dropped.
    /// Empty for rows minted before this field existed.
    /// </summary>
    [JsonPropertyName("arguments")]
    public string Arguments { get; init; } = string.Empty;

    /// <summary>Command text for shell-class calls, when the tool names one.</summary>
    [JsonPropertyName("command")]
    public string? Command { get; init; }

    /// <summary>Working directory the command would run in, when the call sets one.</summary>
    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    /// <summary>Target path or ref the call acts on, when the tool names one.</summary>
    [JsonPropertyName("resource_path")]
    public string? ResourcePath { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("request_hash")]
    public string RequestHash { get; init; } = string.Empty;

    [JsonPropertyName("consumed_by_execution_id")]
    public Guid? ConsumedByExecutionId { get; init; }

    [JsonPropertyName("decision")]
    public string? Decision { get; init; }

    [JsonPropertyName("decision_reason")]
    public string? DecisionReason { get; init; }

    [JsonPropertyName("decided_by_device_id")]
    public string? DecidedByDeviceId { get; init; }

    [JsonPropertyName("decided_by_device_name")]
    public string? DecidedByDeviceName { get; init; }

    [JsonPropertyName("decided_at")]
    public DateTimeOffset? DecidedAt { get; init; }

    [JsonPropertyName("consumed_at")]
    public DateTimeOffset? ConsumedAt { get; init; }

    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }
}
