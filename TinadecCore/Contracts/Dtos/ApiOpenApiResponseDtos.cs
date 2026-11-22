using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

public sealed class InteractionCreatedResponseDto
{
    [JsonPropertyName("interaction_id")]
    public Guid InteractionId { get; init; }

    [JsonPropertyName("session_id")]
    public Guid SessionId { get; init; }

    [JsonPropertyName("run_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RunId { get; init; }

    [JsonPropertyName("turn_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? TurnId { get; init; }

    [JsonPropertyName("message_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? MessageId { get; init; }

    [JsonPropertyName("dispatch_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DispatchMode { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("mode_version_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ModeVersionId { get; init; }

    [JsonPropertyName("meeting_model_override")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MeetingModelOverrideDto? MeetingModelOverride { get; init; }

    [JsonPropertyName("attachment_ids")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Guid>? AttachmentIds { get; init; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    [JsonPropertyName("client_message_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientMessageId { get; init; }

    [JsonPropertyName("context_revision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ContextRevision { get; init; }

    [JsonPropertyName("stream_cursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StreamCursor { get; init; }

    [JsonPropertyName("correlation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("interrupt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Interrupt { get; init; }

    [JsonPropertyName("interrupted_model_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? InterruptedModelCalls { get; init; }
}

public sealed class InteractionMutationResponseDto
{
    [JsonPropertyName("interaction_id")]
    public Guid InteractionId { get; init; }

    [JsonPropertyName("run_id")]
    public Guid RunId { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("dispatch_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DispatchMode { get; init; }

    [JsonPropertyName("target_run_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? TargetRunId { get; init; }

    [JsonPropertyName("action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Action { get; init; }
}

public sealed class InteractionSummaryResponseDto
{
    [JsonPropertyName("interaction_id")]
    public Guid? InteractionId { get; init; }

    [JsonPropertyName("run_id")]
    public Guid RunId { get; init; }

    [JsonPropertyName("session_id")]
    public Guid SessionId { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class RunOrchestrationSummaryDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("user_message_id")]
    public string UserMessageId { get; init; } = string.Empty;

    [JsonPropertyName("turn_id")]
    public string? TurnId { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("runtime_profile_id")]
    public string? RuntimeProfileId { get; init; }

    [JsonPropertyName("mode_version_id")]
    public Guid? ModeVersionId { get; init; }

    [JsonPropertyName("config_version")]
    public int ConfigVersion { get; init; }

    [JsonPropertyName("config_hash")]
    public string? ConfigHash { get; init; }

    [JsonPropertyName("context_revision")]
    public long ContextRevision { get; init; }

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class RunOrchestrationResponseDto
{
    [JsonPropertyName("run")]
    public RunOrchestrationSummaryDto Run { get; init; } = new();

    [JsonPropertyName("frozen")]
    public JsonElement? Frozen { get; init; }

    [JsonPropertyName("graph")]
    public JsonElement? Graph { get; init; }

    [JsonPropertyName("nodes")]
    public IReadOnlyList<JsonElement> Nodes { get; init; } = [];

    [JsonPropertyName("lanes")]
    public IReadOnlyList<JsonElement> Lanes { get; init; } = [];

    [JsonPropertyName("flows")]
    public IReadOnlyList<JsonElement> Flows { get; init; } = [];

    [JsonPropertyName("assignments")]
    public IReadOnlyList<JsonElement> Assignments { get; init; } = [];

    [JsonPropertyName("step_results")]
    public IReadOnlyList<JsonElement> StepResults { get; init; } = [];

    [JsonPropertyName("agent_instances")]
    public IReadOnlyList<JsonElement> AgentInstances { get; init; } = [];

    [JsonPropertyName("supervision_findings")]
    public IReadOnlyList<JsonElement> SupervisionFindings { get; init; } = [];

    [JsonPropertyName("context_packs")]
    public IReadOnlyList<JsonElement> ContextPacks { get; init; } = [];
}

public sealed class WorkspaceSnapshotResponseDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("tenant_id")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("workspace_id")]
    public Guid WorkspaceId { get; init; }

    [JsonPropertyName("project_id")]
    public Guid ProjectId { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("is_git")]
    public bool IsGit { get; init; }

    [JsonPropertyName("workspace_hash")]
    public string WorkspaceHash { get; init; } = string.Empty;

    [JsonPropertyName("content_reference")]
    public string? ContentReference { get; init; }

    [JsonPropertyName("content_hash")]
    public string? ContentHash { get; init; }

    [JsonPropertyName("content_length")]
    public long ContentLength { get; init; }

    [JsonPropertyName("file_count")]
    public int FileCount { get; init; }

    [JsonPropertyName("base_snapshot_id")]
    public Guid? BaseSnapshotId { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class WorkspaceRestoreResponseDto
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("snapshot_id")]
    public Guid SnapshotId { get; init; }

    [JsonPropertyName("workspace_hash")]
    public string WorkspaceHash { get; init; } = string.Empty;

    [JsonPropertyName("conflicts")]
    public IReadOnlyList<string> Conflicts { get; init; } = [];

    [JsonPropertyName("applied_file_count")]
    public int AppliedFileCount { get; init; }

    [JsonPropertyName("restored_at")]
    public DateTimeOffset RestoredAt { get; init; }
}

public sealed class WorkspaceFileChangeResponseDto
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("before_sha256")]
    public string? BeforeSha256 { get; init; }

    [JsonPropertyName("after_sha256")]
    public string? AfterSha256 { get; init; }

    [JsonPropertyName("before_length")]
    public long? BeforeLength { get; init; }

    [JsonPropertyName("after_length")]
    public long? AfterLength { get; init; }

    [JsonPropertyName("restorable")]
    public bool Restorable { get; init; }
}

public sealed class WorkspaceFileSideResponseDto
{
    [JsonPropertyName("present")]
    public bool Present { get; init; }

    [JsonPropertyName("length")]
    public long? Length { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("binary")]
    public bool Binary { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

public sealed class WorkspaceFileDiffResponseDto
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("restorable")]
    public bool Restorable { get; init; }

    [JsonPropertyName("before")]
    public WorkspaceFileSideResponseDto Before { get; init; } = new();

    [JsonPropertyName("after")]
    public WorkspaceFileSideResponseDto After { get; init; } = new();
}

public sealed class AgentDefinitionResponseDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("i18n")]
    public JsonElement? I18n { get; init; }

    [JsonPropertyName("layer")]
    public string Layer { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("source_kind")]
    public string SourceKind { get; init; } = string.Empty;

    [JsonPropertyName("source_key")]
    public string SourceKey { get; init; } = string.Empty;

    [JsonPropertyName("provenance")]
    public JsonElement? Provenance { get; init; }

    [JsonPropertyName("base_prompt_pipeline_id")]
    public Guid? BasePromptPipelineId { get; init; }

    [JsonPropertyName("managed")]
    public bool Managed { get; init; }

    [JsonPropertyName("writable")]
    public bool Writable { get; init; }

    [JsonPropertyName("capabilities")]
    public JsonElement? Capabilities { get; init; }

    [JsonPropertyName("model_strategy")]
    public JsonElement? ModelStrategy { get; init; }

    [JsonPropertyName("tool_scope")]
    public JsonElement? ToolScope { get; init; }

    [JsonPropertyName("system_prompt")]
    public string? SystemPrompt { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("archived_at")]
    public DateTimeOffset? ArchivedAt { get; init; }
}

public sealed class AgentPublishResponseDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("snapshot")]
    public AgentDefinitionResponseDto Snapshot { get; init; } = new();
}

public sealed class AgentModeSummaryResponseDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; init; }

    [JsonPropertyName("i18n")]
    public JsonElement? I18n { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("archived_at")]
    public DateTimeOffset? ArchivedAt { get; init; }

    [JsonPropertyName("runtime_policy")]
    public JsonElement? RuntimePolicy { get; init; }

    [JsonPropertyName("orchestration_tier")]
    public string? OrchestrationTier { get; init; }

    [JsonPropertyName("default_options")]
    public JsonElement? DefaultOptions { get; init; }

    [JsonPropertyName("latest_published_mode_version_id")]
    public Guid? LatestPublishedModeVersionId { get; init; }

    [JsonPropertyName("pack_id")]
    public string? PackId { get; init; }

    [JsonPropertyName("pack_managed")]
    public bool PackManaged { get; init; }
}

public sealed class AgentModeTopologyResponseDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("orchestration_tier")]
    public string? OrchestrationTier { get; init; }

    [JsonPropertyName("default_options")]
    public JsonElement? DefaultOptions { get; init; }

    [JsonPropertyName("runtime_policy")]
    public JsonElement? RuntimePolicy { get; init; }

    [JsonPropertyName("bindings")]
    public IReadOnlyList<JsonElement> Bindings { get; init; } = [];

    [JsonPropertyName("nodes")]
    public IReadOnlyList<JsonElement> Nodes { get; init; } = [];

    [JsonPropertyName("edges")]
    public IReadOnlyList<JsonElement> Edges { get; init; } = [];

    [JsonPropertyName("canvas_layout")]
    public JsonElement? CanvasLayout { get; init; }

    [JsonPropertyName("latest_published_mode_version_id")]
    public Guid? LatestPublishedModeVersionId { get; init; }

    [JsonPropertyName("pack_id")]
    public string? PackId { get; init; }

    [JsonPropertyName("pack_managed")]
    public bool PackManaged { get; init; }
}

public sealed class ModeDeletedResponseDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("deleted")]
    public bool Deleted { get; init; }
}
