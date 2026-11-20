namespace TinadecCore.DmaEA;

/// <summary>
/// Pure dispatch authority for declared-graph tiers. Two questions are answered here and
/// they are deliberately separate:
///
/// ① WHO MAY BE DISPATCHED TO — <see cref="IsDispatchAllowed"/>: a worker is reachable when
///    it is a node the conversation identity (or an operation-layer node) can get to by
///    walking FORWARD edges. That is transitive on purpose: a pipeline stage
///    <c>leader -&gt; m1 -&gt; m2</c> dispatches <c>m2</c> legitimately even though the only
///    edge into <c>m2</c> comes from another execution node, which the previous
///    "source must be the conversation node or an operation node" rule rejected — so every
///    pipeline mode threw WorkerAssignmentException at dispatch. Backflow
///    <c>execution -&gt; operation</c> stays prohibited: an executor may not hand work UP the
///    graph, and reachability is computed forward from the roots, so an operation node
///    downstream of an executor is never made dispatchable by it.
///    The spawnable-template whitelist of the spawn-carrying tiers
///    (<c>self_dispatch</c>/<c>solo_dispatch</c>) reaches slugs that are not declared nodes
///    at all; <c>free_form</c> reaches anything. Edges are declared over NODE KEYS while
///    dispatch selects agent slugs, so reachability translates keys through the frozen node
///    set.
///
/// ② HOW THE STAGES ARE ORDERED — <see cref="StageDependencies"/>: the
///    <c>execution -&gt; execution</c> edges as ordered stage pairs, in the form the planner
///    context surfaces. They are prompt material and dependency hints, never an
///    enforcement input.
///
/// Deliberately static pure functions so the negative cases are unit-tested directly and
/// the dispatch site stays a single call.
/// </summary>
public static class GraphEdgeAuthority
{
    /// <summary>The layer whose nodes act as dispatch ROOTS (they are handed work, then hand it on).</summary>
    internal const string OperationLayer = "operation";

    /// <summary>The layer whose nodes are dispatch TARGETS (workers).</summary>
    internal const string ExecutionLayer = "execution";

    public static bool IsDispatchAllowed(FrozenGraph? graph, string? workerSlug)
    {
        if (graph is null) return true;
        if (string.IsNullOrWhiteSpace(workerSlug)) return false;
        if (string.Equals(graph.Tier, FrozenGraphTiers.FreeForm, StringComparison.Ordinal)) return true;

        var nodeBySlug = graph.Nodes
            .GroupBy(node => node.AgentSlug, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
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

        // Free-form already returned true; every other tier must actually reach the node.
        return DispatchableNodeKeys(graph).Contains(workerNode.NodeKey);
    }

    /// <summary>
    /// The node keys a dispatch may reach: everything forward-reachable from the conversation
    /// node and from every operation-layer node, following edges whose target is an
    /// execution-layer node. Computed as one forward walk (BFS), which is what makes a
    /// chained stage edge legal without making backflow legal: the walk starts only at
    /// roots, and it never crosses into an operation node because that target is not
    /// enqueued and not recorded as reachable.
    /// </summary>
    internal static IReadOnlySet<string> DispatchableNodeKeys(FrozenGraph graph)
    {
        var layerByNodeKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
            if (!layerByNodeKey.ContainsKey(node.NodeKey))
                layerByNodeKey[node.NodeKey] = node.Layer;

        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(graph.ConversationNodeKey)
            && layerByNodeKey.TryGetValue(graph.ConversationNodeKey, out var conversationLayer)
            && string.Equals(conversationLayer, OperationLayer, StringComparison.Ordinal))
            roots.Add(graph.ConversationNodeKey);
        foreach (var node in graph.Nodes)
            if (string.Equals(node.Layer, OperationLayer, StringComparison.Ordinal) && !roots.Contains(node.NodeKey))
                roots.Add(node.NodeKey);

        var edgesBySource = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            if (!edgesBySource.TryGetValue(edge.SourceNodeKey, out var targets))
                edgesBySource[edge.SourceNodeKey] = targets = [];
            targets.Add(edge.TargetNodeKey);
        }

        // Execution nodes are the only reachable set. An operation node is a root (already
        // enqueued) and never a reachable target, which is where "execution -> operation
        // backflow is prohibited" is enforced structurally rather than by a second rule.
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var root in roots) queue.Enqueue(root);
        var visited = new HashSet<string>(roots, StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!edgesBySource.TryGetValue(current, out var targets)) continue;
            foreach (var target in targets)
            {
                if (!layerByNodeKey.TryGetValue(target, out var targetLayer)) continue;
                if (!string.Equals(targetLayer, ExecutionLayer, StringComparison.Ordinal)) continue;
                reachable.Add(target);
                if (visited.Add(target)) queue.Enqueue(target);
            }
        }
        return reachable;
    }

    /// <summary>
    /// True when an edge may exist at all: an <c>execution -&gt; operation</c> edge never
    /// (an executor cannot hand work up the graph). Every other direction —
    /// operation→operation, operation→execution, execution→execution — is a legal
    /// communication direction.
    /// </summary>
    public static bool IsEdgeDirectionAllowed(string? sourceLayer, string? targetLayer) =>
        !string.Equals(sourceLayer, ExecutionLayer, StringComparison.Ordinal)
        || !string.Equals(targetLayer, OperationLayer, StringComparison.Ordinal);

    /// <summary>
    /// The ordered stage dependencies a planner should read: every
    /// <c>execution -&gt; execution</c> edge as (predecessor slug, successor slug), sorted so
    /// two runs over the same frozen graph produce the same instruction text. Blank or
    /// unresolved node keys are skipped rather than surfaced as a dependency on nothing.
    /// </summary>
    public static IReadOnlyList<StageDependency> StageDependencies(FrozenGraph? graph)
    {
        if (graph is null) return [];
        var slugByNodeKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var layerByNodeKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            if (slugByNodeKey.ContainsKey(node.NodeKey)) continue;
            slugByNodeKey[node.NodeKey] = node.AgentSlug;
            layerByNodeKey[node.NodeKey] = node.Layer;
        }
        var dependencies = new List<StageDependency>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            if (!layerByNodeKey.TryGetValue(edge.SourceNodeKey, out var sourceLayer)
                || !layerByNodeKey.TryGetValue(edge.TargetNodeKey, out var targetLayer))
                continue;
            if (!string.Equals(sourceLayer, ExecutionLayer, StringComparison.Ordinal)
                || !string.Equals(targetLayer, ExecutionLayer, StringComparison.Ordinal))
                continue;
            var predecessor = slugByNodeKey[edge.SourceNodeKey];
            var successor = slugByNodeKey[edge.TargetNodeKey];
            if (string.IsNullOrWhiteSpace(predecessor) || string.IsNullOrWhiteSpace(successor)) continue;
            if (!seen.Add(predecessor + "\u001f" + successor)) continue;
            dependencies.Add(new StageDependency(predecessor, successor, edge.EdgeKey));
        }
        return dependencies
            .OrderBy(item => item.PredecessorSlug, StringComparer.Ordinal)
            .ThenBy(item => item.SuccessorSlug, StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>
/// One ordered stage hand-off inside the execution layer: <paramref name="PredecessorSlug"/>
/// runs before <paramref name="SuccessorSlug"/>. <paramref name="EdgeKey"/> is the declared
/// edge it came from, so a diagnostic can name the exact edge.
/// </summary>
public sealed record StageDependency(string PredecessorSlug, string SuccessorSlug, string EdgeKey);
