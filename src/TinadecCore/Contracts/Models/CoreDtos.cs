using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tinadec.Contracts.Models;

public sealed record ProjectDto(
    string Id,
    string Name,
    string Path,
    DateTimeOffset CreatedAt);

public sealed record SessionDto(
    string Id,
    string ProjectId,
    string Title,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MessageDto(
    string Id,
    string SessionId,
    string Role,
    string Content,
    DateTimeOffset CreatedAt);

public sealed record ApprovalDto(
    string Id,
    string? SessionId,
    string Kind,
    string Summary,
    string? Command,
    string? Cwd,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt);

public sealed record ModelSettingsDto(
    string BaseUrl,
    string Model,
    bool HasApiKey,
    DateTimeOffset UpdatedAt);

public sealed record ModelProviderTemplateDto(
    string ProviderFamily,
    string Driver,
    string DisplayName,
    string ConnectionKind,
    string CredentialKind,
    string Summary,
    string ContributorDescription,
    string? DefaultBaseUrl,
    string? DefaultModel,
    int DefaultTimeoutSeconds,
    ProviderCapabilityDto Capabilities);

public sealed record ModelProviderInstanceDto(
    string Id,
    string Driver,
    string DisplayName,
    string ConnectionKind,
    string? BaseUrl,
    string? Model,
    bool HasApiKey,
    string? BinaryPath,
    string? HomePath,
    string? ServerUrl,
    string? LaunchArgs,
    IReadOnlyList<string> Capabilities,
    bool Enabled,
    string Status,
    string StatusMessage,
    DateTimeOffset? CooldownUntil,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ModelRouteDto(
    string Purpose,
    string ProviderInstanceId,
    string? Model,
    DateTimeOffset UpdatedAt);

public sealed record ModelProviderReadinessDto(
    string ProviderInstanceId,
    string DisplayName,
    string Driver,
    string ConnectionKind,
    string Status,
    string ProviderStatus,
    bool Enabled,
    bool HasCredential,
    IReadOnlyList<string> RoutePurposes,
    string Summary,
    IReadOnlyList<string> Evidence);

public sealed record ModelRouteReadinessDto(
    string Purpose,
    string? ProviderInstanceId,
    string? ProviderDisplayName,
    string? Model,
    string Status,
    string Summary,
    IReadOnlyList<string> Evidence);

public sealed record ModelReadinessReceiptDto(
    string Status,
    DateTimeOffset GeneratedAt,
    string ReceiptId,
    int ProviderCount,
    int ReadyProviderCount,
    int WarningProviderCount,
    int BlockedProviderCount,
    int RouteCount,
    int ReadyRouteCount,
    int WarningRouteCount,
    int BlockedRouteCount,
    IReadOnlyList<ModelProviderReadinessDto> Providers,
    IReadOnlyList<ModelRouteReadinessDto> Routes,
    IReadOnlyList<string> DesignNotes);

public sealed record DoctorCheckDto(
    string Name,
    string Status,
    string Message);

public sealed record DoctorReportDto(
    string Platform,
    [property: JsonPropertyName("agent_core_version")]
    string CoreVersion,
    IReadOnlyList<DoctorCheckDto> Checks);

public sealed record RuntimeReadinessComponentDto(
    string Id,
    string Name,
    string Status,
    string Summary,
    IReadOnlyList<string> Evidence);

public sealed record RuntimeReadinessReceiptDto(
    string Status,
    DateTimeOffset GeneratedAt,
    string Runtime,
    string ReceiptId,
    IReadOnlyList<RuntimeReadinessComponentDto> Components,
    int ReadyCount,
    int WarningCount,
    int BlockedCount);

public sealed record ExtensionSourceDto(
    string Id,
    string Name,
    string Kind,
    string Location,
    bool Enabled,
    DateTimeOffset? LastRefreshedAt,
    DateTimeOffset CreatedAt);

public sealed record MarketCatalogItemDto(
    string CatalogId,
    string SourceId,
    string ExtensionId,
    string Kind,
    string Version,
    string Publisher,
    string DisplayName,
    string Description,
    string SourceKind,
    string SourceLocation,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Permissions,
    string Status,
    string? InstalledExtensionId);

public sealed record InstalledExtensionDto(
    string Id,
    string? CatalogId,
    string ExtensionId,
    string Kind,
    string Version,
    string Publisher,
    string DisplayName,
    string Description,
    string SourceKind,
    string SourceLocation,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Permissions,
    bool Enabled,
    string Status,
    string StatusMessage,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt);

public sealed record ExtensionInstallPreviewDto(
    string ExtensionId,
    string Kind,
    string Version,
    string Publisher,
    string DisplayName,
    string Description,
    string SourceKind,
    string SourceLocation,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Risks,
    bool RequiresApproval,
    string ApprovalSummary);

public sealed record ExtensionInstallResultDto(
    bool ApprovalRequired,
    ApprovalDto? Approval,
    InstalledExtensionDto? Extension,
    ExtensionInstallPreviewDto Preview);

public sealed record McpServerDto(
    string Id,
    string ExtensionId,
    string Name,
    string Transport,
    string Status,
    IReadOnlyList<string> Tools,
    DateTimeOffset UpdatedAt);

public sealed record AcpAdapterDto(
    string Id,
    string ExtensionId,
    string Name,
    string Command,
    string Status,
    string StatusMessage,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset UpdatedAt);

public sealed record AgentProfileDto(
    string Id,
    string Name,
    string Layer,
    string AgentType,
    string Mode,
    string Description,
    string ModelRoutePurpose,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> Capabilities,
    string? SystemPrompt,
    bool Enabled,
    bool IsBuiltIn,
    DateTimeOffset UpdatedAt);

public sealed record AgentModeDto(
    string Id,
    string DisplayName,
    string Summary,
    int MaxParallelExecutors,
    bool WorktreeIsolation,
    bool ApprovalRequired,
    string BudgetPolicy);

public sealed record AgentCandidateDto(
    string Id,
    string GeneratedByAgentId,
    string Name,
    string Layer,
    string AgentType,
    string Description,
    IReadOnlyList<string> SuggestedTools,
    IReadOnlyList<string> EvaluationNotes,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record PromptFragmentDto(
    string Id,
    string Key,
    string Title,
    string Scope,
    string? TargetAgentId,
    string Category,
    string Content,
    int Priority,
    bool Enabled,
    [property: JsonPropertyName("is_builtin")]
    bool IsBuiltIn,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record OrchestrationRunDto(
    string Id,
    string SessionId,
    string? UserMessageId,
    string Status,
    string Summary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record TaskGraphDto(
    string Id,
    string RunId,
    string SessionId,
    string Title,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record TaskNodeDto(
    string Id,
    string GraphId,
    string RunId,
    string SessionId,
    string Title,
    string Description,
    string Status,
    int Priority,
    string Risk,
    IReadOnlyList<string> SuccessCriteria,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> RequiredCapabilities,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AgentAssignmentDto(
    string Id,
    string RunId,
    string TaskNodeId,
    string AgentId,
    string AgentName,
    string AgentLayer,
    string AgentType,
    string ModelRoutePurpose,
    string PermissionMode,
    IReadOnlyList<string> AllowedTools,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record StepResultDto(
    string Id,
    string RunId,
    string TaskNodeId,
    string AgentId,
    string Status,
    string Summary,
    IReadOnlyList<string> Evidence,
    DateTimeOffset CreatedAt);

public sealed record ContextPackDto(
    string Id,
    string RunId,
    string SessionId,
    string CreatedByAgentId,
    string Summary,
    int TokenBudget,
    double CompressionRatio,
    IReadOnlyList<string> EvidenceMap,
    DateTimeOffset CreatedAt);

public sealed record PromptContextPreviewDto(
    string AgentId,
    string Mode,
    IReadOnlyList<PromptFragmentDto> Fragments,
    IReadOnlyList<string> ContextPackIds,
    int EstimatedTokens,
    string SystemPrompt,
    IReadOnlyList<string> Warnings);

public sealed record PromptContextPlanDto(
    string RunId,
    string AgentId,
    string Strategy,
    IReadOnlyList<string> SelectedFragmentIds,
    string Summary,
    string CreatedByAgentId);

public sealed record PromptContextPlanningInput(
    string SessionId,
    string? RunId,
    string AgentId,
    string Mode,
    IReadOnlyList<PromptFragmentDto> CandidateFragments,
    IReadOnlyList<ContextPackDto> ContextPacks,
    IReadOnlyList<TaskNodeDto> TaskNodes,
    IReadOnlyList<ToolDescriptorDto> Tools,
    string UserContent,
    bool IsComplex);

public sealed record SupervisionFindingDto(
    string Id,
    string RunId,
    string SessionId,
    string Severity,
    string Category,
    string Summary,
    string Recommendation,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record OrchestrationSnapshotDto(
    OrchestrationRunDto? Run,
    TaskGraphDto? Graph,
    IReadOnlyList<TaskNodeDto> Nodes,
    IReadOnlyList<AgentAssignmentDto> Assignments,
    IReadOnlyList<StepResultDto> StepResults,
    IReadOnlyList<ContextPackDto> ContextPacks,
    IReadOnlyList<SupervisionFindingDto> SupervisionFindings);

public sealed record ToolDescriptorDto(
    string Id,
    string DisplayName,
    string Domain,
    string Source,
    string Risk,
    bool RequiresApproval,
    string ExecuteEndpoint,
    IReadOnlyList<string> Capabilities);

public sealed record ToolSearchResultDto(
    ToolDescriptorDto Tool,
    int Score,
    IReadOnlyList<string> MatchedFields,
    string ProviderLayer,
    bool RequiresHumanCheckpoint,
    string ApprovalSummary);

public sealed record ToolExecutionTimelineItemDto(
    string Id,
    string RunId,
    string SessionId,
    string ToolId,
    string ToolDisplayName,
    string Source,
    string ProviderLayer,
    string Risk,
    bool RequiresApproval,
    string Status,
    string? ApprovalId,
    string? StepResultId,
    string Summary,
    IReadOnlyList<string> Evidence,
    DateTimeOffset RequestedAt,
    DateTimeOffset UpdatedAt,
    long DurationMs,
    long RequestedSeq,
    long UpdatedSeq,
    IReadOnlyList<string> EventTypes,
    string CheckpointSummary);

public sealed record AgentLayerManifestDto(
    string Layer,
    string Role,
    int AgentCount,
    int EnabledAgentCount,
    int MaxParallelExecutors,
    bool WorktreeIsolation,
    bool ApprovalRequired,
    IReadOnlyList<string> AgentTypes,
    IReadOnlyList<string> ToolIds);

public sealed record ToolProviderManifestDto(
    string Source,
    string DisplayName,
    string Layer,
    string Status,
    int ToolCount,
    int ActiveToolCount,
    int FutureToolCount,
    int ApprovalRequiredCount,
    int ReadOnlyCount,
    IReadOnlyList<string> CapabilityPrefixes);

public sealed record ToolRiskManifestDto(
    string Risk,
    int ToolCount,
    bool RequiresHumanCheckpoint,
    string PolicySummary);

public sealed record ToolRegistrySummaryDto(
    int DeclaredToolCount,
    int CanonicalToolCount,
    int DuplicateToolIdCount,
    IReadOnlyList<string> DuplicateToolIds,
    IReadOnlyList<string> SourcePrecedence,
    string SelectionPolicy);

public sealed record HarnessManifestDto(
    string Runtime,
    string OwnershipModel,
    ToolRegistrySummaryDto ToolRegistry,
    IReadOnlyList<AgentLayerManifestDto> AgentLayers,
    IReadOnlyList<ToolProviderManifestDto> ToolProviders,
    IReadOnlyList<ToolRiskManifestDto> ToolRisks,
    IReadOnlyList<ToolDescriptorDto> Tools,
    IReadOnlyList<string> DesignNotes);

public sealed record AgentWorkflowStepDto(
    string Id,
    string RunId,
    string TaskNodeId,
    string AgentId,
    string AgentType,
    string Runtime,
    string PermissionMode,
    IReadOnlyList<string> ToolIds,
    string Status);

public sealed record AgentWorkflowPlanDto(
    string RunId,
    string Runtime,
    IReadOnlyList<AgentWorkflowStepDto> Steps);

public sealed record CodeToolExecuteResultDto(
    string ToolId,
    string Status,
    string Summary,
    IReadOnlyList<string> Evidence,
    IReadOnlyDictionary<string, object?> Data,
    bool RequiresApproval,
    string? ApprovalSummary);

public sealed record ToolExecutionResponseDto(
    string Status,
    ToolDescriptorDto Tool,
    ApprovalDto? Approval,
    CodeToolExecuteResultDto? Result,
    StepResultDto? StepResult);

public sealed record ResolvedModelInvocationContextDto(
    string Purpose,
    ModelRouteDto? Route,
    ModelProviderInstanceDto? Provider,
    string EffectiveBaseUrl,
    string EffectiveModel,
    string? EncryptedApiKey,
    string? Driver,
    string ConnectionKind,
    string ProviderInstanceId,
    bool IsFallbackProvider);

public sealed record ModelInvocationResultDto(
    string Status,
    string Content,
    ResolvedModelInvocationContextDto Context,
    bool UsedStubResponse,
    string? RuntimeId,
    ProviderErrorCategory? ErrorCategory = null,
    bool IsRetryable = false,
    int? ProviderStatusCode = null,
    int? ProviderExitCode = null,
    string? SafeErrorMessage = null,
    string? ErrorProviderId = null);

public sealed record ModelInvocationRequestDto(
    IReadOnlyList<MessageDto> Messages,
    string? SystemPrompt,
    IReadOnlyList<JsonObject> Tools,
    ModelSettingsDto Settings,
    ModelStateHandleDto? StateHandle);

public sealed record ModelInvocationResponseDto(
    string TextContent,
    ModelUsageDto Usage,
    ModelFinishReason FinishReason,
    ProviderMetadataDto Metadata,
    ModelStateHandleDto? StateHandle,
    ProviderErrorCategory? ErrorCategory,
    string? ErrorMessage);

public sealed record ModelUsageDto(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens);

[JsonConverter(typeof(JsonStringEnumConverter<ModelFinishReason>))]
public enum ModelFinishReason
{
    [JsonStringEnumMemberName("stop")]
    Stop,
    [JsonStringEnumMemberName("length")]
    Length,
    [JsonStringEnumMemberName("content_filter")]
    ContentFilter,
    [JsonStringEnumMemberName("tool_calls")]
    ToolCalls,
    [JsonStringEnumMemberName("error")]
    Error,
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

public sealed record ProviderMetadataDto(
    string ProviderId,
    string Model,
    string? RawProviderName,
    IReadOnlyDictionary<string, object?> Custom);

[JsonConverter(typeof(JsonStringEnumConverter<ProviderErrorCategory>))]
public enum ProviderErrorCategory
{
    [JsonStringEnumMemberName("authentication_failed")]
    AuthenticationFailed,
    [JsonStringEnumMemberName("rate_limited")]
    RateLimited,
    [JsonStringEnumMemberName("timeout")]
    Timeout,
    [JsonStringEnumMemberName("provider_unavailable")]
    ProviderUnavailable,
    [JsonStringEnumMemberName("invalid_request")]
    InvalidRequest,
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

public sealed record ProviderCapabilityDto(
    bool SupportsStreaming,
    bool SupportsTools,
    bool SupportsJsonMode,
    bool SupportsSystemPrompt,
    int? MaxContextTokens,
    bool RequiresWorkspace,
    string CredentialKind,
    ProviderHealthStatus HealthStatus);

[JsonConverter(typeof(JsonStringEnumConverter<ProviderHealthStatus>))]
public enum ProviderHealthStatus
{
    [JsonStringEnumMemberName("healthy")]
    Healthy,
    [JsonStringEnumMemberName("unhealthy")]
    Unhealthy,
    [JsonStringEnumMemberName("unknown")]
    Unknown,
    [JsonStringEnumMemberName("disabled")]
    Disabled,
    [JsonStringEnumMemberName("cooldown")]
    Cooldown
}

public sealed record ModelStateHandleDto(
    string Handle,
    DateTimeOffset? ExpiresAt);
