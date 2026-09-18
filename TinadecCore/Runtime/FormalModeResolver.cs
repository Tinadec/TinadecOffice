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

    public Task<HashSet<string>?> GetEffectiveToolsForSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        GetEffectiveToolsAsync(sessionId, modeVersionIdOverride: null, ct);

    public Task<HashSet<string>?> GetEffectiveToolsForModeAsync(
        Guid sessionId,
        Guid modeVersionId,
        CancellationToken ct = default) =>
        GetEffectiveToolsAsync(sessionId, modeVersionId, ct);

    private async Task<HashSet<string>?> GetEffectiveToolsAsync(
        Guid sessionId,
        Guid? modeVersionIdOverride,
        CancellationToken ct)
    {
        SessionReference? session = null;
        Guid? effectiveModeVersionId = null;
        try
        {
            session = await _sessions.FindAsync(sessionId, ct).ConfigureAwait(false);
            if (session is null) return null;
            effectiveModeVersionId = modeVersionIdOverride ?? session.ModeVersionId;
            if (effectiveModeVersionId is null) return null;
            await using var cfg = await _cfgFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var mv = await cfg.ModeVersions.AsNoTracking().FirstOrDefaultAsync(x =>
                x.Id == effectiveModeVersionId.Value
                && x.TenantId == session.TenantId
                && x.WorkspaceId == session.WorkspaceId, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Agent mode version '{effectiveModeVersionId}' was not found.");
            if (!string.Equals(mv.Status, "published", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Agent mode version '{effectiveModeVersionId}' is not published.");
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
            return effectiveModeVersionId is null ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public Task<FormalModeRoster?> ResolveRosterAsync(Guid sessionId, CancellationToken ct = default) =>
        ResolveRosterAsync(sessionId, modeVersionIdOverride: null, ct);

    public Task<FormalModeRoster?> ResolveRosterForModeAsync(
        Guid sessionId,
        Guid modeVersionId,
        CancellationToken ct = default) =>
        ResolveRosterAsync(sessionId, modeVersionId, ct);

    private async Task<FormalModeRoster?> ResolveRosterAsync(
        Guid sessionId,
        Guid? modeVersionIdOverride,
        CancellationToken ct)
    {
        var sess = await _sessions.FindAsync(sessionId, ct).ConfigureAwait(false);
        if (sess is null) return null;
        var modeVersionId = modeVersionIdOverride ?? sess.ModeVersionId;
        if (modeVersionId is null) return null;
        try
        {
            await using var cfg = await _cfgFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var mv = await cfg.ModeVersions.AsNoTracking().FirstOrDefaultAsync(x =>
                x.Id == modeVersionId.Value
                && x.TenantId == sess.TenantId
                && x.WorkspaceId == sess.WorkspaceId, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Agent mode version '{modeVersionId}' was not found.");
            if (!string.Equals(mv.Status, "published", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Agent mode version '{modeVersionId}' is not published.");
            var nodes = ParseModeSnapshot(mv);
            var envelopes = ParseBindingEnvelopes(mv);
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

            // First pass: verify every node's pinned AgentVersion and collect the
            // raw per-node resolution inputs. Roster semantics (conversation
            // output/context access, lifecycles) are derived graph-natively in the
            // second pass once the conversation node and declared edges are known.
            var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var definitionInputs = new List<ConversationIdentityResolver.DefinitionInput>();
            var resolved = new List<ResolvedNode>();
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
                definitionInputs.Add(new ConversationIdentityResolver.DefinitionInput(
                    node.AgentDefinitionId, slug, node.Layer, RawJson(agent, "capabilities", "[]")));
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
                var systemPrompt = OptionalString(agent, "system_prompt") ?? string.Empty;
                var enabled = OptionalBoolean(agent, "enabled") ?? true;
                if (bindings.TryGetValue(node.AgentDefinitionId, out var binding))
                {
                    // 用户的覆盖是最近一次显式意图：优先于 mode 节点覆盖与 agent 定义。
                    if (binding.Mode == "fixed" && binding.ProviderInstanceId is { } bindingProvider)
                    {
                        // CLI/ACP 运行时的 fixed 绑定 model 为 null：省略 model 字段而非静默忽略整个绑定，
                        // 与 AgentConfigurationEndpoints.PutAgentRuntimeBinding 的写入口径一致。
                        var modelField = string.IsNullOrWhiteSpace(binding.Model) ? string.Empty : $",\"model\":{JsonSerializer.Serialize(binding.Model)}";
                        modelStrategyJson = $"{{\"kind\":\"fixed\",\"provider_instance_id\":\"{bindingProvider}\"{modelField}}}";
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
                resolved.Add(new ResolvedNode(
                    node, slug, layer, role, effectiveTools, modelStrategyJson, modelStrategySource, promptGraph,
                    ReadStringArray(agent, "capabilities"))
                {
                    SystemPrompt = systemPrompt,
                    Enabled = enabled,
                    Order = order
                });
            }

            // Graph-native derivation inputs: the declared edges and the resolved
            // conversation node (marker → capability → legacy meeting chain).
            var edges = ParseDeclaredEdges(mv);
            var edgeTargetNodeKeys = edges.Select(edge => edge.TargetNodeKey).ToHashSet(StringComparer.Ordinal);
            var nodeInputs = nodes.Select(node => new ConversationIdentityResolver.NodeInput(
                node.AgentDefinitionId, node.NodeKey, node.Layer, node.Label, node.Config)).ToArray();
            var conversation = ConversationIdentityResolver.Resolve(nodeInputs, definitionInputs);
            var conversationNodeKey = conversation?.NodeKey;
            var conversationSlug = conversation?.TemplateSlug;
            var conversationSpawnAuthority = conversation is not null && resolved
                .Where(item => string.Equals(item.Slug, conversation.TemplateSlug, StringComparison.OrdinalIgnoreCase))
                .Any(item => EffectiveCapabilities(envelopes, item).Any(capability =>
                    string.Equals(capability, ThreeNamespaceMap.SpawnTemporaryCapability, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(capability, ThreeNamespaceMap.SpawnAliasCapability, StringComparison.OrdinalIgnoreCase)));

            // Second pass: build the roster entries with graph-derived semantics.
            // Lifecycle ladder: conversation → session; operation non-conversation →
            // task; declared edge target → on_demand; else persistent. Context access
            // and direct user output belong to the conversation node alone.
            var operation = new List<RuntimeAgentRosterEntry>();
            var execution = new List<RuntimeAgentRosterEntry>();
            foreach (var item in resolved)
            {
                var isConversation = string.Equals(item.Node.NodeKey, conversationNodeKey, StringComparison.Ordinal);
                var lifecycle = item.Layer switch
                {
                    _ when isConversation => "session",
                    "operation" => "task",
                    _ when edgeTargetNodeKeys.Contains(item.Node.NodeKey) => "on_demand",
                    _ => "persistent"
                };
                var entry = new RuntimeAgentRosterEntry(
                    item.Slug,
                    item.Layer,
                    item.Role,
                    lifecycle,
                    EffectiveCapabilities(envelopes, item),
                    isConversation,
                    isConversation ? "manage" : "read",
                    item.EffectiveTools,
                    item.Slug,
                    item.Node.AgentDefinitionId,
                    item.Node.AgentVersionId,
                    item.Node.AgentVersionHash)
                {
                    SystemPrompt = item.SystemPrompt,
                    ModelStrategyJson = item.ModelStrategyJson,
                    ModelStrategySource = item.ModelStrategySource,
                    Enabled = item.Enabled,
                    RosterOrder = item.Order,
                    PromptPipelineId = item.Node.PromptPipelineId,
                    PromptVersionId = item.Node.PromptVersionId,
                    PromptVersionContentHash = item.Node.PromptVersionHash ?? string.Empty,
                    PromptGraphJson = item.PromptGraph,
                    ResourceGrants = envelopes.NodeGrants.TryGetValue(item.Node.NodeKey, out var grants) ? grants : []
                };
                if (string.Equals(item.Layer, "operation", StringComparison.Ordinal)) operation.Add(entry);
                else execution.Add(entry);
            }
            if (operation.Count == 0)
                throw new InvalidDataException($"Published agent mode '{mv.AgentModeId}' has no operation-layer agent.");
            // The execution layer is optional only for the free_form shape: no
            // declared edges and a conversation identity that holds spawn authority
            // (it builds its workers itself instead of dispatching a declared
            // roster). Anything else without execution agents is a broken topology.
            if (execution.Count == 0 && !(edges.Count == 0 && conversationSpawnAuthority))
                throw new InvalidDataException($"Published agent mode '{mv.AgentModeId}' has no execution-layer agent.");

            var graphNodes = resolved
                .Select(item => new DeclaredGraphNode(item.Node.NodeKey, item.Slug, item.Layer))
                .ToArray();
            var spawnable = await ResolveSpawnableTemplatesAsync(cfg, mv, nodes, sess.TenantId, sess.WorkspaceId, envelopes, ct).ConfigureAwait(false);

            return new FormalModeRoster(operation, execution, mv.Id, mv.Version, mv.TopologyHash, $"mode:{mv.AgentModeId}:{mv.Version}")
            {
                AgentModeId = mv.AgentModeId,
                Edges = edges,
                HasDeclaredEdges = edges.Count > 0,
                GraphNodes = graphNodes,
                ConversationNodeKey = conversationNodeKey,
                ConversationTemplateSlug = conversationSlug,
                SpawnableTemplates = spawnable
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ResolveRosterAsync failed for {SessionId}", sessionId);
            throw new InvalidDataException($"Formal agent mode version '{modeVersionId}' could not be verified.", ex);
        }
    }

    /// <summary>
    /// Resolves the union of relationship-file <c>agent_types</c> across the mode
    /// snapshot's nodes into hash-pinned spawnable templates. Each slug must resolve
    /// to an enabled AgentDefinition with a published AgentVersion in the same
    /// tenant/workspace; the version's immutable snapshot is hash-verified and its
    /// tool_scope/capabilities are frozen. Unknown or unpublished slugs fail closed.
    /// </summary>
    private async Task<IReadOnlyList<DeclaredSpawnableTemplate>> ResolveSpawnableTemplatesAsync(
        AgentConfigurationDbContext cfg,
        ModeVersionRecord mv,
        IReadOnlyList<ModeNodeSnapshot> nodes,
        Guid tenantId,
        Guid workspaceId,
        BindingEnvelopes envelopes,
        CancellationToken ct)
    {
        var slugs = nodes
            .SelectMany(node => node.RelationshipAgentTypes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (slugs.Length == 0) return [];

        // Tolerate multiple definition rows per slug (pack re-publishes create
        // revisioned rows): the newest enabled row wins.
        var definitions = (await cfg.AgentDefinitions.AsNoTracking()
            .Where(definition => definition.TenantId == tenantId
                && definition.WorkspaceId == workspaceId
                && !definition.ArchivedAt.HasValue
                && definition.Enabled
                && slugs.Contains(definition.Slug))
            .ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(definition => definition.Slug, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(definition => definition.Version).First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<DeclaredSpawnableTemplate>();
        foreach (var slug in slugs)
        {
            if (!definitions.TryGetValue(slug, out var definition))
                throw new InvalidDataException($"Relationship file declares agent_types '{slug}' but no such enabled agent definition exists.");
            var version = await cfg.AgentVersions.AsNoTracking()
                .Where(item => item.AgentDefinitionId == definition.Id
                    && item.TenantId == tenantId
                    && item.WorkspaceId == workspaceId
                    && item.Status == "published")
                .OrderByDescending(item => item.Version)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Relationship file declares agent_types '{slug}' but the agent definition has no published version.");
            VerifyHash(version.SnapshotJson, version.ContentHash, version.ContentHash, $"AgentVersion '{version.Id}' (spawnable '{slug}')");
            using var document = JsonDocument.Parse(version.SnapshotJson);
            var agent = document.RootElement;
            if (!string.Equals(RequiredString(agent, "slug"), slug, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"AgentVersion '{version.Id}' snapshot slug does not match spawnable declaration '{slug}'.");
            if (string.Equals(RequiredString(agent, "layer"), "operation", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Spawnable template '{slug}' must be an execution-layer agent.");
            var toolScope = ReadStringArray(agent, "tool_scope");
            if (envelopes.TemplateToolFaces.TryGetValue(slug, out var face))
            {
                // The binding envelope may narrow the template's tool face
                // (narrowing only — publish gate enforces ⊆ at install time).
                toolScope = toolScope.Where(tool => face.Contains(tool, StringComparer.OrdinalIgnoreCase)).ToArray();
            }
            result.Add(new DeclaredSpawnableTemplate(
                slug,
                definition.Id,
                version.Id,
                version.ContentHash,
                RequiredString(agent, "role"),
                toolScope,
                ReadStringArray(agent, "capabilities"))
            {
                ResourceGrants = envelopes.TemplateGrants.TryGetValue(slug, out var grants) ? grants : []
            });
        }
        return result.OrderBy(item => item.Slug, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Parses the mode snapshot's pack binding envelopes. A binding's envelope
    /// may carry:
    /// • <c>resources</c> — <c>{"read":[prefixes], "write":[prefixes]}</c>,
    ///   workspace-relative forward-slash prefixes (empty string = the whole
    ///   workspace), frozen into node grants (by <c>node_key</c>) and
    ///   spawnable-template grants (by <c>agent_ref</c>); absent = grant nothing;
    /// • <c>capabilities</c> — a string array that NARROWS the node's template
    ///   capabilities for this mode (narrowing only; absent = inherit). This is
    ///   what makes one conversation template serve both a self_dispatch mode
    ///   (spawn authority kept) and a deterministic mode (spawn narrowed away);
    /// • <c>tools</c> — a string array narrowing a spawnable template's tool
    ///   face (its frozen ceiling input).
    /// </summary>
    private static BindingEnvelopes ParseBindingEnvelopes(ModeVersionRecord version)
    {
        Dictionary<string, IReadOnlyList<FrozenResourceGrant>> nodeGrants = new(StringComparer.Ordinal);
        Dictionary<string, string[]> nodeCapabilities = new(StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<FrozenResourceGrant>> templateGrants = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IReadOnlyList<string>> templateToolFaces = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(version.SnapshotJson)) return new BindingEnvelopes(nodeGrants, nodeCapabilities, templateGrants, templateToolFaces);
        JsonElement root;
        try
        {
            using var probe = JsonDocument.Parse(version.SnapshotJson);
            root = probe.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new BindingEnvelopes(nodeGrants, nodeCapabilities, templateGrants, templateToolFaces);
        }
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("bindings", out var bindingArray)
            || bindingArray.ValueKind != JsonValueKind.Array)
        {
            return new BindingEnvelopes(nodeGrants, nodeCapabilities, templateGrants, templateToolFaces);
        }
        foreach (var binding in bindingArray.EnumerateArray())
        {
            if (binding.ValueKind != JsonValueKind.Object
                || !binding.TryGetProperty("envelope", out var envelope)
                || envelope.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (envelope.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Object)
            {
                var grants = new List<FrozenResourceGrant>();
                foreach (var (property, level) in new[] { ("read", "read"), ("write", "write") })
                {
                    if (!resources.TryGetProperty(property, out var prefixes) || prefixes.ValueKind != JsonValueKind.Array) continue;
                    foreach (var prefix in prefixes.EnumerateArray())
                    {
                        if (prefix.ValueKind != JsonValueKind.String) continue;
                        grants.Add(new FrozenResourceGrant(NormalizePathPrefix(prefix.GetString()), level));
                    }
                }
                if (grants.Count > 0)
                {
                    if (TryBindingKey(binding, "node_key", out var nodeKey)) nodeGrants[nodeKey] = grants;
                    if (TryBindingKey(binding, "agent_ref", out var agentRef)) templateGrants[agentRef] = grants;
                }
            }

            if (envelope.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Array)
            {
                var narrowed = capabilities.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    .Select(item => item.GetString()!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (TryBindingKey(binding, "node_key", out var nodeKey)) nodeCapabilities[nodeKey] = narrowed;
            }

            if (envelope.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                var face = tools.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    .Select(item => item.GetString()!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (face.Length > 0 && TryBindingKey(binding, "agent_ref", out var agentRef)) templateToolFaces[agentRef] = face;
            }
        }
        return new BindingEnvelopes(nodeGrants, nodeCapabilities, templateGrants, templateToolFaces);
    }

    private static bool TryBindingKey(JsonElement binding, string property, out string value)
    {
        value = string.Empty;
        if (!binding.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            return false;
        }
        value = element.GetString()!.Trim();
        return true;
    }

    /// <summary>Template capabilities narrowed by the node's binding envelope (envelope wins when it declares a list).</summary>
    private static IReadOnlyList<string> EffectiveCapabilities(BindingEnvelopes envelopes, ResolvedNode item) =>
        envelopes.NodeCapabilities.TryGetValue(item.Node.NodeKey, out var narrowed)
            ? item.Capabilities.Where(capability => narrowed.Contains(capability, StringComparer.OrdinalIgnoreCase)).ToArray()
            : item.Capabilities;

    private static string NormalizePathPrefix(string? value)
    {
        var prefix = (value ?? string.Empty).Trim().Replace('\\', '/');
        if (prefix.StartsWith("./", StringComparison.Ordinal)) prefix = prefix[2..];
        prefix = prefix.TrimStart('/');
        if (prefix.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains(".."))
            throw new InvalidDataException("Resource grant prefixes must not escape the workspace root ('..' is not allowed).");
        return prefix.Length == 0 ? string.Empty : prefix.EndsWith('/') ? prefix : prefix + "/";
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
            JsonElement? config = node.TryGetProperty("config", out var configElement) && configElement.ValueKind == JsonValueKind.Object
                ? configElement.Clone()
                : null;
            // Relationship files are optional per node; when present, the
            // structured `agent_types` whitelist is the spawnable-template source.
            List<string> relationshipAgentTypes = [];
            if (node.TryGetProperty("relationship", out var relationship) && relationship.ValueKind == JsonValueKind.Object
                && relationship.TryGetProperty("agent_types", out var agentTypes))
            {
                relationshipAgentTypes = ReadStringArray(relationship, "agent_types").ToList();
            }
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
                RawNullableJson(node, "model_strategy_override"),
                OptionalString(node, "label"),
                config,
                relationshipAgentTypes));
        }
        if (result.Count == 0) throw new InvalidDataException($"ModeVersion '{version.Id}' snapshot contains no nodes.");
        if (result.Select(item => item.NodeKey).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new InvalidDataException($"ModeVersion '{version.Id}' snapshot contains duplicate node keys.");
        if (!result.Select(item => item.NodeKey).SequenceEqual(result.Select(item => item.NodeKey).OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException($"ModeVersion '{version.Id}' snapshot nodes are not in canonical order.");
        return result;
    }

    /// <summary>
    /// Parses the declared edges frozen into the mode snapshot. Tolerant of a
    /// missing or empty edges array (returns an empty list — the free_form case);
    /// malformed individual edges are rejected rather than silently dropped so a
    /// corrupt topology cannot walk a partially declared graph. An edge's
    /// <c>condition</c> object is frozen verbatim as its data contract.
    /// </summary>
    private static IReadOnlyList<DeclaredGraphEdge> ParseDeclaredEdges(ModeVersionRecord version)
    {
        if (string.IsNullOrWhiteSpace(version.SnapshotJson)) return [];
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(version.SnapshotJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return [];
        }
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("edges", out var edgeArray)
            || edgeArray.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var edges = new List<DeclaredGraphEdge>();
        foreach (var edge in edgeArray.EnumerateArray())
        {
            if (edge.ValueKind != JsonValueKind.Object) continue;
            JsonElement? dataContract = null;
            if (edge.TryGetProperty("condition", out var condition) && condition.ValueKind == JsonValueKind.Object
                && condition.EnumerateObject().Any())
            {
                dataContract = condition.Clone();
            }
            edges.Add(new DeclaredGraphEdge(
                RequiredString(edge, "edge_key"),
                RequiredString(edge, "source_node_key"),
                RequiredString(edge, "target_node_key"))
            { DataContract = dataContract });
        }
        return edges;
    }

    private static void VerifyHash(string body, string? indexedHash, string? frozenHash, string label)    {
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

    // One node's fully-resolved first-pass output; roster semantics are applied in
    // the second pass once the conversation node and edge set are known.
    private sealed record ResolvedNode(
        ModeNodeSnapshot Node,
        string Slug,
        string Layer,
        string Role,
        IReadOnlyList<string> EffectiveTools,
        string ModelStrategyJson,
        string ModelStrategySource,
        string PromptGraph,
        IReadOnlyList<string> Capabilities)
    {
        public string SystemPrompt { get; init; } = string.Empty;
        public bool Enabled { get; init; } = true;
        public int Order { get; init; }
    }

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
        string? ModelStrategyOverrideJson,
        string? Label = null,
        JsonElement? Config = null,
        IReadOnlyList<string>? RelationshipAgentTypes = null);

    /// <summary>Per-mode binding envelope narrowing parsed from the snapshot.</summary>
    private sealed record BindingEnvelopes(
        IReadOnlyDictionary<string, IReadOnlyList<FrozenResourceGrant>> NodeGrants,
        IReadOnlyDictionary<string, string[]> NodeCapabilities,
        IReadOnlyDictionary<string, IReadOnlyList<FrozenResourceGrant>> TemplateGrants,
        IReadOnlyDictionary<string, IReadOnlyList<string>> TemplateToolFaces);
}
