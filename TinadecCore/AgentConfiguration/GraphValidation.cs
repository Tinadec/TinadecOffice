using System.Text;
using System.Text.Json;

namespace TinadecCore.AgentConfiguration;

/// <summary>
/// Shared mode-graph validation for both publish paths (pack admission and manual
/// mode publish) — one implementation so the two historical "meeting enforcement"
/// sites cannot drift again.
///
/// Conversation node contract: a mode must declare exactly one resolvable
/// conversation node, via ① an explicit config marker (config.conversation true),
/// ② an operation-layer agent carrying a conversation capability
/// (user.respond / user.converse), or ③ the literal meeting node (agent slug or
/// node key/label, legacy 7-mode compatibility). At most one node may carry the
/// explicit marker.
///
/// Edge contract: edges are a communication topology, not a DAG — mutual pairs
/// (dispatch out / result back) are legal. Rejected: unknown endpoints, self-loops.
/// </summary>
internal static class GraphValidation
{
    /// <summary>
    /// Core capability that gates the always-v1 graph-orchestration pack surface
    /// (resources.tools, mode bindings, node relationship files). Packs carrying
    /// any of it must declare this capability: older Cores reject the unknown
    /// capability fail-closed (422 agent_pack_incompatible) instead of silently
    /// ignoring the semantics they cannot enforce.
    /// </summary>
    public const string GraphModePacksCapability = "graph_mode_packs";

    public const string ConversationCapability = "user.respond";
    public const string ConversationCapabilityAlias = "user.converse";

    /// <summary>Per-node relationship description file cap (S11).</summary>
    public const int MaxRelationshipBytes = 16 * 1024;

    internal sealed record AgentRef(string Key, string Slug, string Layer, IReadOnlyList<string> Capabilities);

    internal sealed record NodeRef(string NodeKey, string AgentKey, string Layer, string? Label, JsonElement Config, JsonElement? Relationship);

    internal sealed record EdgeRef(string EdgeKey, string Source, string Target);

    internal static void ValidateModeGraph(string modeKey, IReadOnlyList<NodeRef> nodes, IReadOnlyList<EdgeRef> edges, IReadOnlyList<AgentRef> agents)
    {
        var agentByKey = agents.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var nodeKeys = new HashSet<string>(StringComparer.Ordinal);
        var markedConversationNodes = 0;
        var conversationCapableNodes = 0;

        foreach (var node in nodes)
        {
            if (!nodeKeys.Add(node.NodeKey)) Invalid($"mode '{modeKey}' duplicate node '{node.NodeKey}'.");
            if (!agentByKey.TryGetValue(node.AgentKey, out var agent))
                Invalid($"mode '{modeKey}' references unknown agent '{node.AgentKey}'.");
            if (!string.Equals(node.Layer, agent.Layer, StringComparison.Ordinal))
                Invalid($"mode '{modeKey}' node '{node.NodeKey}' layer differs from agent '{node.AgentKey}'.");
            if (node.Config.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
                Invalid($"mode '{modeKey}' node '{node.NodeKey}' config must be an object.");

            var marked = IsConversationMarked(node.Config);
            if (marked)
            {
                markedConversationNodes++;
                conversationCapableNodes++;
            }
            else if (IsConversationCapable(node, agent))
            {
                conversationCapableNodes++;
            }

            if (node.Relationship is { } relationship && relationship.ValueKind == JsonValueKind.Object)
                ValidateRelationship(modeKey, node.NodeKey, relationship);
        }

        if (markedConversationNodes > 1)
            Invalid($"mode '{modeKey}' declares {markedConversationNodes} explicitly marked conversation nodes; at most one is allowed.");
        if (conversationCapableNodes == 0)
            Invalid($"mode '{modeKey}' must declare a conversation node (explicit config marker, an operation-layer agent with a conversation capability, or a literal meeting node).");

        foreach (var edge in edges)
        {
            if (!nodeKeys.Contains(edge.Source) || !nodeKeys.Contains(edge.Target))
                Invalid($"mode '{modeKey}' edge '{edge.EdgeKey}' references an unknown node.");
            if (string.Equals(edge.Source, edge.Target, StringComparison.Ordinal))
                Invalid($"mode '{modeKey}' edge '{edge.EdgeKey}' is a self-loop; communication edges must connect distinct nodes.");
        }
    }

    /// <summary>
    /// Relationship description file (five fields: duty / inputs_outputs /
    /// allowed_dispatch_targets / success_criteria / agent_types), capped at
    /// <see cref="MaxRelationshipBytes"/>. Present-but-incomplete is rejected: the
    /// file is compiled into the role prompt and drives dispatch validation, so a
    /// partial file would silently narrow semantics.
    /// </summary>
    internal static void ValidateRelationship(string modeKey, string nodeKey, JsonElement relationship)
    {
        foreach (var field in new[] { "duty", "inputs_outputs", "allowed_dispatch_targets", "success_criteria", "agent_types" })
        {
            if (!relationship.TryGetProperty(field, out _))
                Invalid($"mode '{modeKey}' node '{nodeKey}' relationship file is missing the '{field}' field.");
        }
        var serialized = JsonSerializer.Serialize(relationship);
        if (Encoding.UTF8.GetByteCount(serialized) > MaxRelationshipBytes)
            Invalid($"mode '{modeKey}' node '{nodeKey}' relationship file exceeds the {MaxRelationshipBytes} byte cap.");
    }

    internal static bool IsConversationMarked(JsonElement config) =>
        config.ValueKind == JsonValueKind.Object
        && config.TryGetProperty("conversation", out var marker)
        && (marker.ValueKind == JsonValueKind.True
            || (marker.ValueKind == JsonValueKind.String && string.Equals(marker.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

    internal static bool HasConversationCapability(IReadOnlyList<string> capabilities) =>
        capabilities.Any(capability =>
            string.Equals(capability, ConversationCapability, StringComparison.OrdinalIgnoreCase)
            || string.Equals(capability, ConversationCapabilityAlias, StringComparison.OrdinalIgnoreCase));

    private static bool IsConversationCapable(NodeRef node, AgentRef agent) =>
        (string.Equals(node.Layer, "operation", StringComparison.Ordinal) && HasConversationCapability(agent.Capabilities))
        || string.Equals(agent.Slug, "meeting", StringComparison.OrdinalIgnoreCase)
        || string.Equals(node.NodeKey, "meeting", StringComparison.OrdinalIgnoreCase)
        || string.Equals(node.Label, "meeting", StringComparison.OrdinalIgnoreCase);

    private static void Invalid(string message) => throw new InvalidDataException(message);
}
