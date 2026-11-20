using System.Text;
using System.Text.Json;
using TinadecCore.Abstractions.Ports;

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

    public static readonly IReadOnlyList<string> ValidOrchestrationTiers =
    [
        "solo_dispatch",
        "free_form",
        "self_dispatch",
        "deterministic"
    ];

    internal static string? NormalizeOrchestrationTier(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier)) return null;
        var normalized = tier.Trim().ToLowerInvariant();
        if (string.Equals(normalized, "solo", StringComparison.Ordinal))
            return "solo_dispatch";
        if (ValidOrchestrationTiers.Contains(normalized, StringComparer.Ordinal))
            return normalized;
        Invalid($"orchestration_tier '{tier}' is not supported; expected one of: {string.Join(", ", ValidOrchestrationTiers)} (or alias 'solo').");
        return null;
    }

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
    /// Relationship description file (optional fields: duty / inputs_outputs /
    /// allowed_dispatch_targets / success_criteria / agent_types / subscriptions),
    /// capped at <see cref="MaxRelationshipBytes"/>. Individual fields are optional so
    /// lightweight authored graphs can declare only what they need, while each present
    /// field is type-checked and <c>subscriptions</c> is validated against
    /// <see cref="GovernanceTopics"/>.
    /// </summary>
    internal static void ValidateRelationship(string modeKey, string nodeKey, JsonElement relationship)
    {
        if (relationship.TryGetProperty("duty", out var duty) && duty.ValueKind != JsonValueKind.Null)
        {
            if (duty.ValueKind != JsonValueKind.String)
                Invalid($"mode '{modeKey}' node '{nodeKey}' relationship file 'duty' must be a string.");
        }
        if (relationship.TryGetProperty("inputs_outputs", out var io) && io.ValueKind != JsonValueKind.Null)
        {
            if (io.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array))
                Invalid($"mode '{modeKey}' node '{nodeKey}' relationship file 'inputs_outputs' must be a string, object, or string array.");
            if (io.ValueKind == JsonValueKind.Array && io.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                Invalid($"mode '{modeKey}' node '{nodeKey}' relationship file 'inputs_outputs' array must contain strings only.");
        }
        if (relationship.TryGetProperty("success_criteria", out var criteria) && criteria.ValueKind != JsonValueKind.Null)
        {
            if (criteria.ValueKind is not (JsonValueKind.String or JsonValueKind.Array))
                Invalid($"mode '{modeKey}' node '{nodeKey}' relationship file 'success_criteria' must be a string or string array.");
            if (criteria.ValueKind == JsonValueKind.Array && criteria.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                Invalid($"mode '{modeKey}' node '{nodeKey}' relationship file 'success_criteria' array must contain strings only.");
        }
        if (relationship.TryGetProperty("allowed_dispatch_targets", out var targets) && targets.ValueKind != JsonValueKind.Null)
        {
            if (targets.ValueKind != JsonValueKind.Array || targets.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                Invalid($"mode '{modeKey}' node '{nodeKey}' relationship file 'allowed_dispatch_targets' must be an array of strings.");
        }
        if (relationship.TryGetProperty("agent_types", out var agentTypes) && agentTypes.ValueKind != JsonValueKind.Null)
        {
            if (agentTypes.ValueKind != JsonValueKind.Array || agentTypes.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                Invalid($"mode '{modeKey}' node '{nodeKey}' relationship file 'agent_types' must be an array of strings.");
        }
        if (relationship.TryGetProperty("subscriptions", out var subscriptions) && subscriptions.ValueKind != JsonValueKind.Null)
        {
            if (subscriptions.ValueKind != JsonValueKind.Array)
                Invalid($"mode '{modeKey}' node '{nodeKey}' relationship file 'subscriptions' must be an array of topic names.");
            foreach (var topic in subscriptions.EnumerateArray())
            {
                if (topic.ValueKind != JsonValueKind.String || !GovernanceTopics.IsKnown(topic.GetString()))
                    Invalid($"mode '{modeKey}' node '{nodeKey}' subscribes to unknown topic {topic.GetRawText()}; known topics: {string.Join(", ", GovernanceTopics.All)}.");
            }
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
