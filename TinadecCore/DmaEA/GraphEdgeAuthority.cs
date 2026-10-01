namespace TinadecCore.DmaEA;

/// <summary>
/// Pure dispatch authority for declared-graph tiers:
/// deterministic — a task may only be assigned to a worker whose AGENT SLUG is
///   the slug of a node that is the target of a declared edge sourced from the
///   conversation node or any other operation-layer node;
/// self_dispatch — declared edge targets plus spawned workers whose slug is a
///   member of the frozen spawnable-template whitelist;
/// solo_dispatch — same dispatch reach as self_dispatch, but the conversation
///   identity also holds a tool surface of its own: it does the work AND hands
///   work off. Declared edges are honoured when the mode has them; without edges
///   the spawnable whitelist carries the dispatch;
/// free_form — no declared constraints: the single director may assign any
///   worker it spawned (edges are prompt material only).
/// Edges are declared over NODE KEYS while dispatch selects agent slugs, so the
/// authority translates target node keys through the frozen node set.
/// Deliberately a static pure function so the negative cases are unit-tested
/// directly and the dispatch site stays a single call.
/// </summary>
public static class GraphEdgeAuthority
{
    public static bool IsDispatchAllowed(FrozenGraph? graph, string? workerSlug)
    {
        if (graph is null) return true;
        if (string.IsNullOrWhiteSpace(workerSlug)) return false;
        if (string.Equals(graph.Tier, FrozenGraphTiers.FreeForm, StringComparison.Ordinal)) return true;
        var nodeBySlug = graph.Nodes.ToDictionary(node => node.AgentSlug, StringComparer.Ordinal);
        if (!nodeBySlug.TryGetValue(workerSlug, out var workerNode))
        {
            // A slug that is not a declared node can only be a spawned template, and only
            // the tiers that carry spawn authority may dispatch to one. solo_dispatch is
            // grouped with self_dispatch: it walks declared edges when it has them and
            // falls back to the spawnable whitelist when it does not, so one tier covers
            // both "the master dispatches along the graph" and "the master dispatches
            // freely".
            var tierAllowsSpawnedWorkers = string.Equals(graph.Tier, FrozenGraphTiers.SelfDispatch, StringComparison.Ordinal)
                || string.Equals(graph.Tier, FrozenGraphTiers.SoloDispatch, StringComparison.Ordinal);
            return tierAllowsSpawnedWorkers
                && graph.SpawnableTemplates.Any(template =>
                    string.Equals(template.Slug, workerSlug, StringComparison.OrdinalIgnoreCase));
        }
        var workerNodeKey = workerNode.NodeKey;

        foreach (var edge in graph.Edges)
        {
            if (!string.Equals(edge.TargetNodeKey, workerNodeKey, StringComparison.Ordinal))
            {
                continue;
            }
            var sourceIsConversation = string.Equals(edge.SourceNodeKey, graph.ConversationNodeKey, StringComparison.Ordinal);
            if (sourceIsConversation)
            {
                return true;
            }
            var sourceNode = graph.Nodes.FirstOrDefault(node =>
                string.Equals(node.NodeKey, edge.SourceNodeKey, StringComparison.Ordinal));
            if (sourceNode is not null && string.Equals(sourceNode.Layer, "operation", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
