using System.Text.Json.Serialization;

namespace TinadecCore.DmaEA;

/// <summary>Immutable agent profile projection consumed by the orchestrator.</summary>
public sealed class AgentDefinition
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Layer { get; init; } = string.Empty;
    public string AgentType { get; init; } = string.Empty;
    public string? ModelRoutePurpose { get; init; }
    public string? SystemPrompt { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    public bool Enabled { get; init; }
}

/// <summary>A planned subtask produced by the planning layer.</summary>
public sealed class PlannedTask
{
    [JsonPropertyName("task_key")]
    public string? TaskKey { get; init; }
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;
    [JsonPropertyName("category")]
    public string? Category { get; init; }
    [JsonPropertyName("description")]
    public string? Description { get; init; }
    [JsonPropertyName("success_criteria")]
    public string[] SuccessCriteria { get; init; } = [];
    [JsonPropertyName("dependencies")]
    public string[] Dependencies { get; init; } = [];
    [JsonPropertyName("required_capabilities")]
    public string[] RequiredCapabilities { get; init; } = [];
    [JsonPropertyName("required_tools")]
    public string[] RequiredTools { get; init; } = [];
    [JsonPropertyName("priority")]
    public int Priority { get; init; } = 1;
    [JsonPropertyName("risk")]
    public string Risk { get; init; } = "medium";
}

/// <summary>Outcome of executing one planned task.</summary>
public sealed class StepResult
{
    public Guid TaskNodeId { get; init; }
    public string AgentId { get; init; } = string.Empty;
    public string Status { get; init; } = "completed";
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>
    /// Optional context patch proposed by the worker. The engine applies it
    /// against the task's input context revision, so a result computed on stale
    /// context loses the CAS race and stays audit-only.
    /// </summary>
    public string? ProposedPatchSummary { get; init; }
    public string? ProposedPatchContent { get; init; }
}

/// <summary>Tenant/session/goal context shared across one orchestration run.</summary>
public sealed class DmaeaRunContext
{
    public required Guid SessionId { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required string UserGoal { get; init; }
    public required Guid TriggerMessageId { get; init; }
    public required Guid RunId { get; init; }
}
