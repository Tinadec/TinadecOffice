using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

public sealed class AgentPackEnvelopeDto
{
    [JsonPropertyName("manifest")]
    public AgentPackManifestDto? Manifest { get; init; }

    [JsonPropertyName("integrity")]
    public AgentPackIntegrityDto? Integrity { get; init; }
}

public sealed class AgentPackManifestDto
{
    [JsonPropertyName("api_version")]
    public string? ApiVersion { get; init; }

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("metadata")]
    public AgentPackMetadataDto? Metadata { get; init; }

    [JsonPropertyName("compatibility")]
    public AgentPackCompatibilityDto? Compatibility { get; init; }

    [JsonPropertyName("resources")]
    public AgentPackResourcesDto? Resources { get; init; }

    [JsonPropertyName("activation")]
    public AgentPackActivationDto? Activation { get; init; }
}

public sealed class AgentPackMetadataDto
{
    [JsonPropertyName("pack_id")]
    public string? PackId { get; init; }

    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    [JsonPropertyName("product_id")]
    public string? ProductId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

public sealed class AgentPackCompatibilityDto
{
    [JsonPropertyName("minimum_core_version")]
    public string? MinimumCoreVersion { get; init; }

    [JsonPropertyName("required_core_capabilities")]
    public IReadOnlyList<string> RequiredCoreCapabilities { get; init; } = [];
}

public sealed class AgentPackResourcesDto
{
    [JsonPropertyName("agents")]
    public IReadOnlyList<AgentPackAgentResourceDto> Agents { get; init; } = [];

    [JsonPropertyName("prompt_pipelines")]
    public IReadOnlyList<AgentPackPromptPipelineResourceDto> PromptPipelines { get; init; } = [];

    [JsonPropertyName("modes")]
    public IReadOnlyList<AgentPackModeResourceDto> Modes { get; init; } = [];
}

public sealed class AgentPackAgentResourceDto
{
    [JsonPropertyName("resource_key")]
    public string? ResourceKey { get; init; }

    [JsonPropertyName("slug")]
    public string? Slug { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("layer")]
    public string? Layer { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    [JsonPropertyName("model_strategy")]
    public JsonElement ModelStrategy { get; init; }

    [JsonPropertyName("tool_scope")]
    public IReadOnlyList<string> ToolScope { get; init; } = [];

    [JsonPropertyName("system_prompt")]
    public string? SystemPrompt { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("base_prompt_pipeline_ref")]
    public string? BasePromptPipelineRef { get; init; }
}

public sealed class AgentPackPromptPipelineResourceDto
{
    [JsonPropertyName("resource_key")]
    public string? ResourceKey { get; init; }

    [JsonPropertyName("slug")]
    public string? Slug { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("graph")]
    public JsonElement Graph { get; init; }
}

public sealed class AgentPackModeResourceDto
{
    [JsonPropertyName("resource_key")]
    public string? ResourceKey { get; init; }

    [JsonPropertyName("slug")]
    public string? Slug { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("nodes")]
    public IReadOnlyList<AgentPackModeNodeDto> Nodes { get; init; } = [];

    [JsonPropertyName("edges")]
    public IReadOnlyList<AgentPackModeEdgeDto> Edges { get; init; } = [];

    [JsonPropertyName("canvas_layout")]
    public JsonElement CanvasLayout { get; init; }
}

public sealed class AgentPackModeNodeDto
{
    [JsonPropertyName("node_key")]
    public string? NodeKey { get; init; }

    [JsonPropertyName("agent_ref")]
    public string? AgentRef { get; init; }

    [JsonPropertyName("layer")]
    public string? Layer { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("config")]
    public JsonElement Config { get; init; }

    [JsonPropertyName("position")]
    public JsonElement Position { get; init; }
}

public sealed class AgentPackModeEdgeDto
{
    [JsonPropertyName("edge_key")]
    public string? EdgeKey { get; init; }

    [JsonPropertyName("source_node_key")]
    public string? SourceNodeKey { get; init; }

    [JsonPropertyName("target_node_key")]
    public string? TargetNodeKey { get; init; }

    [JsonPropertyName("condition")]
    public JsonElement Condition { get; init; }
}

public sealed class AgentPackActivationDto
{
    [JsonPropertyName("workspace_defaults")]
    public AgentPackWorkspaceDefaultsDto? WorkspaceDefaults { get; init; }
}

public sealed class AgentPackWorkspaceDefaultsDto
{
    [JsonPropertyName("agent_ref")]
    public string? AgentRef { get; init; }

    [JsonPropertyName("mode_ref")]
    public string? ModeRef { get; init; }

    [JsonPropertyName("prompt_pipeline_ref")]
    public string? PromptPipelineRef { get; init; }
}

public sealed class AgentPackIntegrityDto
{
    [JsonPropertyName("algorithm")]
    public string? Algorithm { get; init; }

    [JsonPropertyName("digest")]
    public string? Digest { get; init; }
}

public sealed class AgentPackApplyRequestDto
{
    [JsonPropertyName("preview_id")]
    public Guid PreviewId { get; init; }

    [JsonPropertyName("envelope")]
    public AgentPackEnvelopeDto? Envelope { get; init; }
}
