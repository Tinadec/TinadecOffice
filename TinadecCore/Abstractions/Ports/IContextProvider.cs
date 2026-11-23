using Microsoft.Extensions.AI;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Produces a ContextPack with evidence, sources, and token budget.
/// Extends the MAF AIContextProvider concept; does not assemble the final system prompt.
/// </summary>
public interface IContextProvider
{
    /// <summary>Builds a context pack from an explicit frozen runtime request.</summary>
    Task<ContextPack> BuildContextAsync(
        ContextBuildRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Produces a context pack for the given session and run.</summary>
    Task<ContextPack> BuildContextAsync(
        string sessionId,
        string? runId,
        CancellationToken cancellationToken = default) =>
        BuildContextAsync(new ContextBuildRequest(sessionId, runId), cancellationToken);
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

    /// <summary>
    /// Candidates the token budget removed. Kept as evidence rather than as names because "the pack
    /// has 4 items" and "nothing was missing" are the same visible fact, and the second one is a
    /// lie the surface can only avoid by reading this list. The content stays off the wire; only
    /// name and price are projected, so keeping it costs a dropped item's text for the lifetime of
    /// a pack that is about to be discarded anyway.
    /// </summary>
    public IReadOnlyList<ContextEvidence> Dropped { get; init; } = [];

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public sealed class ContextEvidence
{
    public string Source { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public int EstimatedTokens { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public sealed record ContextBuildRequest(
    string SessionId,
    string? RunId,
    string RuntimeProfileId = "",
    string AgentId = "meeting",
    string? TaskContext = null,
    int? TokenBudget = null,
    int? RecentMessageLimit = null,
    int? ReviewedMemoryLimit = null,
    TinaChatInputBinding? TinaChatInput = null)
{
    /// <summary>
    /// The run's frozen workspace, when one is bound. Carried in rather than looked up, exactly like
    /// <see cref="FrozenPromptAssemblyRequest.Workspace"/>: admission already proved this root belongs
    /// to this session's tenant and workspace, and a context builder that re-resolved a root from the
    /// store could read a directory the run was never granted.
    /// </summary>
    public FrozenWorkspaceBinding? Workspace { get; init; }
    public ToolExecutionContextDto? ToolExecutionContext { get; init; }

    /// <summary>
    /// The frozen tool surface for this agent. Context uses it only to describe how a selected
    /// skill may be opened; it never exposes the skill body or expands this set of grants.
    /// </summary>
    public IReadOnlyList<string>? AllowedToolIds { get; init; }

    /// <summary>Optional mode- or session-level context strategy override (<c>direct</c>, <c>managed</c>, <c>deep_curator</c>).</summary>
    public string? ContextStrategyOverride { get; init; }

    /// <summary>Optional mode- or session-level compaction trigger ratio override (<c>0.40..0.95</c>).</summary>
    public double? CompactionTriggerRatioOverride { get; init; }

    /// <summary>Optional mode- or session-level recall fidelity override (<c>summary_only</c>, <c>balanced</c>, <c>verbatim_first</c>).</summary>
    public string? RecallPolicyOverride { get; init; }

    /// <summary>Optional mode- or session-level cross-session recall override.</summary>
    public bool? AllowCrossSessionRecallOverride { get; init; }

    /// <summary>Optional mode- or session-level memory evolution mode override (<c>auto_evolve</c>, <c>candidate_only</c>).</summary>
    public string? EvolutionModeOverride { get; init; }

    /// <summary>Optional mode- or session-level core markdown max tokens override (<c>256..8000</c>).</summary>
    public int? CoreMarkdownMaxTokensOverride { get; init; }
}
