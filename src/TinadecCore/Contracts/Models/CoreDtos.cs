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
    string Driver,
    string DisplayName,
    string ConnectionKind,
    string Summary,
    string? DefaultBaseUrl,
    string? DefaultModel,
    IReadOnlyList<string> Capabilities);

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
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ModelRouteDto(
    string Purpose,
    string ProviderInstanceId,
    string? Model,
    DateTimeOffset UpdatedAt);

public sealed record DoctorCheckDto(
    string Name,
    string Status,
    string Message);

public sealed record DoctorReportDto(
    string Platform,
    [property: JsonPropertyName("agent_core_version")]
    string CoreVersion,
    IReadOnlyList<DoctorCheckDto> Checks);

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
    string? RuntimeId);
