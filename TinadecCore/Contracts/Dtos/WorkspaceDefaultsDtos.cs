using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

public sealed class WorkspaceDefaultsRequestDto
{
    [JsonPropertyName("default_agent_definition_id")]
    public Guid? DefaultAgentDefinitionId { get; init; }

    [JsonPropertyName("default_agent_mode_id")]
    public Guid? DefaultAgentModeId { get; init; }

    [JsonPropertyName("default_prompt_pipeline_id")]
    public Guid? DefaultPromptPipelineId { get; init; }

    [JsonPropertyName("default_agent_version_id")]
    public Guid? DefaultAgentVersionId { get; init; }

    [JsonPropertyName("default_mode_version_id")]
    public Guid? DefaultModeVersionId { get; init; }

    [JsonPropertyName("default_prompt_version_id")]
    public Guid? DefaultPromptVersionId { get; init; }
}
