using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Runtime;

internal sealed class FormalModeResolver : IFormalModeResolver
{
    private readonly ISessionLocator _sessions;
    private readonly IDbContextFactory<AgentConfigurationDbContext> _cfgFactory;
    private readonly ILogger<FormalModeResolver> _logger;

    public FormalModeResolver(
        ISessionLocator sessions,
        IDbContextFactory<AgentConfigurationDbContext> cfgFactory,
        ILogger<FormalModeResolver> logger)
    {
        _sessions = sessions;
        _cfgFactory = cfgFactory;
        _logger = logger;
    }

    public async Task<HashSet<string>?> GetEffectiveToolsForSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        SessionReference? session = null;
        try
        {
            session = await _sessions.FindAsync(sessionId, ct).ConfigureAwait(false);
            if (session?.ModeVersionId is null) return null;
            await using var cfg = await _cfgFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var mv = await cfg.ModeVersions.AsNoTracking().FirstOrDefaultAsync(x =>
                x.Id == session.ModeVersionId.Value
                && x.TenantId == session.TenantId
                && x.WorkspaceId == session.WorkspaceId, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Agent mode version '{session.ModeVersionId}' was not found.");
            if (!string.Equals(mv.Status, "published", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Agent mode version '{session.ModeVersionId}' is not published.");
            var nodes = ParseModeSnapshot(mv);
            var union = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasWildcard = false;
            foreach (var node in nodes)
            {
                if (node.EffectiveTools.Contains("*", StringComparer.OrdinalIgnoreCase)) hasWildcard = true;
                else foreach (var tool in node.EffectiveTools) union.Add(tool);
            }
            return hasWildcard ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "*" } : union;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetEffectiveToolsForSession failed for {SessionId}", sessionId);
            // A formal mode that cannot be verified is an empty grant, never a
            // signal to fall back to the broader legacy TOML authorization set.
            return session?.ModeVersionId is null ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task<FormalModeRoster?> ResolveRosterAsync(Guid sessionId, CancellationToken ct = default)
    {
        var sess = await _sessions.FindAsync(sessionId, ct).ConfigureAwait(false);
        if (sess?.ModeVersionId is not { } modeVersionId) return null;
        try
        {
            await using var cfg = await _cfgFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var mv = await cfg.ModeVersions.AsNoTracking().FirstOrDefaultAsync(x =>
                x.Id == modeVersionId
                && x.TenantId == sess.TenantId
                && x.WorkspaceId == sess.WorkspaceId, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Agent mode version '{modeVersionId}' was not found.");
            if (!string.Equals(mv.Status, "published", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Agent mode version '{modeVersionId}' is not published.");
            var nodes = ParseModeSnapshot(mv);
            // 用户级运行时绑定（配置体验改造 A）：per-agent 的覆盖记录，优先级
            // user_binding > mode_node_override > agent_version.model_strategy。
            var bindings = await cfg.AgentRuntimeBindings.AsNoTracking()
                .Where(x => x.TenantId == sess.TenantId && x.WorkspaceId == sess.WorkspaceId)
                .ToDictionaryAsync(x => x.AgentDefinitionId, ct).ConfigureAwait(false);

            var versionIds = nodes.Select(node => node.AgentVersionId).Distinct().ToArray();
            var versions = await cfg.AgentVersions.AsNoTracking()
                .Where(version => versionIds.Contains(version.Id)
                    && version.TenantId == sess.TenantId
                    && version.WorkspaceId == sess.WorkspaceId)
                .ToDictionaryAsync(version => version.Id, ct).ConfigureAwait(false);
            var promptVersionIds = nodes.Where(node => node.PromptVersionId is not null)
                .Select(node => node.PromptVersionId!.Value).Distinct().ToArray();
            var promptVersions = await cfg.PromptVersions.AsNoTracking()
                .Where(version => promptVersionIds.Contains(version.Id)
                    && version.TenantId == sess.TenantId
                    && version.WorkspaceId == sess.WorkspaceId)
                .ToDictionaryAsync(version => version.Id, ct).ConfigureAwait(false);

            var operation = new List<RuntimeAgentRosterEntry>();
            var execution = new List<RuntimeAgentRosterEntry>();
            var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var order = 0; order < nodes.Count; order++)
            {
                var node = nodes[order];
                if (!versions.TryGetValue(node.AgentVersionId, out var version)
                    || version.AgentDefinitionId != node.AgentDefinitionId
                    || !string.Equals(version.Status, "published", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Mode node '{node.NodeKey}' does not reference a published AgentVersion owned by its agent definition.");
                }
                VerifyHash(version.SnapshotJson, version.ContentHash, node.AgentVersionHash, $"AgentVersion '{version.Id}'");
                using var agentDocument = JsonDocument.Parse(version.SnapshotJson);
                var agent = agentDocument.RootElement;
                var snapshotDefinitionId = RequiredGuid(agent, "id");
                if (snapshotDefinitionId != node.AgentDefinitionId)
                    throw new InvalidDataException($"AgentVersion '{version.Id}' snapshot has a different agent definition id.");
                var slug = RequiredString(agent, "slug");
                if (!slugs.Add(slug)) throw new InvalidDataException($"Mode version contains duplicate agent slug '{slug}'.");
                var layer = RequiredString(agent, "layer").Trim().ToLowerInvariant();
                if (layer is not ("operation" or "execution") || !string.Equals(layer, node.Layer, StringComparison.Ordinal))
                    throw new InvalidDataException($"Mode node '{node.NodeKey}' layer does not match AgentVersion '{version.Id}'.");
                if (!string.Equals(version.Layer, layer, StringComparison.Ordinal) || !string.Equals(version.Role, RequiredString(agent, "role"), StringComparison.Ordinal))
                    throw new InvalidDataException($"AgentVersion '{version.Id}' indexed layer/role differs from its immutable snapshot.");

                string promptGraph = string.Empty;
                if (node.PromptVersionId is { } promptVersionId)
                {
                    if (node.PromptPipelineId is not { } promptPipelineId || string.IsNullOrWhiteSpace(node.PromptVersionHash))
                        throw new InvalidDataException($"Mode node '{node.NodeKey}' has an incomplete prompt binding.");
                    if (!promptVersions.TryGetValue(promptVersionId, out var promptVersion)
                        || promptVersion.PromptPipelineId != promptPipelineId
                        || !string.Equals(promptVersion.Status, "published", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Mode node '{node.NodeKey}' does not reference a published PromptVersion owned by its prompt pipeline.");
                    VerifyHash(promptVersion.GraphJson, promptVersion.ContentHash, node.PromptVersionHash, $"PromptVersion '{promptVersion.Id}'");
                    promptGraph = promptVersion.GraphJson;
                }
                else if (node.PromptPipelineId is not null || !string.IsNullOrWhiteSpace(node.PromptVersionHash))
                {
                    throw new InvalidDataException($"Mode node '{node.NodeKey}' has an incomplete prompt binding.");
                }

                var role = RequiredString(agent, "role");
                var modelStrategyJson = node.ModelStrategyOverrideJson ?? RawJson(agent, "model_strategy", "{\"kind\":\"inherit\"}");
                var modelStrategySource = node.ModelStrategyOverrideJson is null ? "agent_version" : "mode_node_override";
                var effectiveTools = node.EffectiveTools;
                if (bindings.TryGetValue(node.AgentDefinitionId, out var binding))
                {
                    // 用户的覆盖是最近一次显式意图：优先于 mode 节点覆盖与 agent 定义。
                    if (binding.Mode == "fixed" && binding.ProviderInstanceId is { } bindingProvider && !string.IsNullOrWhiteSpace(binding.Model))
                    {
                        modelStrategyJson = $"{{\"kind\":\"fixed\",\"provider_instance_id\":\"{bindingProvider}\",\"model\":{JsonSerializer.Serialize(binding.Model)}}}";
                        modelStrategySource = "user_binding";
                    }
                    else if (binding.Mode == "route" && !string.IsNullOrWhiteSpace(binding.RoutePurpose))
                    {
                        modelStrategyJson = $"{{\"kind\":\"route\",\"route_purpose\":{JsonSerializer.Serialize(binding.RoutePurpose)}}}";
                        modelStrategySource = "user_binding";
                    }
                    if (!string.IsNullOrWhiteSpace(binding.ToolScopeOverrideJson))
                    {
                        effectiveTools = JsonSerializer.Deserialize<string[]>(binding.ToolScopeOverrideJson) ?? effectiveTools;
                    }
                }
                var entry = new RuntimeAgentRosterEntry(
                    slug,
                    layer,
                    role,
                    LifecycleFor(slug, role),
                    ReadStringArray(agent, "capabilities"),
                    DirectUserOutput: string.Equals(slug, "meeting", StringComparison.Ordinal),
                    ContextAccess: string.Equals(slug, "meeting", StringComparison.Ordinal) ? "manage" : "read",
                    effectiveTools,
                    slug,
                    node.AgentDefinitionId,
                    node.AgentVersionId,
                    node.AgentVersionHash)
                {
                    SystemPrompt = OptionalString(agent, "system_prompt") ?? string.Empty,
                    ModelStrategyJson = modelStrategyJson,
                    ModelStrategySource = modelStrategySource,
                    Enabled = OptionalBoolean(agent, "enabled") ?? true,
                    RosterOrder = order,
                    PromptPipelineId = node.PromptPipelineId,
                    PromptVersionId = node.PromptVersionId,
                    PromptVersionContentHash = node.PromptVersionHash ?? string.Empty,
                    PromptGraphJson = promptGraph
                };
                if (string.Equals(layer, "operation", StringComparison.Ordinal)) operation.Add(entry);
                else if (string.Equals(layer, "execution", StringComparison.Ordinal)) execution.Add(entry);
            }
            if (operation.Count == 0)
                throw new InvalidDataException($"Published agent mode '{mv.AgentModeId}' has no operation-layer agent.");
            if (execution.Count == 0)
                throw new InvalidDataException($"Published agent mode '{mv.AgentModeId}' has no execution-layer agent.");

            return new FormalModeRoster(operation, execution, mv.Id, mv.Version, mv.TopologyHash, $"mode:{mv.AgentModeId}:{mv.Version}")
            {
                AgentModeId = mv.AgentModeId
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ResolveRosterAsync failed for {SessionId}", sessionId);
            throw new InvalidDataException($"Formal agent mode version '{modeVersionId}' could not be verified.", ex);
        }
    }

    private static IReadOnlyList<ModeNodeSnapshot> ParseModeSnapshot(ModeVersionRecord version)
    {
        if (string.IsNullOrWhiteSpace(version.SnapshotJson))
            throw new InvalidDataException($"ModeVersion '{version.Id}' has no frozen snapshot.");
        VerifyHash(version.SnapshotJson, version.TopologyHash, version.TopologyHash, $"ModeVersion '{version.Id}'");
        using var document = JsonDocument.Parse(version.SnapshotJson);
        var root = document.RootElement;
        if (!string.Equals(RequiredString(root, "schema"), "tinadec.mode_version/v1", StringComparison.Ordinal))
            throw new InvalidDataException($"ModeVersion '{version.Id}' has an unsupported snapshot schema.");
        if (!root.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.Object || RequiredGuid(mode, "id") != version.AgentModeId)
            throw new InvalidDataException($"ModeVersion '{version.Id}' snapshot does not identify its mode definition.");
        if (!root.TryGetProperty("nodes", out var nodeArray) || nodeArray.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"ModeVersion '{version.Id}' snapshot has no node array.");

        var result = new List<ModeNodeSnapshot>();
        foreach (var node in nodeArray.EnumerateArray())
        {
            var layer = RequiredString(node, "layer").Trim().ToLowerInvariant();
            if (layer is not ("operation" or "execution"))
                throw new InvalidDataException("Mode snapshot node layer must be operation or execution.");
            var tools = ReadStringArray(node, "effective_tools", requireOrdinalOrder: true, requireProperty: true);
            result.Add(new ModeNodeSnapshot(
                RequiredString(node, "node_key"),
                RequiredGuid(node, "agent_definition_id"),
                RequiredGuid(node, "agent_version_id"),
                RequiredString(node, "agent_version_hash").Trim().ToLowerInvariant(),
                layer,
                tools,
                OptionalGuid(node, "prompt_pipeline_id"),
                OptionalGuid(node, "prompt_version_id"),
                OptionalString(node, "prompt_version_hash")?.Trim().ToLowerInvariant(),
                RawNullableJson(node, "model_strategy_override")));
        }
        if (result.Count == 0) throw new InvalidDataException($"ModeVersion '{version.Id}' snapshot contains no nodes.");
        if (result.Select(item => item.NodeKey).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new InvalidDataException($"ModeVersion '{version.Id}' snapshot contains duplicate node keys.");
        if (!result.Select(item => item.NodeKey).SequenceEqual(result.Select(item => item.NodeKey).OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException($"ModeVersion '{version.Id}' snapshot nodes are not in canonical order.");
        return result;
    }

    private static void VerifyHash(string body, string? indexedHash, string? frozenHash, string label)
    {
        if (string.IsNullOrWhiteSpace(indexedHash) || string.IsNullOrWhiteSpace(frozenHash))
            throw new InvalidDataException($"{label} has no immutable content hash.");
        var computed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        if (!string.Equals(computed, indexedHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(computed, frozenHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label} content hash verification failed.");
    }

    private static string RequiredString(JsonElement element, string property)
    {
        var value = OptionalString(element, property);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Frozen snapshot property '{property}' is required.")
            : value;
    }

    private static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? OptionalBoolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static Guid RequiredGuid(JsonElement element, string property) =>
        OptionalGuid(element, property) is { } value && value != Guid.Empty
            ? value
            : throw new InvalidDataException($"Frozen snapshot property '{property}' must be a non-empty UUID.");

    private static Guid? OptionalGuid(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement element,
        string property,
        bool requireOrdinalOrder = false,
        bool requireProperty = false)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            if (requireProperty) throw new InvalidDataException($"Frozen snapshot property '{property}' is required.");
            return [];
        }
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Frozen snapshot property '{property}' must be an array.");
        var result = value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToArray();
        if (result.Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length)
            throw new InvalidDataException($"Frozen snapshot property '{property}' contains duplicate values.");
        if (requireOrdinalOrder && !result.SequenceEqual(result.OrderBy(item => item, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException($"Frozen snapshot property '{property}' is not in canonical order.");
        return result;
    }

    private static string RawJson(JsonElement element, string property, string fallback) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.GetRawText()
            : fallback;

    private static string? RawNullableJson(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value.GetRawText()
            : null;

    private static string LifecycleFor(string slug, string role) => slug switch
    {
        "meeting" => "session",
        "task_planner" or "supervisor" => "task",
        _ when role.Contains("worker", StringComparison.OrdinalIgnoreCase) || slug.StartsWith("worker.", StringComparison.Ordinal) => "on_demand",
        _ => "persistent"
    };

    private sealed record ModeNodeSnapshot(
        string NodeKey,
        Guid AgentDefinitionId,
        Guid AgentVersionId,
        string AgentVersionHash,
        string Layer,
        IReadOnlyList<string> EffectiveTools,
        Guid? PromptPipelineId,
        Guid? PromptVersionId,
        string? PromptVersionHash,
        string? ModelStrategyOverrideJson);
}
