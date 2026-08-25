using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Lifecycle;
using TinadecCore.Models;
using TinadecCore.Persistence;

namespace TinadecCore.Runtime;

internal sealed class FormalModeResolver : IFormalModeResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ISessionLocator _sessions;
    private readonly IDbContextFactory<AgentConfigurationDbContext> _cfgFactory;
    private readonly IDbContextFactory<ModelControlDbContext> _modelFactory;
    private readonly IDbContextFactory<LifecycleDbContext> _lifecycleFactory;
    private readonly IContentStore _content;
    private readonly ISecretStore _secrets;
    private readonly ILifecycleManager _lifecycle;
    private readonly ILogger<FormalModeResolver> _logger;

    public FormalModeResolver(
        ISessionLocator sessions,
        IDbContextFactory<AgentConfigurationDbContext> cfgFactory,
        IDbContextFactory<ModelControlDbContext> modelFactory,
        IDbContextFactory<LifecycleDbContext> lifecycleFactory,
        IContentStore content,
        ISecretStore secrets,
        ILifecycleManager lifecycle,
        ILogger<FormalModeResolver> logger)
    {
        _sessions = sessions;
        _cfgFactory = cfgFactory;
        _modelFactory = modelFactory;
        _lifecycleFactory = lifecycleFactory;
        _content = content;
        _secrets = secrets;
        _lifecycle = lifecycle;
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

    public async Task<ChatResolution?> TryResolveFormalChatAsync(FrozenAgentChatRequest request, CancellationToken ct = default)
    {
        try
        {
            var sess = await _sessions.FindAsync(request.SessionId, ct).ConfigureAwait(false);
            if (sess is null) return null;
            JsonElement strat;
            try { strat = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.ModelStrategyJson) ? "{\"kind\":\"inherit\"}" : request.ModelStrategyJson).RootElement.Clone(); }
            catch { return null; }
            var kind = strat.TryGetProperty("kind", out var k) ? k.GetString()?.Trim().ToLowerInvariant() : strat.TryGetProperty("selection_kind", out var sk) ? sk.GetString()?.Trim().ToLowerInvariant() : "inherit";
            if (kind is not ("inherit" or "fixed" or "parent_select" or "cli" or "acp")) kind = "inherit";
            string? resolvedModel = null;
            string? resolvedProvider = null;
            string log = kind;
            ChatResolution? resolved = null;
            if (kind == "fixed")
            {
                var provIdStr = strat.TryGetProperty("provider_instance_id", out var p) ? p.GetString() : null;
                var model = strat.TryGetProperty("model", out var m) ? m.GetString() : strat.TryGetProperty("model_id", out var mi) ? mi.GetString() : null;
                resolvedModel = model; resolvedProvider = provIdStr;
                log = $"fixed:{provIdStr}:{model}";
                if (Guid.TryParse(provIdStr, out var provId))
                    resolved = await TryBuildResolutionFromProviderAsync(provId, model, ct).ConfigureAwait(false);
            }
            else if (kind is "cli" or "acp")
            {
                var runtimeId = strat.TryGetProperty("runtime_id", out var rt) ? rt.GetString() : strat.TryGetProperty("provider_instance_id", out var rp) ? rp.GetString() : null;
                // Legacy ACP runtimes carry a "legacy_provider:" prefix; strip to the provider instance id.
                if (runtimeId is not null && runtimeId.StartsWith("legacy_provider:", StringComparison.OrdinalIgnoreCase))
                    runtimeId = runtimeId["legacy_provider:".Length..];
                if (Guid.TryParse(runtimeId, out var runtimeProviderId))
                {
                    resolved = await TryBuildResolutionFromProviderAsync(runtimeProviderId, null, ct).ConfigureAwait(false);
                    resolvedModel = resolved?.Model;
                    resolvedProvider = runtimeId;
                    log = $"{kind}:{runtimeId}";
                }
                else
                {
                    log = $"{kind}: runtime_id '{runtimeId}' is not a provider instance id";
                }
            }
            else if (kind == "parent_select")
            {
                await using var mdb = await _modelFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                var candidates = await mdb.Providers.AsNoTracking().Where(p => p.Enabled && p.DeletedAt == null).ToListAsync(ct).ConfigureAwait(false);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var cand = candidates.ElementAtOrDefault(attempt);
                    if (cand is null) break;
                    var ver = await mdb.ProviderVersions.AsNoTracking().Where(v => v.ProviderId == cand.Id).OrderByDescending(v => v.Version).FirstOrDefaultAsync(ct).ConfigureAwait(false);
                    if (ver is null) continue;
                    var r = await TryBuildResolutionFromProviderAsync(cand.Id, null, ct).ConfigureAwait(false);
                    if (r is not null && r.IsAvailable) { resolved = r; resolvedModel = r.Model; resolvedProvider = cand.Id.ToString(); log = $"parent_select attempt {attempt + 1}:{cand.Id}"; break; }
                    log = $"parent_select attempt {attempt + 1}:{cand.Id} unavailable";
                }
                if (resolved is null) log = "parent_select: no enabled provider, fallback to default";
            }
            else
            {
                log = "inherit";
            }
            var payload = new { agent_id = request.AgentId, layer = request.Layer, kind, resolved_provider = resolvedProvider, resolved_model = resolvedModel ?? resolved?.Model, log, session_id = request.SessionId, mode_version_id = sess.ModeVersionId, run_id = request.RunId, turn_id = request.TurnId };
            try { await _lifecycle.AppendEventAsync(request.RunId, "model_selection", payload, $"Model selection {kind} for {request.AgentId}", cancellationToken: ct).ConfigureAwait(false); } catch { }
            try { await _lifecycle.AppendRunStreamAsync(request.RunId.ToString(), new DurableRunStreamAppend(request.TurnId, "model_selection", null, IdempotencyKey: $"run:{request.RunId}:model_sel:{request.AgentId}:{request.TurnId}"), ct).ConfigureAwait(false); } catch { }
            try
            {
                await using var ldb = await _lifecycleFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                ldb.ControlEventIndex.Add(new ControlEventIndexRecord { Id = Guid.NewGuid(), TenantId = sess.TenantId, WorkspaceId = sess.WorkspaceId, AggregateType = "run", AggregateId = request.RunId, EventType = "model_selection", RelativeFilePath = $"runs/{request.RunId}/model_selection.json", PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions)))).ToLowerInvariant(), Timestamp = DateTimeOffset.UtcNow });
                await ldb.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "control_event_index write failed for model_selection"); }
            if (kind is "fixed" or "parent_select" or "cli" or "acp") return resolved;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TryResolveFormalChat failed for {SessionId} {AgentId}", request.SessionId, request.AgentId);
            return null;
        }
    }

    private async Task<ChatResolution?> TryBuildResolutionFromProviderAsync(Guid providerId, string? overrideModel, CancellationToken ct)
    {
        try
        {
            await using var db = await _modelFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var provider = await db.Providers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == providerId && x.DeletedAt == null, ct).ConfigureAwait(false);
            if (provider is null || !provider.Enabled) return new ChatResolution { IsAvailable = false, Error = "Fixed provider not found or disabled." };
            var ver = await db.ProviderVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == provider.CurrentVersionId, ct).ConfigureAwait(false);
            if (ver is null) return new ChatResolution { IsAvailable = false, Error = "Provider has no current version." };
            await using var stream = await _content.OpenReadAsync(new ContentReference(ver.ContentReference, ver.ContentHash, ver.ContentLength, "application/json"), ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            string? Str(string key) => doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var protocol = ChatProtocols.Normalize(Str("protocol") ?? ChatProtocols.InferFromDriver(provider.Driver));
            var isCli = protocol is ChatProtocols.Acp or ChatProtocols.OpencodeServe;
            var model = !string.IsNullOrWhiteSpace(overrideModel) ? overrideModel : Str("model");
            if (string.IsNullOrWhiteSpace(model) && !isCli) return new ChatResolution { IsAvailable = false, Error = "Model not configured for fixed provider." };
            model ??= provider.Driver;
            string? baseUrl = isCli ? Str("server_url") : Str("base_url");
            if (string.IsNullOrWhiteSpace(baseUrl)) return new ChatResolution { IsAvailable = false, Error = isCli ? "CLI provider server_url missing." : "Provider base_url missing." };
            string? apiKey = null;
            if (!isCli)
            {
                if (string.IsNullOrWhiteSpace(provider.SecretReference)) return new ChatResolution { IsAvailable = false, Error = "Provider has no API key reference." };
                apiKey = await _secrets.GetAsync(provider.SecretReference, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(apiKey)) return new ChatResolution { IsAvailable = false, Error = "Provider API key not stored." };
            }
            return new ChatResolution { IsAvailable = true, BaseUrl = baseUrl, Model = model, ApiKey = apiKey, ModelId = $"{provider.Driver}/{model}", Protocol = protocol, ServerUrl = Str("server_url"), BinaryPath = Str("binary_path"), LaunchArgs = Str("launch_args"), HomePath = Str("home_path") };
        }
        catch (Exception ex) { return new ChatResolution { IsAvailable = false, Error = ex.Message }; }
    }

    private static HashSet<string> ParseTools(string? json)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return set;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray()) if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString())) set.Add(el.GetString()!.Trim());
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("allowed_tools", out var at) && at.ValueKind == JsonValueKind.Array) foreach (var el in at.EnumerateArray()) if (el.ValueKind == JsonValueKind.String) set.Add(el.GetString()!.Trim());
                if (root.TryGetProperty("tool_enabled", out var te) && te.ValueKind == JsonValueKind.Array) foreach (var el in te.EnumerateArray()) if (el.ValueKind == JsonValueKind.String) set.Add(el.GetString()!.Trim());
                if (root.TryGetProperty("tools", out var ts) && ts.ValueKind == JsonValueKind.Array) foreach (var el in ts.EnumerateArray()) if (el.ValueKind == JsonValueKind.String) set.Add(el.GetString()!.Trim());
            }
        }
        catch { }
        return set;
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
                var entry = new RuntimeAgentRosterEntry(
                    slug,
                    layer,
                    role,
                    LifecycleFor(slug, role),
                    ReadStringArray(agent, "capabilities"),
                    DirectUserOutput: string.Equals(slug, "meeting", StringComparison.Ordinal),
                    ContextAccess: string.Equals(slug, "meeting", StringComparison.Ordinal) ? "manage" : "read",
                    node.EffectiveTools,
                    slug,
                    node.AgentDefinitionId,
                    node.AgentVersionId,
                    node.AgentVersionHash)
                {
                    SystemPrompt = OptionalString(agent, "system_prompt") ?? string.Empty,
                    ModelStrategyJson = RawJson(agent, "model_strategy", "{\"kind\":\"inherit\"}"),
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
                OptionalString(node, "prompt_version_hash")?.Trim().ToLowerInvariant()));
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
        string? PromptVersionHash);
}
