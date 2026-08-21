namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Resolves formal agent/mode effective tools and model strategy along the topology.
/// Implemented in the composition root (Runtime) so business modules can depend only on Abstractions.
/// </summary>
public interface IFormalModeResolver
{
    /// <summary>
    /// Returns the union of effective tools (agent ∩ mode) for the session's mode_version, or null if no formal mode.
    /// Contains "*" if wildcard, empty set if no effective tools (publish warned EMPTY_EFFECTIVE_TOOLS).
    /// </summary>
    Task<HashSet<string>?> GetEffectiveToolsForSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a formal ChatResolution for the given layer (operation/execution) along the topology.
    /// Handles inherit/fixed/parent_select (up to 2 retries) and audits to EventIndex/RunStream/control_event_index.
    /// Returns null for inherit or when no formal mode, meaning caller should fallback to default resolver.
    /// </summary>
    Task<ChatResolution?> TryResolveFormalChatAsync(Guid sessionId, string layer, Guid runId, Guid turnId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the runnable agent roster for the session's published relational mode version.
    /// Returns null when the session has no mode_version_id or the mode cannot be resolved, in which case
    /// the caller should fall back to the TOML baseline roster.
    /// </summary>
    Task<FormalModeRoster?> ResolveRosterAsync(Guid sessionId, CancellationToken cancellationToken = default);
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
    string RuntimeProfileId);

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
    string PromptProfile);
