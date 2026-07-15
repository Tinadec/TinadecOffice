using Microsoft.Extensions.AI;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Produces a ContextPack with evidence, sources, and token budget.
/// Extends the MAF AIContextProvider concept; does not assemble the final system prompt.
/// </summary>
public interface IContextProvider
{
    /// <summary>Produces a context pack for the given session and run.</summary>
    Task<ContextPack> BuildContextAsync(
        string sessionId,
        string? runId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Token-budgeted context pack with provenance evidence.
/// </summary>
public sealed class ContextPack
{
    public string SessionId { get; init; } = string.Empty;
    public string? RunId { get; init; }
    public int TokenBudget { get; init; }
    public int EstimatedTokens { get; init; }
    public IReadOnlyList<ContextEvidence> Evidence { get; init; } = [];
}

public sealed class ContextEvidence
{
    public string Source { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public int EstimatedTokens { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
