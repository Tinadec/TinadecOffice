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
    public static bool IsDispatchAllowed(FrozenGraph? graph, string? workerSlug, string? dispatcherSlug = null)
    {
        if (graph is null) return true;
        if (string.IsNullOrWhiteSpace(workerSlug)) return false;
        var trimmedWorker = workerSlug.Trim();

        var nodeBySlug = new Dictionary<string, FrozenGraphNode>(StringComparer.OrdinalIgnoreCase);
        var nodeByKey = new Dictionary<string, FrozenGraphNode>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            nodeBySlug.TryAdd(node.AgentSlug, node);
            nodeByKey.TryAdd(node.NodeKey, node);
        }
        nodeBySlug.TryGetValue(trimmedWorker, out var workerNode);

        // When a concrete dispatcher is named (e.g. an execution worker invoking task_dispatch
        // for 2-layer nested delegation), evaluate ONLY that dispatcher's outgoing declared
        // edges and its node-declared allowed_dispatch_targets / agent_types, strictly
        // forbidding execution -> operation backflow.
        if (!string.IsNullOrWhiteSpace(dispatcherSlug))
        {
            var trimmedDispatcher = dispatcherSlug.Trim();
            nodeBySlug.TryGetValue(trimmedDispatcher, out var dispatcherNode);
            var dispatcherIsExecution = dispatcherNode is null
                || string.Equals(dispatcherNode.Layer, "execution", StringComparison.OrdinalIgnoreCase);
            if (dispatcherIsExecution && workerNode is not null
                && string.Equals(workerNode.Layer, "operation", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Conversation root delegates to the root reachability & tier authority,
            // subject to its own allowed_dispatch_targets narrowing if declared.
            if (dispatcherNode is not null
                && (dispatcherNode.IsConversation
                    || string.Equals(dispatcherNode.NodeKey, graph.ConversationNodeKey, StringComparison.Ordinal)
                    || string.Equals(dispatcherNode.AgentSlug, graph.ConversationTemplateSlug, StringComparison.OrdinalIgnoreCase)))
            {
                if (dispatcherNode.AllowedDispatchTargets is { } rootTargets
                    && !rootTargets.Contains(trimmedWorker, StringComparer.OrdinalIgnoreCase))
                    return false;
                return IsDispatchAllowed(graph, trimmedWorker, dispatcherSlug: null);
            }

            if (dispatcherNode is not null)
            {
                if (dispatcherNode.AllowedDispatchTargets is { } narrowTargets
                    && !narrowTargets.Contains(trimmedWorker, StringComparer.OrdinalIgnoreCase))
                {
                    return false;
                }
                if (workerNode is not null
                    && !string.Equals(workerNode.Layer, "operation", StringComparison.OrdinalIgnoreCase)
                    && graph.Edges.Any(edge =>
                        string.Equals(edge.SourceNodeKey, dispatcherNode.NodeKey, StringComparison.Ordinal)
                        && string.Equals(edge.TargetNodeKey, workerNode.NodeKey, StringComparison.Ordinal)))
                {
                    return true;
                }
                if (dispatcherNode.AllowedDispatchTargets is { } explicitTargets
                    && explicitTargets.Contains(trimmedWorker, StringComparer.OrdinalIgnoreCase))
                {
                    return IsKnownWorkerOrAuthorizedTemplate(graph, workerNode, trimmedWorker);
                }
                if (dispatcherNode.AgentTypes is { } agentTypes
                    && agentTypes.Contains(trimmedWorker, StringComparer.OrdinalIgnoreCase))
                {
                    return IsKnownWorkerOrAuthorizedTemplate(graph, workerNode, trimmedWorker);
                }
                return false;
            }

            var dispatcherTemplate = graph.SpawnableTemplates.FirstOrDefault(template =>
                string.Equals(template.Slug, trimmedDispatcher, StringComparison.OrdinalIgnoreCase));
            if (dispatcherTemplate?.AllowedDispatchTargets is { } templateTargets
                && templateTargets.Contains(trimmedWorker, StringComparer.OrdinalIgnoreCase))
            {
                return IsKnownWorkerOrAuthorizedTemplate(graph, workerNode, trimmedWorker);
            }
            return false;
        }

        if (string.Equals(graph.Tier, FrozenGraphTiers.FreeForm, StringComparison.Ordinal))
        {
            if (workerNode is not null && string.Equals(workerNode.Layer, "operation", StringComparison.OrdinalIgnoreCase) && !workerNode.IsConversation)
                return false;
            return true;
        }

        if (workerNode is null)
            return TierAllowsSpawnedWorkers(graph.Tier)
                && graph.SpawnableTemplates.Any(template =>
                    string.Equals(template.Slug, trimmedWorker, StringComparison.OrdinalIgnoreCase));

        // Execution tasks never dispatch back into operation-layer nodes.
        if (string.Equals(workerNode.Layer, "operation", StringComparison.OrdinalIgnoreCase))
            return false;

        // Directed BFS reachability from conversation / operation seeds across forward
        // edges (supporting multi-stage pipelines leader -> m1 -> m2 while forbidding
        // execution -> operation backflow traversal).
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var node in graph.Nodes)
        {
            if (string.Equals(node.NodeKey, graph.ConversationNodeKey, StringComparison.Ordinal)
                || string.Equals(node.Layer, "operation", StringComparison.OrdinalIgnoreCase))
            {
                if (visited.Add(node.NodeKey))
                    queue.Enqueue(node.NodeKey);
            }
        }
        if (!string.IsNullOrWhiteSpace(graph.ConversationNodeKey) && visited.Add(graph.ConversationNodeKey))
            queue.Enqueue(graph.ConversationNodeKey);

        while (queue.Count > 0)
        {
            var currentKey = queue.Dequeue();
            nodeByKey.TryGetValue(currentKey, out var sourceNode);
            var sourceIsExecution = sourceNode is not null
                && string.Equals(sourceNode.Layer, "execution", StringComparison.OrdinalIgnoreCase);

            foreach (var edge in graph.Edges)
            {
                if (!string.Equals(edge.SourceNodeKey, currentKey, StringComparison.Ordinal))
                    continue;
                if (!nodeByKey.TryGetValue(edge.TargetNodeKey, out var targetNode))
                    continue;
                if (sourceIsExecution && string.Equals(targetNode.Layer, "operation", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(edge.TargetNodeKey, workerNode.NodeKey, StringComparison.Ordinal))
                    return true;
                if (visited.Add(edge.TargetNodeKey))
                    queue.Enqueue(edge.TargetNodeKey);
            }
        }
        return false;
    }

    private static bool IsKnownWorkerOrAuthorizedTemplate(FrozenGraph graph, FrozenGraphNode? workerNode, string workerSlug)
    {
        if (workerNode is not null)
            return !string.Equals(workerNode.Layer, "operation", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(graph.Tier, FrozenGraphTiers.FreeForm, StringComparison.Ordinal))
            return true;
        return TierAllowsSpawnedWorkers(graph.Tier)
            && graph.SpawnableTemplates.Any(template =>
                string.Equals(template.Slug, workerSlug, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TierAllowsSpawnedWorkers(string tier) =>
        string.Equals(tier, FrozenGraphTiers.SelfDispatch, StringComparison.Ordinal)
        || string.Equals(tier, FrozenGraphTiers.SoloDispatch, StringComparison.Ordinal)
        || string.Equals(tier, FrozenGraphTiers.FreeForm, StringComparison.Ordinal);
}
