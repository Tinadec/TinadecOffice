using System.Text.Json;

namespace TinadecCore.DmaEA;

/// <summary>
/// Declared-graph projection (additive-only) for the orchestration endpoints:
/// parses a published mode-version snapshot into { nodes, edges } with the
/// conversation marker and per-node relationship file surfaced. Unknown snapshot
/// fields are ignored; null when no snapshot exists (legacy rows), so callers keep
/// emitting graph:null instead of inventing an empty graph. The projection is
/// read-only over the frozen snapshot — the same document the graph-driven
/// resolver walks — so what the UI draws is what the engine runs.
/// </summary>
internal static class OrchestrationGraphProjection
{
    public static object? FromSnapshot(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return null;
        JsonElement snapshot;
        try
        {
            using var document = JsonDocument.Parse(snapshotJson);
            snapshot = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
        if (snapshot.ValueKind != JsonValueKind.Object
            || !snapshot.TryGetProperty("nodes", out var nodesElement)
            || nodesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var nodes = new List<object>();
        foreach (var node in nodesElement.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            var nodeKey = GetString(node, "node_key");
            if (nodeKey is null) continue;
            nodes.Add(new
            {
                node_key = nodeKey,
                label = GetString(node, "label"),
                layer = GetString(node, "layer"),
                agent_definition_id = GetString(node, "agent_definition_id"),
                is_conversation = IsConversationNode(node),
                relationship = node.TryGetProperty("relationship", out var relationship) && relationship.ValueKind == JsonValueKind.Object
                    ? (object?)relationship
                    : null
            });
        }

        var edges = new List<object>();
        if (snapshot.TryGetProperty("edges", out var edgesElement) && edgesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var edge in edgesElement.EnumerateArray())
            {
                if (edge.ValueKind != JsonValueKind.Object) continue;
                var edgeKey = GetString(edge, "edge_key");
                if (edgeKey is null) continue;
                edges.Add(new
                {
                    edge_key = edgeKey,
                    source_node_key = GetString(edge, "source_node_key"),
                    target_node_key = GetString(edge, "target_node_key"),
                    data_contract = edge.TryGetProperty("condition", out var condition) && condition.ValueKind == JsonValueKind.Object
                        ? (object?)condition
                        : null
                });
            }
        }

        return new { nodes, edges };
    }

    /// <summary>
    /// Observed data flows projected from the durable task graph: every task in
    /// the checkpoint is a dispatch through the conversation identity (or the
    /// legacy "meeting" fallback) to the task's resolved worker, with the result
    /// flowing back on completion. Derived from checkpoint state, so replay and
    /// the orchestration projection agree on the same durable source.
    /// </summary>
    public static IReadOnlyList<object> FlowsFromCheckpoint(
        FullDuplexCheckpointV1? checkpoint,
        string? conversationTemplateSlug)
    {
        if (checkpoint is null) return [];
        var source = string.IsNullOrWhiteSpace(conversationTemplateSlug) ? "meeting" : conversationTemplateSlug!;
        return checkpoint.Tasks.Select(task => (object)new
        {
            from = source,
            to = string.IsNullOrWhiteSpace(task.WorkerAgentSlug) ? "worker.general" : task.WorkerAgentSlug.Trim(),
            task_key = task.TaskKey,
            kind = "dispatch",
            status = task.Status
        }).ToArray();
    }

    private static bool IsConversationNode(JsonElement node) =>
        node.TryGetProperty("config", out var config)
        && config.ValueKind == JsonValueKind.Object
        && config.TryGetProperty("conversation", out var marker)
        && (marker.ValueKind == JsonValueKind.True
            || (marker.ValueKind == JsonValueKind.String && string.Equals(marker.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
