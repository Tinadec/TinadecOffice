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
    bool IncludeLiveFragments = false);

public sealed class PromptAssemblyResult
{
    public string Instructions { get; init; } = string.Empty;
    public int EstimatedTokens { get; init; }
    public IReadOnlyList<string> FragmentIds { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
