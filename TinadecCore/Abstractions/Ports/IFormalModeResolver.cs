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
}
