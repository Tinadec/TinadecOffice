using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Runtime;

/// <summary>
/// Dual-write decorator over <see cref="FormalModeResolver"/> (lane 双写过渡，不取代).
/// The inner resolver owns everything on the must-keep list — model strategy stack
/// (user_binding &gt; mode_node_override &gt; agent_version), prompt hash verification,
/// tool-scope overrides — and this decorator never touches it. For modes WITH
/// declared edges it derives the four roster semantics that were hardcoded to the
/// meeting slug (DirectUserOutput, ContextAccess, Lifecycle, lane counting) from the
/// declared graph and the conversation resolution instead.
///
/// Shadow mode (default) resolves the legacy roster and only reports drift; active
/// mode applies the derived overrides. The flip is a review decision gated on the
/// named drift corpus: seeded office snapshot + vibe fixture snapshot +
/// synthetic-divergence snapshot, all zero-drift across a full suite run.
/// </summary>
internal sealed class GraphModeResolver : IFormalModeResolver
{
    private readonly IFormalModeResolver _inner;
    private readonly ILogger<GraphModeResolver> _logger;

    public GraphModeResolver(IFormalModeResolver inner, ILogger<GraphModeResolver> logger)
    {
        _inner = inner;
        _logger = logger;
    }

    public Task<HashSet<string>?> GetEffectiveToolsForSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        _inner.GetEffectiveToolsForSessionAsync(sessionId, cancellationToken);

    public async Task<FormalModeRoster?> ResolveRosterAsync(
        Guid sessionId,
        string graphResolverMode = GraphResolverModes.Shadow,
        CancellationToken cancellationToken = default)
    {
        var legacy = await _inner.ResolveRosterAsync(sessionId, graphResolverMode, cancellationToken).ConfigureAwait(false);
        if (legacy is null || !legacy.HasDeclaredEdges) return legacy;

        var derived = DeriveOverrides(legacy);
        var drift = Compare(legacy, derived);
        if (!drift.IsEmpty)
        {
            _logger.LogWarning(
                "GraphModeResolver drift on session {SessionId} (mode {ModeVersionId}): {Differences}",
                sessionId, legacy.ModeVersionId, string.Join("; ", drift.Differences));
        }

        return graphResolverMode == GraphResolverModes.Active ? ApplyOverrides(legacy, derived) : legacy;
    }

    /// <summary>Derived roster semantics per agent slug, from the declared graph.</summary>
    internal static IReadOnlyDictionary<string, GraphDerivedEntry> DeriveOverrides(FormalModeRoster roster)
    {
        var nodeBySlug = roster.GraphNodes.ToDictionary(node => node.AgentSlug, StringComparer.Ordinal);
        var operationLayers = roster.GraphNodes
            .Where(node => string.Equals(node.Layer, "operation", StringComparison.Ordinal))
            .Select(node => node.NodeKey)
            .ToHashSet(StringComparer.Ordinal);
        var edgeTargets = roster.Edges
            .Select(edge => edge.TargetNodeKey)
            .ToHashSet(StringComparer.Ordinal);

        var derived = new Dictionary<string, GraphDerivedEntry>(StringComparer.Ordinal);
        foreach (var entry in roster.Operation.Concat(roster.Execution))
        {
            var nodeKey = nodeBySlug.TryGetValue(entry.Id, out var node) ? node.NodeKey : null;
            var isConversation = nodeKey is not null
                && string.Equals(nodeKey, roster.ConversationNodeKey, StringComparison.Ordinal);
            // Lifecycle ladder: conversation → session; operation (non-conversation) →
            // task; declared dispatch target → on_demand; anything else → persistent.
            // For the vibe graph this reproduces the legacy mapping exactly (meeting →
            // session, worker.* → on_demand); office modes without edges never reach
            // this code.
            var lifecycle = isConversation
                ? "session"
                : string.Equals(entry.Layer, "operation", StringComparison.Ordinal) && operationLayers.Contains(nodeKey)
                    ? "task"
                    : nodeKey is not null && edgeTargets.Contains(nodeKey)
                        ? "on_demand"
                        : "persistent";
            derived[entry.Id] = new GraphDerivedEntry(
                isConversation,
                isConversation ? "manage" : "read",
                lifecycle);
        }
        return derived;
    }

    /// <summary>Pure drift detector: per-entry differences between legacy and graph-derived semantics.</summary>
    internal static GraphDriftReport Compare(FormalModeRoster legacy, IReadOnlyDictionary<string, GraphDerivedEntry> derived)
    {
        var differences = new List<string>();
        foreach (var entry in legacy.Operation.Concat(legacy.Execution))
        {
            if (!derived.TryGetValue(entry.Id, out var expected)) continue;
            if (entry.DirectUserOutput != expected.DirectUserOutput)
                differences.Add($"{entry.Id}.DirectUserOutput {entry.DirectUserOutput} → {expected.DirectUserOutput}");
            if (!string.Equals(entry.ContextAccess, expected.ContextAccess, StringComparison.Ordinal))
                differences.Add($"{entry.Id}.ContextAccess {entry.ContextAccess} → {expected.ContextAccess}");
            if (!string.Equals(entry.Lifecycle, expected.Lifecycle, StringComparison.Ordinal))
                differences.Add($"{entry.Id}.Lifecycle {entry.Lifecycle} → {expected.Lifecycle}");
        }
        return new GraphDriftReport(differences);
    }

    internal static FormalModeRoster ApplyOverrides(FormalModeRoster legacy, IReadOnlyDictionary<string, GraphDerivedEntry> derived)
    {
        IReadOnlyList<RuntimeAgentRosterEntry> Apply(IEnumerable<RuntimeAgentRosterEntry> entries) => entries
            .Select(entry => derived.TryGetValue(entry.Id, out var expected)
                ? entry with { DirectUserOutput = expected.DirectUserOutput, ContextAccess = expected.ContextAccess, Lifecycle = expected.Lifecycle }
                : entry)
            .ToArray();

        return legacy with { Operation = Apply(legacy.Operation), Execution = Apply(legacy.Execution) };
    }
}

internal sealed record GraphDerivedEntry(bool DirectUserOutput, string ContextAccess, string Lifecycle);

internal sealed record GraphDriftReport(IReadOnlyList<string> Differences)
{
    public bool IsEmpty => Differences.Count == 0;
}
