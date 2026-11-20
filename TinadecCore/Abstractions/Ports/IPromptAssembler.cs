using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Deterministically assembles prompt fragments, agent instructions, skill contributions,
/// and ContextPack into MAF ChatOptions.Instructions / AIContext.
/// Full prompt content stays local to preview UI and in-memory calls.
/// </summary>
public interface IPromptAssembler
{
    Task<PromptAssemblyResult> AssembleAsync(
        string agentId,
        ContextPack? contextPack,
        CancellationToken cancellationToken = default);

    Task<PromptAssemblyResult> AssembleAsync(
        FrozenPromptAssemblyRequest request,
        CancellationToken cancellationToken = default) =>
        AssembleAsync(request.AgentId, request.ContextPack, cancellationToken);
}

/// <summary>
/// Immutable prompt inputs captured in a run configuration. Durable execution uses
/// this request so a resumed run never reloads a mutable agent or prompt pipeline.
/// </summary>
public sealed record FrozenPromptAssemblyRequest(
    string AgentId,
    ContextPack? ContextPack,
    string? SystemPrompt = null,
    Guid? AgentVersionId = null,
    string? AgentVersionContentHash = null,
    Guid? PromptPipelineId = null,
    Guid? PromptVersionId = null,
    string? PromptVersionContentHash = null,
    string? PromptGraphJson = null,
    bool IncludeLiveFragments = false,
    string? ResponseLocale = null)
{
    /// <summary>
    /// The run's frozen mode policy. B5 reads it only to decide whether the tool-attribution
    /// constraint (blocked calls are not retried) belongs in this prompt.
    /// </summary>
    public ModeRuntimePolicyDto? RuntimePolicy { get; init; }

    /// <summary>
    /// The run's frozen workspace, when one is bound. It is prompt input, not a
    /// live lookup: the assembler renders the frozen root, git facts, listing and
    /// path contract, and never asks the store for a current root.
    /// </summary>
    public FrozenWorkspaceBinding? Workspace { get; init; }

    public Guid? AgentDefinitionId { get; init; }
    public string? AgentName { get; init; }
    public string? AgentSlug { get; init; }
    public string? AgentLayer { get; init; }
    public string? ModeName { get; init; }
    public string? ModeSlug { get; init; }
    public string? ModeInstructions { get; init; }
    public IReadOnlyList<string>? Capabilities { get; init; }
    public bool? HasTools { get; init; }
    public IReadOnlyList<FrozenPromptFragment>? FrozenFragments { get; init; }
    public IReadOnlyList<Guid>? IncludeFragmentIds { get; init; }
    public IReadOnlyList<string>? WorkspaceRules { get; init; }
    public string? SkillsIndexText { get; init; }
}

public sealed record FrozenPromptFragment(
    Guid Id,
    string Key,
    string Title,
    string Scope,
    Guid? TargetAgentId,
    string? TargetAgentSlug,
    string Category,
    int Priority,
    string Content,
    int Version,
    string ContentHash);

public sealed record PromptAssemblyLayer(
    string Id,
    string Kind,
    string Title,
    string Content,
    int EstimatedTokens,
    string Source);

public sealed class PromptAssemblyResult
{
    public string Instructions { get; init; } = string.Empty;
    public int EstimatedTokens { get; init; }
    public IReadOnlyList<string> FragmentIds { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<PromptAssemblyLayer> Layers { get; init; } = [];
}

public interface IFrozenPromptFragmentSource
{
    Task<IReadOnlyList<FrozenPromptFragment>> SnapshotActiveFragmentsAsync(
        Guid tenantId,
        Guid? workspaceId,
        CancellationToken cancellationToken = default);
}

public sealed record AgentIdentitySnapshot(
    Guid Id,
    string Slug,
    string DisplayName,
    string Layer,
    IReadOnlyList<string> Capabilities,
    bool HasTools);

public interface IAgentIdentityLookup
{
    Task<AgentIdentitySnapshot?> ResolveAsync(
        string agentIdOrSlug,
        CancellationToken cancellationToken = default);
}

public sealed class PromptFragmentException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public interface IPromptFragmentService
{
    Task<IReadOnlyList<PromptFragmentDto>> ListAsync(
        string? scope = null,
        Guid? targetAgentId = null,
        string? targetAgentSlug = null,
        string? category = null,
        bool? enabled = null,
        CancellationToken cancellationToken = default);

    Task<PromptFragmentDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PromptFragmentDto> SaveAsync(
        Guid? id,
        PromptFragmentSaveRequestDto input,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PromptFragmentVersionDto>> ListVersionsAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<PromptFragmentVersionDto> CreateVersionAsync(
        Guid id,
        PromptFragmentVersionCreateRequestDto input,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default);

    Task<PromptFragmentDto> RollbackAsync(
        Guid id,
        PromptFragmentRollbackRequestDto input,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default);

    Task<PromptFragmentDto> CloneAsync(
        Guid id,
        PromptFragmentCloneRequestDto? input = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
