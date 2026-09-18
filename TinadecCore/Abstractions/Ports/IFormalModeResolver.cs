using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecCore.Abstractions.Ports;

public interface IFormalModeResolver
{
    /// <summary>
    /// Returns the union of effective tools (agent ∩ mode) for the session's mode_version, or null if no formal mode.
    /// Contains "*" if wildcard, empty set if no effective tools (publish warned EMPTY_EFFECTIVE_TOOLS).
    /// </summary>
    Task<HashSet<string>?> GetEffectiveToolsForSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the effective tool union for one explicitly frozen mode version.
    /// Queued interactions use this path so a later session-mode change cannot alter
    /// the request that was already accepted.
    /// </summary>
    Task<HashSet<string>?> GetEffectiveToolsForModeAsync(
        Guid sessionId,
        Guid modeVersionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<HashSet<string>?>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves the runnable agent roster for the session's published relational mode version.
    /// Returns null when the session has no mode_version_id or the mode cannot be resolved, in which case
    /// the caller should fall back to the TOML baseline roster.
    /// Roster semantics (DirectUserOutput / ContextAccess / Lifecycle) are derived graph-natively from the
    /// declared graph and the resolved conversation node; the policy stack (model strategy, prompt hash,
    /// tool-scope overrides) is part of the same resolution and is never derived from graph shape.
    /// </summary>
    Task<FormalModeRoster?> ResolveRosterAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Resolves an explicitly selected published mode version for this session scope.</summary>
    Task<FormalModeRoster?> ResolveRosterForModeAsync(
        Guid sessionId,
        Guid modeVersionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<FormalModeRoster?>(null);
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

    // DmaEA graph orchestration: the declared communication topology, conversation
    // resolution and spawnable-template declarations resolved from the frozen mode
    // snapshot. HasDeclaredEdges is keyed on edge COUNT — an empty array means the
    // free_form tier (no dispatch edges; spawn authority decides what may be built).
    public IReadOnlyList<DeclaredGraphEdge> Edges { get; init; } = [];
    public bool HasDeclaredEdges { get; init; }
    public IReadOnlyList<DeclaredGraphNode> GraphNodes { get; init; } = [];
    public string? ConversationNodeKey { get; init; }
    public string? ConversationTemplateSlug { get; init; }

    /// <summary>
    /// Spawnable worker templates declared through the mode snapshot's relationship
    /// files (<c>agent_types</c> union across nodes), each resolved to the published
    /// AgentVersion and hash-pinned at resolution time. Unknown slugs fail closed.
    /// </summary>
    public IReadOnlyList<DeclaredSpawnableTemplate> SpawnableTemplates { get; init; } = [];
}

/// <summary>One declared communication edge of a mode graph (dispatch direction).</summary>
public sealed record DeclaredGraphEdge(string EdgeKey, string SourceNodeKey, string TargetNodeKey)
{
    /// <summary>
    /// The edge's declared data contract (the pack pipeline's edge <c>condition</c>
    /// object), frozen verbatim. Null when the edge declares no contract or only an
    /// empty object — prompt material for the traversal, never an enforcement input.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? DataContract { get; init; }
}

/// <summary>One declared node of a mode graph, projected with its resolved agent slug.</summary>
public sealed record DeclaredGraphNode(string NodeKey, string AgentSlug, string Layer);

/// <summary>
/// A spawnable worker template resolved from a relationship file's <c>agent_types</c>
/// entry. <c>VersionHash</c> pins the published AgentVersion snapshot; <c>ToolScope</c>
/// and <c>Capabilities</c> come from that pinned snapshot, so the freeze never re-reads
/// mutable rows.
/// </summary>
public sealed record DeclaredSpawnableTemplate(
    string Slug,
    Guid AgentDefinitionId,
    Guid AgentVersionId,
    string VersionHash,
    string Role,
    IReadOnlyList<string> ToolScope,
    IReadOnlyList<string> Capabilities)
{
    /// <summary>Resource-path grants from the template's binding envelope (empty = no workspace authorization).</summary>
    public IReadOnlyList<FrozenResourceGrant> ResourceGrants { get; init; } = [];
}

/// <summary>
/// A frozen resource grant: a workspace-relative path prefix (empty string = the
/// whole workspace) with a level of <c>read</c> or <c>write</c> (write implies
/// read). Workspace-level authorization is enforced by the PDP resource_access
/// boundary; prefix narrowing enforcement is a registered follow-up.
/// </summary>
public sealed record FrozenResourceGrant(string PathPrefix, string Level);

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

    /// <summary>Resource-path grants from the node's binding envelope (empty = no workspace authorization for provider tools).</summary>
    public IReadOnlyList<FrozenResourceGrant> ResourceGrants { get; init; } = [];
}
