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
}

public sealed class PromptAssemblyResult
{
    public string Instructions { get; init; } = string.Empty;
    public int EstimatedTokens { get; init; }
    public IReadOnlyList<string> FragmentIds { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
