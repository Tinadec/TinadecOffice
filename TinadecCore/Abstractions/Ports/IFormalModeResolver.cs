using System.Text.Json;
using System.Text.Json.Serialization;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

public interface IFormalModeResolver
{
    /// <summary>A published, directly executable baseline for a spatial composition.</summary>
    Task<FormalModeRoster?> ResolveSpaceBaseAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<FormalModeRoster?>(null);

    /// <summary>Same baseline resolved with a frozen interface locale for prompt overlays.</summary>
    Task<FormalModeRoster?> ResolveSpaceBaseAsync(Guid sessionId, string? interfaceLocale, CancellationToken cancellationToken = default) =>
        ResolveSpaceBaseAsync(sessionId, cancellationToken);

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

    /// <summary>Resolves the candidate roster with a frozen interface locale for prompt overlays.</summary>
    Task<FormalModeRoster?> ResolveRosterForModeAsync(
        Guid sessionId,
        Guid modeVersionId,
        string? interfaceLocale,
        CancellationToken cancellationToken = default) =>
        ResolveRosterForModeAsync(sessionId, modeVersionId, cancellationToken);
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
    public string? ModeSlug { get; init; }
    public string? ModeDisplayName { get; init; }

    /// <summary>
    /// Optional explicit orchestration tier (<c>solo_dispatch | free_form | self_dispatch | deterministic</c>)
    /// declared on the published mode. Null means derive from graph topology and conversation tools.
    ///
    /// The resolver populates this from the frozen snapshot's <c>orchestration_tier</c> key via
    /// <see cref="FormalModeSnapshotDocuments.ReadOrchestrationTier"/>. It is the value the run
    /// freeze prefers over inference — <c>AgentRuntimeConfigurationResolver.DeriveGraphTier</c>
    /// takes it as its fourth argument and maps it onto <c>FrozenGraphTiers</c>, falling back to
    /// the four-branch derivation only when it is null/empty.
    /// </summary>
    public string? OrchestrationTier { get; init; }

    /// <summary>True when the published mode declared its tier explicitly (no inference needed).</summary>
    [JsonIgnore]
    public bool HasExplicitOrchestrationTier => !string.IsNullOrWhiteSpace(OrchestrationTier);

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

    /// <summary>
    /// Team-level collaboration instructions frozen in the mode version snapshot.
    /// The resolver appends them to the conversation identity's system prompt; they
    /// are audit-visible here so tests and projections can assert what ran.
    /// </summary>
    public string? ModeInstructions { get; init; }

    /// <summary>
    /// Host-owned workflow selected by the published mode. Null means ordinary
    /// mode behavior; old mode versions remain byte-compatible.
    /// </summary>
    public ModeRuntimePolicyDto? RuntimePolicy { get; init; }
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

    /// <summary>
    /// The agent's published responsibility description ("what it is for / when to use it /
    /// what it cannot do"). It is what the coordinator reads to choose a dispatch target, so it
    /// travels into the frozen roster verbatim. Null-suppressed so bodies frozen before it
    /// existed serialize to identical bytes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>
    /// The published role instructions frozen with this template. Spawned workers do not have a
    /// relational roster entry of their own at admission, so this must travel with the template;
    /// resolving it later would let a mutable agent version change the worker's duty mid-run.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemPrompt { get; init; }

    /// <summary>Targets this execution role may itself dispatch to; null keeps the tier default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AllowedDispatchTargets { get; init; }
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
    /// <summary>
    /// The agent's published responsibility description ("what it is for / when to use it /
    /// what it cannot do"). It is what the coordinator reads to choose a dispatch target, so it
    /// travels into the frozen roster verbatim. Null-suppressed so bodies frozen before it
    /// existed serialize to identical bytes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>
    /// The facts this role wakes on, from its relationship file's <c>subscriptions</c>
    /// (<see cref="GovernanceTopics"/>). Null when the role declares none, which keeps the derived
    /// role-name wiring and serializes a pre-subscription body to identical bytes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Triggers { get; init; }

    /// <summary>
    /// The executor ids this node may dispatch to, from its relationship file's
    /// <c>allowed_dispatch_targets</c>. Null when the file does not declare it (the tier's roster
    /// applies, as before); an empty list means the node may dispatch to nobody.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AllowedDispatchTargets { get; init; }

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

/// <summary>
/// Reads the mode-level documents a frozen <c>tinadec.mode_version/v1</c> snapshot carries but
/// that are not part of the node/edge projection: the explicit orchestration tier and the team
/// collaboration instructions.
///
/// Lives here, next to the record that consumes them, so the resolver that has the snapshot in
/// hand and the freeze that needs the value cannot each invent their own key spelling. Both
/// readers are tolerant by design: a snapshot published before a field existed, and a snapshot
/// with a malformed value, both read as null — an old mode keeps its inferred tier instead of
/// failing admission over a field it never declared.
///
/// Usage in <c>TinadecCore/Runtime/FormalModeResolver.cs</c> where the roster is built:
/// <code>
/// OrchestrationTier = FormalModeSnapshotDocuments.ReadOrchestrationTier(mv.SnapshotJson),
/// ModeInstructions  = FormalModeSnapshotDocuments.ReadInstructions(mv.SnapshotJson),
/// </code>
/// </summary>
public static class FormalModeSnapshotDocuments
{
    public const string OrchestrationTierProperty = "orchestration_tier";
    public const string InstructionsProperty = "instructions";

    /// <summary>The snapshot's declared tier, folded onto the canonical tier names, or null.</summary>
    public static string? ReadOrchestrationTier(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(snapshotJson!);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(OrchestrationTierProperty, out var tier)
                && tier.ValueKind == JsonValueKind.String
                    ? NormalizeTier(tier.GetString())
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The snapshot's team collaboration instructions, or null when it declares none.</summary>
    public static string? ReadInstructions(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(snapshotJson!);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(InstructionsProperty, out var instructions)
                || instructions.ValueKind != JsonValueKind.String)
                return null;
            var text = instructions.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The canonical tier names, with the <c>solo</c> alias folded onto <c>solo_dispatch</c>.
    /// Mirrors <c>GraphValidation.NormalizeOrchestrationTier</c> (the publish-side authority);
    /// an unrecognized value reads as null so the caller falls back to inference.
    /// </summary>
    public static string? NormalizeTier(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier)) return null;
        return tier.Trim().ToLowerInvariant() switch
        {
            "solo" or "solo_dispatch" => "solo_dispatch",
            "free_form" => "free_form",
            "self_dispatch" => "self_dispatch",
            "deterministic" => "deterministic",
            _ => null
        };
    }
}
