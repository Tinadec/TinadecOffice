using System.Text.Json;

namespace TinadecCore.Runtime;

/// <summary>
/// ConversationIdentity resolution (DmaEA graph orchestration): picks the mode
/// node that carries the governance-layer conversation role. The choice is frozen
/// at session creation and immutable across mode switches.
///
/// Resolution order, all inputs from the published mode snapshot (no live state):
/// ① a node explicitly marked conversation:true in its config →
/// ② an operation-layer node whose agent definition holds a conversation
///    capability (user.respond, alias user.converse) →
/// ③ the literal "meeting" node (node_key or label, legacy 7-mode compatibility).
///
/// A null result means the mode declares no conversation node; callers must fail
/// closed with the existing agent_mode_not_configured semantics instead of
/// guessing an identity.
/// </summary>
public static class ConversationIdentityResolver
{
    public const string ConversationCapability = "user.respond";
    public const string ConversationCapabilityAlias = "user.converse";

    public sealed record NodeInput(Guid AgentDefinitionId, string NodeKey, string Layer, string? Label, JsonElement? Config);

    public sealed record DefinitionInput(Guid Id, string Slug, string Layer, string? CapabilitiesJson);

    public sealed record ConversationIdentity(string NodeKey, string TemplateSlug);

    /// <summary>Parses a published mode-version snapshot and resolves its conversation node.</summary>
    public static ConversationIdentity? Resolve(string? snapshotJson, IReadOnlyList<DefinitionInput> definitions)
    {
        if (!TryParseNodes(snapshotJson, out var nodes)) return null;
        return Resolve(nodes, definitions);
    }

    /// <summary>
    /// Validates a caller-requested conversation node: it must exist in the mode
    /// snapshot and satisfy at least one resolution tier. Anything else is an
    /// invalid identity request, not a silent fallback.
    /// </summary>
    public static ConversationIdentity? ResolveRequested(string requestedNodeKey, string? snapshotJson, IReadOnlyList<DefinitionInput> definitions)
    {
        if (!TryParseNodes(snapshotJson, out var nodes)) return null;
        var requested = nodes.FirstOrDefault(n => string.Equals(n.NodeKey, requestedNodeKey, StringComparison.Ordinal));
        if (requested is null) return null;
        var resolved = Resolve([requested], definitions);
        return resolved is not null ? new ConversationIdentity(requested.NodeKey, resolved.TemplateSlug) : null;
    }

    public static ConversationIdentity? Resolve(IReadOnlyList<NodeInput> nodes, IReadOnlyList<DefinitionInput> definitions)
    {
        if (nodes.Count == 0) return null;
        var byDefinitionId = definitions.ToDictionary(d => d.Id);
        var candidates = nodes
            .Where(n => byDefinitionId.ContainsKey(n.AgentDefinitionId))
            .OrderBy(n => n.NodeKey, StringComparer.Ordinal)
            .ToArray();

        // ① explicit conversation marker in the node config
        var marked = candidates.FirstOrDefault(n => IsConversationMarked(n.Config));
        if (marked is not null)
            return new ConversationIdentity(marked.NodeKey, byDefinitionId[marked.AgentDefinitionId].Slug);

        // ② operation-layer node whose definition carries a conversation capability
        var conversing = candidates.FirstOrDefault(n =>
            string.Equals(n.Layer, "operation", StringComparison.Ordinal)
            && HasConversationCapability(byDefinitionId[n.AgentDefinitionId]));
        if (conversing is not null)
            return new ConversationIdentity(conversing.NodeKey, byDefinitionId[conversing.AgentDefinitionId].Slug);

        // ③ literal "meeting" node — legacy modes without markers or capability data
        var legacy = candidates.FirstOrDefault(IsLegacyMeetingNode);
        return legacy is null ? null : new ConversationIdentity(legacy.NodeKey, byDefinitionId[legacy.AgentDefinitionId].Slug);
    }

    private static bool TryParseNodes(string? snapshotJson, out List<NodeInput> nodes)
    {
        nodes = [];
        if (string.IsNullOrWhiteSpace(snapshotJson)) return false;
        JsonElement snapshot;
        try
        {
            using var document = JsonDocument.Parse(snapshotJson);
            snapshot = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }
        if (snapshot.ValueKind != JsonValueKind.Object
            || !snapshot.TryGetProperty("nodes", out var nodeElements)
            || nodeElements.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        foreach (var element in nodeElements.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var nodeKey = element.TryGetProperty("node_key", out var keyElement) && keyElement.ValueKind == JsonValueKind.String
                ? keyElement.GetString()
                : null;
            var definitionId = element.TryGetProperty("agent_definition_id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                && Guid.TryParse(idElement.GetString(), out var parsedId)
                ? parsedId
                : (Guid?)null;
            if (nodeKey is null || definitionId is null) continue;
            var layer = element.TryGetProperty("layer", out var layerElement) && layerElement.ValueKind == JsonValueKind.String
                ? layerElement.GetString()
                : null;
            var label = element.TryGetProperty("label", out var labelElement) && labelElement.ValueKind == JsonValueKind.String
                ? labelElement.GetString()
                : null;
            JsonElement? config = element.TryGetProperty("config", out var configElement) && configElement.ValueKind == JsonValueKind.Object
                ? configElement.Clone()
                : null;
            nodes.Add(new NodeInput(definitionId.Value, nodeKey, layer ?? string.Empty, label, config));
        }
        return true;
    }

    private static bool IsConversationMarked(JsonElement? config) =>
        config is { } value
        && value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("conversation", out var marker)
        && (marker.ValueKind == JsonValueKind.True
            || (marker.ValueKind == JsonValueKind.String && string.Equals(marker.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

    private static bool HasConversationCapability(DefinitionInput definition)
    {
        if (string.IsNullOrWhiteSpace(definition.CapabilitiesJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(definition.CapabilitiesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var capability = item.GetString();
                if (string.Equals(capability, ConversationCapability, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(capability, ConversationCapabilityAlias, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsLegacyMeetingNode(NodeInput node) =>
        string.Equals(node.NodeKey, "meeting", StringComparison.OrdinalIgnoreCase)
        || string.Equals(node.Label, "meeting", StringComparison.OrdinalIgnoreCase);
}
