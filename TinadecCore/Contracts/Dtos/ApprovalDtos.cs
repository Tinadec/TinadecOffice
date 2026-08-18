using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

/// <summary>
/// Control-plane approval creation request. The server canonicalizes <c>parameters</c>
/// and computes <c>request_hash</c> itself; client-supplied hashes are never trusted.
/// </summary>
public sealed class ApprovalCreateRequestDto
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("run_id")]
    public string? RunId { get; init; }

    [JsonPropertyName("task_id")]
    public string? TaskId { get; init; }

    [JsonPropertyName("agent_instance_id")]
    public string? AgentInstanceId { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "tool";

    [JsonPropertyName("tool_id")]
    public string? ToolId { get; init; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }
}

/// <summary>Human decision on a pending approval.</summary>
public sealed class ApprovalDecisionRequestDto
{
    [JsonPropertyName("decision")]
    public string Decision { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
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
