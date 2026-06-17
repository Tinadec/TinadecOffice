namespace Tinadec.Contracts.Models;

public sealed record CreateProjectRequest(string Name, string Path);

public sealed record CreateSessionRequest(string ProjectId, string? Title);

public sealed record PostMessageRequest(string Content);

public sealed record CreateApprovalRequest(
    string? SessionId,
    string Kind,
    string Summary,
    string? Command,
    string? Cwd);

public sealed record DecideApprovalRequest(string Decision);

public sealed record SaveModelSettingsRequest(
    string BaseUrl,
    string Model,
    string? ApiKey,
    bool ClearApiKey = false);

public sealed record SaveModelProviderInstanceRequest(
    string? Id,
    string Driver,
    string DisplayName,
    string ConnectionKind,
    string? BaseUrl,
    string? Model,
    string? ApiKey,
    bool ClearApiKey,
    string? BinaryPath,
    string? HomePath,
    string? ServerUrl,
    string? LaunchArgs,
    IReadOnlyList<string>? Capabilities,
    bool Enabled = true);

public sealed record SaveModelRouteRequest(
    string ProviderInstanceId,
    string? Model);

public sealed record CreateExtensionSourceRequest(
    string Name,
    string Kind,
    string Location,
    bool Enabled = true);

public sealed record InstallExtensionPreviewRequest(
    string? CatalogId,
    string? SourceKind,
    string? SourceLocation,
    string? ManifestJson);

public sealed record InstallExtensionRequest(
    string? CatalogId,
    string? SourceKind,
    string? SourceLocation,
    string? ManifestJson,
    string? ApprovalId);

public sealed record SaveAgentProfileRequest(
    string Name,
    string Layer,
    string AgentType,
    string Mode,
    string Description,
    string ModelRoutePurpose,
    IReadOnlyList<string>? AllowedTools,
    IReadOnlyList<string>? Capabilities,
    string? SystemPrompt,
    bool Enabled);

public sealed record UpdateAgentModeRequest(
    string Mode);

public sealed record SavePromptFragmentRequest(
    string Key,
    string Title,
    string Scope,
    string? TargetAgentId,
    string Category,
    string Content,
    int Priority,
    bool Enabled);

public sealed record PromptContextPreviewRequest(
    string AgentId,
    string? Mode,
    string? SessionId,
    string? RunId,
    string? UserContent);

public sealed record CodeToolExecuteRequest(
    string? SessionId,
    string? RunId,
    string? TaskNodeId,
    string? ApprovalId,
    string? Cwd,
    IReadOnlyDictionary<string, object?>? Arguments);
