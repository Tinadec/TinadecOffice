using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

public sealed record PromptFragmentDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("scope")]
    public string Scope { get; init; } = "workspace";

    [JsonPropertyName("target_agent_id")]
    public Guid? TargetAgentId { get; init; }

    [JsonPropertyName("target_agent_slug")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetAgentSlug { get; init; }

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("is_builtin")]
    public bool IsBuiltIn { get; init; }

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record PromptFragmentVersionDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("fragment_id")]
    public Guid FragmentId { get; init; }

    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("changed_fields")]
    public IReadOnlyList<string> ChangedFields { get; init; } = [];

    [JsonPropertyName("change_summary")]
    public string ChangeSummary { get; init; } = string.Empty;

    [JsonPropertyName("is_active")]
    public bool IsActive { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record PromptFragmentSaveRequestDto
{
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("target_agent_id")]
    public string? TargetAgentId { get; init; }

    [JsonPropertyName("target_agent_slug")]
    public string? TargetAgentSlug { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("priority")]
    public int? Priority { get; init; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("change_summary")]
    public string? ChangeSummary { get; init; }
}

public sealed record PromptFragmentVersionCreateRequestDto
{
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("changed_fields")]
    public IReadOnlyList<string>? ChangedFields { get; init; }

    [JsonPropertyName("change_summary")]
    public string? ChangeSummary { get; init; }
}

public sealed record PromptFragmentRollbackRequestDto
{
    [JsonPropertyName("target_version")]
    public int? TargetVersion { get; init; }

    [JsonPropertyName("version_id")]
    public Guid? VersionId { get; init; }

    [JsonPropertyName("change_summary")]
    public string? ChangeSummary { get; init; }
}

public sealed record PromptFragmentCloneRequestDto
{
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("target_agent_id")]
    public string? TargetAgentId { get; init; }

    [JsonPropertyName("target_agent_slug")]
    public string? TargetAgentSlug { get; init; }
}

public sealed record PromptContextPreviewRequestDto
{
    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("mode_id")]
    public Guid? ModeId { get; init; }

    [JsonPropertyName("mode_version_id")]
    public Guid? ModeVersionId { get; init; }

    [JsonPropertyName("pipeline_id")]
    public Guid? PipelineId { get; init; }

    [JsonPropertyName("draft_system_prompt")]
    public string? DraftSystemPrompt { get; init; }

    [JsonPropertyName("draft_mode_instructions")]
    public string? DraftModeInstructions { get; init; }

    [JsonPropertyName("draft_pipeline_graph")]
    public JsonElement? DraftPipelineGraph { get; init; }

    [JsonPropertyName("include_fragment_ids")]
    public IReadOnlyList<Guid>? IncludeFragmentIds { get; init; }

    [JsonPropertyName("session_id")]
    public Guid? SessionId { get; init; }

    [JsonPropertyName("run_id")]
    public Guid? RunId { get; init; }

    [JsonPropertyName("user_content")]
    public string? UserContent { get; init; }
}

public sealed record PromptContextLayerDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("estimated_tokens")]
    public int EstimatedTokens { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;
}

public sealed record PromptContextPreviewResponseDto
{
    [JsonPropertyName("assembled_prompt")]
    public string AssembledPrompt { get; init; } = string.Empty;

    [JsonPropertyName("total_estimated_tokens")]
    public int TotalEstimatedTokens { get; init; }

    [JsonPropertyName("layers")]
    public IReadOnlyList<PromptContextLayerDto> Layers { get; init; } = [];

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [JsonPropertyName("agent_id")]
    public string AgentId { get; init; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = string.Empty;

    [JsonPropertyName("estimated_tokens")]
    public int EstimatedTokens { get; init; }

    [JsonPropertyName("system_prompt")]
    public string SystemPrompt { get; init; } = string.Empty;

    [JsonPropertyName("fragments")]
    public IReadOnlyList<PromptFragmentDto> Fragments { get; init; } = [];

    [JsonPropertyName("context_pack_ids")]
    public IReadOnlyList<string> ContextPackIds { get; init; } = [];
}
