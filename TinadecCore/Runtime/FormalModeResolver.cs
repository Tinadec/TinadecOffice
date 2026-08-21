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
        try
        {
            var sess = await _sessions.FindAsync(sessionId, ct).ConfigureAwait(false);
            if (sess?.ModeVersionId is null) return null;
            await using var cfg = await _cfgFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var mv = await cfg.ModeVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sess.ModeVersionId.Value, ct).ConfigureAwait(false);
            if (mv is null) return null;
            var modeId = mv.AgentModeId;
            // use session's tenant/workspace for isolation
            var nodes = await cfg.ModeNodes.AsNoTracking().Where(x => x.ModeId == modeId).ToListAsync(ct).ConfigureAwait(false);
            if (nodes.Count == 0)
            {
                // fallback parse snapshot
                if (!string.IsNullOrWhiteSpace(mv.SnapshotJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(mv.SnapshotJson);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("nodes", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var n in arr.EnumerateArray())
                            {
                                Guid aid = Guid.Empty;
                                if (n.TryGetProperty("AgentDefinitionId", out var a) && Guid.TryParse(a.GetString(), out var g)) aid = g;
                                else if (n.TryGetProperty("agentDefinitionId", out var a2) && Guid.TryParse(a2.GetString(), out var g2)) aid = g2;
                                if (aid != Guid.Empty) nodes.Add(new ModeNodeRecord { AgentDefinitionId = aid, ConfigJson = null, Layer = n.TryGetProperty("Layer", out var l) ? l.GetString() ?? "operation" : "operation" });
                            }
                        }
                    }
                    catch { }
                }
                if (nodes.Count == 0) return null;
            }
            var union = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasWildcard = false;
            foreach (var node in nodes)
            {
                var agent = await cfg.AgentDefinitions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == node.AgentDefinitionId, ct).ConfigureAwait(false);
                if (agent is null) continue;
                var agentTools = ParseTools(agent.ToolScopeJson);
                var modeTools = ParseTools(node.ConfigJson);
                HashSet<string> effective;
                if (agentTools.Contains("*")) effective = modeTools.Count > 0 ? new HashSet<string>(modeTools, StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "*" };
                else if (modeTools.Count == 0) effective = new HashSet<string>(agentTools, StringComparer.OrdinalIgnoreCase);
                else effective = new HashSet<string>(agentTools.Intersect(modeTools, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
                if (effective.Contains("*")) hasWildcard = true;
                else foreach (var t in effective) union.Add(t);
            }
            return hasWildcard ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "*" } : union;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetEffectiveToolsForSession failed for {SessionId}", sessionId);
            return null;
        }
    }

    public async Task<ChatResolution?> TryResolveFormalChatAsync(Guid sessionId, string layer, Guid runId, Guid turnId, CancellationToken ct = default)
    {
        try
        {
            var sess = await _sessions.FindAsync(sessionId, ct).ConfigureAwait(false);
            if (sess?.ModeVersionId is null) return null;
            await using var cfg = await _cfgFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var mv = await cfg.ModeVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sess.ModeVersionId.Value, ct).ConfigureAwait(false);
            if (mv is null) return null;
            var nodes = await cfg.ModeNodes.AsNoTracking().Where(x => x.ModeId == mv.AgentModeId && x.Layer == layer).ToListAsync(ct).ConfigureAwait(false);
            if (nodes.Count == 0) return null;
            var node = nodes.First();
            var agent = await cfg.AgentDefinitions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == node.AgentDefinitionId, ct).ConfigureAwait(false);
            if (agent is null || string.IsNullOrWhiteSpace(agent.ModelStrategyJson)) return null;
            JsonElement strat;
            try { strat = JsonDocument.Parse(agent.ModelStrategyJson).RootElement; } catch { return null; }
            var kind = strat.TryGetProperty("kind", out var k) ? k.GetString()?.Trim().ToLowerInvariant() : strat.TryGetProperty("selection_kind", out var sk) ? sk.GetString()?.Trim().ToLowerInvariant() : "inherit";
            if (kind is not ("inherit" or "fixed" or "parent_select")) kind = "inherit";
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
            var payload = new { layer, kind, resolved_provider = resolvedProvider, resolved_model = resolvedModel ?? resolved?.Model, log, session_id = sessionId, mode_version_id = sess.ModeVersionId, run_id = runId, turn_id = turnId };
            try { await _lifecycle.AppendEventAsync(runId, "model_selection", payload, $"Model selection {kind} for {layer}", cancellationToken: ct).ConfigureAwait(false); } catch { }
            try { await _lifecycle.AppendRunStreamAsync(runId.ToString(), new DurableRunStreamAppend(turnId, "model_selection", null, IdempotencyKey: $"run:{runId}:model_sel:{layer}:{turnId}"), ct).ConfigureAwait(false); } catch { }
            try
            {
                await using var ldb = await _lifecycleFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                ldb.ControlEventIndex.Add(new ControlEventIndexRecord { Id = Guid.NewGuid(), TenantId = sess.TenantId, WorkspaceId = sess.WorkspaceId, AggregateType = "run", AggregateId = runId, EventType = "model_selection", RelativeFilePath = $"runs/{runId}/model_selection.json", PayloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions)))).ToLowerInvariant(), Timestamp = DateTimeOffset.UtcNow });
                await ldb.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "control_event_index write failed for model_selection"); }
            if (kind == "fixed" || kind == "parent_select") return resolved;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TryResolveFormalChat failed for {SessionId} {Layer}", sessionId, layer);
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
}
