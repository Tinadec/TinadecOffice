using Tinadec.Contracts.Events;
using Tinadec.Contracts.Models;
using Tinadec.Contracts.Security;

namespace TinadecCore.Abstractions;

public interface ISessionService
{
    IReadOnlyList<SessionDto> ListSessions(string? projectId);
    SessionDto CreateSession(string projectId, string? title);
    IReadOnlyList<MessageDto> ListMessages(string sessionId);
    MessageDto AddMessage(string sessionId, string role, string content);
}

public interface IApprovalService
{
    IReadOnlyList<ApprovalDto> ListApprovals(string? status, string? sessionId);
    ApprovalDto CreateApproval(CreateApprovalRequest request);
    ApprovalDto? DecideApproval(string approvalId, string decision);
}

public interface IEventLog
{
    IReadOnlyList<EventEnvelope> ListEvents(string? sessionId);
    EventEnvelope AppendNewEvent(string type, string? sessionId, IReadOnlyDictionary<string, object?>? payload, IReadOnlyList<string> capabilities);
}

public interface IExtensionCatalogService
{
    IReadOnlyList<ExtensionSourceDto> ListSources();
    IReadOnlyList<MarketCatalogItemDto> ListCatalog(string? kind, string? query, string? sourceId);
    MarketCatalogItemDto? GetCatalogItem(string catalogId);
}

public interface IExtensionInstallService
{
    ExtensionInstallPreviewDto PreviewInstall(InstallExtensionPreviewRequest request);
    ExtensionInstallResultDto InstallExtension(InstallExtensionRequest request);
    IReadOnlyList<InstalledExtensionDto> ListInstalledExtensions();
}

public interface IExtensionRuntimeRegistry
{
    IReadOnlyList<InstalledExtensionDto> ListEnabledExtensions();
}

public interface ISkillRegistry
{
    IReadOnlyList<InstalledExtensionDto> ListEnabledSkills();
}

public interface IMcpRegistry
{
    IReadOnlyList<McpServerDto> ListServers();
}

public interface IAcpRegistry
{
    IReadOnlyList<AcpAdapterDto> ListAdapters();
}

public interface IAgentProfileRegistry
{
    IReadOnlyList<AgentProfileDto> ListAgentProfiles();
    IReadOnlyList<AgentModeDto> ListAgentModes();
    IReadOnlyList<AgentCandidateDto> ListAgentCandidates();
}

public interface IOrchestrationRuntime
{
    OrchestrationSnapshotDto GetOrchestrationSnapshot(string sessionId);
    IReadOnlyList<OrchestrationRunDto> ListRuns(string sessionId);
    IReadOnlyList<TaskNodeDto> ListTaskNodes(string sessionId);
    IReadOnlyList<ContextPackDto> ListContextPacks(string sessionId);
    IReadOnlyList<SupervisionFindingDto> ListSupervisionFindings(string sessionId);
}

public interface IAgentWorkflowRuntime
{
    AgentWorkflowPlanDto Compile(OrchestrationSnapshotDto snapshot);
}

public interface ICapabilityProvider
{
    string Id { get; }
    IReadOnlyList<ToolDescriptorDto> ListCapabilities();
}

public interface IRuntimeKernelAdapter
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlyList<string> Capabilities { get; }
}

public interface IToolInvocationAdapter
{
    string Id { get; }
    bool CanInvoke(ToolDescriptorDto tool);
    Task<CodeToolExecuteResultDto> InvokeAsync(
        ToolDescriptorDto tool,
        CodeToolExecuteRequest request,
        CancellationToken cancellationToken = default);
}

public interface ICapabilityPolicy
{
    ApprovalRequirement Evaluate(string permissionMode, ToolDescriptorDto tool);
    bool IsReadOnly(ToolDescriptorDto tool);
}

public interface IToolRegistry
{
    IReadOnlyList<ToolDescriptorDto> ListTools(string? domain = null);
    ToolDescriptorDto? Resolve(string toolId);
    ToolRegistrySummaryDto Describe(string? domain = null);
    IReadOnlyList<ModelToolSpecDto> BuildOpenAiToolSpecs(string? domain = null);
}

public interface ICodeToolClient
{
    Task<CodeToolExecuteResultDto> ExecuteAsync(
        ToolDescriptorDto tool,
        CodeToolExecuteRequest request,
        CancellationToken cancellationToken = default);
}

public interface IToolPermissionPolicy
{
    bool RequiresApproval(string capability);
}

public interface IPromptContextPlannerRuntime
{
    Task<PromptContextPlanDto?> TryCreatePlanAsync(
        PromptContextPlanningInput input,
        CancellationToken cancellationToken = default);
}

public interface ICoreStore : ISessionService, IApprovalService
{
    IReadOnlyList<ProjectDto> ListProjects();
    ProjectDto CreateProject(string name, string path);
}
