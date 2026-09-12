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

    // Unknown-member capture (defense in depth): without this, System.Text.Json
    // silently drops unknown manifest members, and because the digest is computed
    // over the re-serialized DTO the dropped bytes would also escape the integrity
    // check. Capturing them keeps both the reader tolerant and the digest honest.
    // Packs that rely on members this Core does not understand must also declare a
    // capability in compatibility.required_core_capabilities (the whitelist gate
    // rejects them fail-closed on older Cores).
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
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

    // Optional (always-v1 discipline): an absent tools list means the legacy
    // behavior — agent tool_scope entries are plain id references resolved against
    // the frozen TinadecTools manifest. Declared tools add the two supported tiers:
    // "reference" (id + hash-pin) and "definition" (controlled import registry).
    // Packs carrying this section must declare the graph_mode_packs core capability.
    // Nullable + omitted-when-absent so re-serialization (and therefore the
    // manifest digest) stays byte-identical for packs that predate this field.
    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AgentPackToolResourceDto>? Tools { get; init; }
}

public sealed class AgentPackToolResourceDto
{
    [JsonPropertyName("tool_id")]
    public string? ToolId { get; init; }

    // "reference" | "definition"
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    // reference tier: sha256 of the pinned TinadecTools manifest entry; a drift
    // between the pinned hash and the live entry fails the pack admission gate.
    [JsonPropertyName("entry_hash")]
    public string? EntryHash { get; init; }

    // definition tier
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    // definition manifest: names/refs/schema only — inline secret values are
    // rejected by the publish gate, never stored. Nullable: reference-tier tools
    // omit it, and a default JsonElement cannot be re-serialized for the digest.
    [JsonPropertyName("definition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Definition { get; init; }
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

    // Optional per-node mode bindings (see resources.tools note: omitted when
    // absent so legacy digests stay byte-identical).
    [JsonPropertyName("bindings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AgentPackModeBindingDto>? Bindings { get; init; }

    [JsonPropertyName("canvas_layout")]
    public JsonElement CanvasLayout { get; init; }
}

public sealed class AgentPackModeBindingDto
{
    [JsonPropertyName("node_key")]
    public string? NodeKey { get; init; }

    [JsonPropertyName("agent_ref")]
    public string? AgentRef { get; init; }

    [JsonPropertyName("duty_description_ref")]
    public string? DutyDescriptionRef { get; init; }

    // json: { "<tool_id>": true|false } — can only narrow the template's tool scope
    [JsonPropertyName("tool_switches")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ToolSwitches { get; init; }

    // json: { "spawn": {"max_depth":..,"max_agents_per_run":..,"max_parallel_workers":..},
    //         "capabilities": [...], "tools": [...] } — envelope, narrowing only
    [JsonPropertyName("envelope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Envelope { get; init; }

    // R2: this mode legitimately relies on Core-reserved capabilities (e.g. the
    // synthetic create_workspace ceiling that is deliberately not intersected with
    // tool_scope); the publish gate skips envelope containment for them.
    [JsonPropertyName("includes_core_reserved")]
    public bool IncludesCoreReserved { get; init; }

    [JsonPropertyName("instance_naming")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? InstanceNaming { get; init; }
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

    // Optional relationship description file (per-node duty contract). Five fields:
    // duty / inputs_outputs / allowed_dispatch_targets / success_criteria /
    // agent_types. Capped at 16KB serialized; compiled into the role system prompt
    // through a fixed tail slot that participates in the prompt hash. Nullable +
    // omitted-when-absent: a default JsonElement cannot be re-serialized, and an
    // always-written empty element would change legacy manifest digests.
    [JsonPropertyName("relationship")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Relationship { get; init; }

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
