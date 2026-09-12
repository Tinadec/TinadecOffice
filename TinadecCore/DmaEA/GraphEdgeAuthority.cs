namespace TinadecCore.DmaEA;

/// <summary>
/// Pure dispatch authority for declared-graph tiers (deterministic | self_dispatch):
/// a task may only be assigned to a worker whose AGENT SLUG is the slug of a node
/// that is the target of a declared edge sourced from the conversation node or any
/// other operation-layer node. Edges are declared over NODE KEYS while dispatch
/// selects agent slugs, so the authority translates target node keys through the
/// frozen node set. Deliberately a static pure function so the negative cases are
/// unit-tested directly and the dispatch site stays a single call. Free-form
/// (Graph == null) never consults this authority.
/// </summary>
public static class GraphEdgeAuthority
{
    public static bool IsDispatchAllowed(FrozenGraph? graph, string? workerSlug)
    {
        if (graph is null) return true;
        if (string.IsNullOrWhiteSpace(workerSlug)) return false;
        var nodeBySlug = graph.Nodes.ToDictionary(node => node.AgentSlug, StringComparer.Ordinal);
        if (!nodeBySlug.TryGetValue(workerSlug, out var workerNode)) return false;
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
