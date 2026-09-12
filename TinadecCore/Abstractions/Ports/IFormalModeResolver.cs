namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Resolves formal agent/mode effective tools and model strategy along the topology.
/// Implemented in the composition root (Runtime) so business modules can depend only on Abstractions.
/// </summary>
public static class GraphResolverModes
{
    /// <summary>Observe-only: legacy roster wins, drift is reported. The default until the flip review.</summary>
    public const string Shadow = "shadow";

    /// <summary>Graph-derived roster semantics win (legacy values still carried for comparison).</summary>
    public const string Active = "active";
}

public interface IFormalModeResolver
{
    /// <summary>
    /// Returns the union of effective tools (agent ∩ mode) for the session's mode_version, or null if no formal mode.
    /// Contains "*" if wildcard, empty set if no effective tools (publish warned EMPTY_EFFECTIVE_TOOLS).
    /// </summary>
    Task<HashSet<string>?> GetEffectiveToolsForSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the runnable agent roster for the session's published relational mode version.
    /// Returns null when the session has no mode_version_id or the mode cannot be resolved, in which case
    /// the caller should fall back to the TOML baseline roster.
    /// <paramref name="graphResolverMode"/> selects the dual-write behavior for declared-graph modes:
    /// "shadow" (default) resolves the legacy roster and only reports drift; "active" applies the
    /// graph-derived overrides (conversation identity output/context, edge-derived lifecycles).
    /// The policy stack (model strategy, prompt hash, tool-scope overrides) is carried by the inner
    /// resolution in both modes and is never overridden here.
    /// </summary>
    Task<FormalModeRoster?> ResolveRosterAsync(Guid sessionId, string graphResolverMode = GraphResolverModes.Shadow, CancellationToken cancellationToken = default);
}

/// <summary>
/// A frozen roster derived from a published relational AgentConfiguration mode version.
/// <c>RuntimeProfileId</c> is deterministic per mode version so an idempotent admission retry
/// yields the same value (used for run-mode matching).
/// </summary>
public sealed record FormalModeRoster(
    IReadOnlyList<RuntimeAgentRosterEntry> Operation,
    IReadOnlyList<RuntimeAgentRosterEntry> Execution,
    Guid ModeVersionId,
    int VersionNumber,
    string? TopologyHash,
    string RuntimeProfileId)
{
    public Guid AgentModeId { get; init; }

    // DmaEA graph orchestration (additive): the declared communication topology and
    // conversation resolution from the frozen mode snapshot. HasDeclaredEdges is
    // keyed on edge COUNT, not on the presence of an `edges` key — an empty array
    // is free-form and the run freeze writes no Graph section for it.
    public IReadOnlyList<DeclaredGraphEdge> Edges { get; init; } = [];
    public bool HasDeclaredEdges { get; init; }
    public IReadOnlyList<DeclaredGraphNode> GraphNodes { get; init; } = [];
    public string? ConversationNodeKey { get; init; }
    public string? ConversationTemplateSlug { get; init; }
}

/// <summary>One declared communication edge of a mode graph (dispatch direction).</summary>
public sealed record DeclaredGraphEdge(string EdgeKey, string SourceNodeKey, string TargetNodeKey);

/// <summary>One declared node of a mode graph, projected with its resolved agent slug.</summary>
public sealed record DeclaredGraphNode(string NodeKey, string AgentSlug, string Layer);

/// <summary>
/// One runnable agent entry resolved from a relational <c>AgentDefinitionRecord</c> referenced by a mode node.
/// <c>Id</c> is the agent slug so the runtime can look it up by the string it already uses.
/// </summary>
public sealed record RuntimeAgentRosterEntry(
    string Id,
    string Layer,
    string Role,
    string Lifecycle,
    IReadOnlyList<string> Capabilities,
    bool DirectUserOutput,
    string ContextAccess,
    IReadOnlyList<string> AllowedTools,
    string PromptProfile,
    Guid? AgentDefinitionId = null,
    Guid? AgentVersionId = null,
    string VersionContentHash = "")
{
    public string SystemPrompt { get; init; } = string.Empty;
    public string ModelStrategyJson { get; init; } = "{\"kind\":\"inherit\"}";
    public string ModelStrategySource { get; init; } = "agent_version";
    public bool Enabled { get; init; } = true;
    public int RosterOrder { get; init; }
    public Guid? PromptPipelineId { get; init; }
    public Guid? PromptVersionId { get; init; }
    public string PromptVersionContentHash { get; init; } = string.Empty;
    public string PromptGraphJson { get; init; } = string.Empty;
}
