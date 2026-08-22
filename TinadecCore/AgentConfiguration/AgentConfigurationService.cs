using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.AgentConfiguration;

/// <summary>Durable draft/version boundary for the agent center.</summary>
public interface IAgentConfigurationService
{
    Task<object> CreateDraftAsync(string kind, object payload, CancellationToken cancellationToken = default);
    Task<object> PublishAsync(Guid entityId, long ifMatchRevision, CancellationToken cancellationToken = default);
}

public sealed class AgentConfigurationService : IAgentConfigurationService
{
    private readonly IDbContextFactory<AgentConfigurationDbContext> _dbFactory;
    private readonly ITenantContextAccessor _tenant;

    public AgentConfigurationService(IDbContextFactory<AgentConfigurationDbContext> dbFactory, ITenantContextAccessor tenant, IContentStore content)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _ = content;
    }

    public async Task<object> CreateDraftAsync(string kind, object payload, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeKind(kind);
        var input = JsonSerializer.SerializeToElement(payload);
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Draft payload must be a JSON object.", nameof(payload));
        var scope = _tenant.Current;
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return normalized switch
        {
            "agent" => await CreateAgentAsync(db, input, scope, now, cancellationToken).ConfigureAwait(false),
            "mode" => await CreateModeAsync(db, input, scope, now, cancellationToken).ConfigureAwait(false),
            "prompt_pipeline" => await CreatePipelineAsync(db, input, scope, now, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException("kind must be agent, mode, or prompt_pipeline.", nameof(kind))
        };
    }

    public async Task<object> PublishAsync(Guid entityId, long ifMatchRevision, CancellationToken cancellationToken = default)
    {
        if (entityId == Guid.Empty) throw new ArgumentException("Entity id is required.", nameof(entityId));
        var scope = _tenant.Current;
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var agent = await db.AgentDefinitions.SingleOrDefaultAsync(x => x.Id == entityId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (agent is not null)
        {
            CheckDraft(agent.Status, agent.Revision, ifMatchRevision);
            ValidateLayer(agent.Layer);
            if (string.IsNullOrWhiteSpace(agent.DisplayName)) throw new InvalidDataException("Agent display name is required.");
            var version = (await db.AgentVersions.Where(x => x.AgentDefinitionId == agent.Id).MaxAsync(x => (int?)x.Version, cancellationToken).ConfigureAwait(false) ?? 0) + 1;
            var snapshot = JsonSerializer.Serialize(new { id = agent.Id, slug = agent.Slug, display_name = agent.DisplayName, layer = agent.Layer, role = agent.Role, capabilities = ParseJson(agent.CapabilitiesJson), model_strategy = ParseJson(agent.ModelStrategyJson), tool_scope = ParseJson(agent.ToolScopeJson) });
            var hash = Hash(snapshot);
            db.AgentVersions.Add(new AgentVersionRecord { Id = Guid.NewGuid(), TenantId = agent.TenantId, WorkspaceId = agent.WorkspaceId, AgentDefinitionId = agent.Id, Version = version, Layer = agent.Layer, Role = agent.Role, SnapshotJson = snapshot, ContentHash = hash, ContentLength = Encoding.UTF8.GetByteCount(snapshot), Status = "published", Revision = 1, CreatedAt = now, CreatedByPrincipalId = scope.PrincipalId });
            agent.Version = version; agent.Status = "published"; agent.Revision++; agent.UpdatedAt = now; agent.UpdatedByPrincipalId = scope.PrincipalId;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new { id = agent.Id, kind = "agent", version, revision = agent.Revision, content_hash = hash };
        }

        var mode = await db.AgentModes.SingleOrDefaultAsync(x => x.Id == entityId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (mode is not null)
        {
            CheckDraft(mode.Status, mode.Revision, ifMatchRevision);
            var nodes = await db.ModeNodes.AsNoTracking().Where(x => x.ModeId == mode.Id && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status == "draft").ToListAsync(cancellationToken).ConfigureAwait(false);
            if (!nodes.Any(x => x.Layer == "operation" && (string.Equals(x.NodeKey, "meeting", StringComparison.OrdinalIgnoreCase) || string.Equals(x.Label, "meeting", StringComparison.OrdinalIgnoreCase)))) throw new InvalidDataException("Mode must contain an operation-layer meeting node.");
            if (!nodes.Any(x => x.Layer == "execution")) throw new InvalidDataException("Mode must contain at least one execution-layer node.");
            var edges = await db.ModeEdges.AsNoTracking().Where(x => x.ModeId == mode.Id && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status == "draft").ToListAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = JsonSerializer.Serialize(new { mode = new { id = mode.Id, slug = mode.Slug, display_name = mode.DisplayName }, nodes, edges });
            var hash = Hash(snapshot); var version = (await db.ModeVersions.Where(x => x.AgentModeId == mode.Id).MaxAsync(x => (int?)x.Version, cancellationToken).ConfigureAwait(false) ?? 0) + 1;
            db.ModeVersions.Add(new ModeVersionRecord { Id = Guid.NewGuid(), TenantId = mode.TenantId, WorkspaceId = mode.WorkspaceId, AgentModeId = mode.Id, Version = version, SnapshotJson = snapshot, TopologyHash = hash, Status = "published", Revision = 1, CreatedAt = now, CreatedByPrincipalId = scope.PrincipalId });
            mode.Version = version; mode.Status = "published"; mode.Revision++; mode.UpdatedAt = now; mode.UpdatedByPrincipalId = scope.PrincipalId;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new { id = mode.Id, kind = "mode", version, revision = mode.Revision, topology_hash = hash };
        }

        var pipeline = await db.PromptPipelines.SingleOrDefaultAsync(x => x.Id == entityId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (pipeline is null) throw new KeyNotFoundException("Configuration entity was not found.");
        CheckDraft(pipeline.Status, pipeline.Revision, ifMatchRevision);
        var pipelineHash = Hash(pipeline.GraphJson); var pipelineVersion = (await db.PromptVersions.Where(x => x.PromptPipelineId == pipeline.Id).MaxAsync(x => (int?)x.Version, cancellationToken).ConfigureAwait(false) ?? 0) + 1;
        db.PromptVersions.Add(new PromptVersionRecord { Id = Guid.NewGuid(), TenantId = pipeline.TenantId, WorkspaceId = pipeline.WorkspaceId, PromptPipelineId = pipeline.Id, Version = pipelineVersion, GraphJson = pipeline.GraphJson, ContentHash = pipelineHash, ContentLength = Encoding.UTF8.GetByteCount(pipeline.GraphJson), Status = "published", Revision = 1, CreatedAt = now, CreatedByPrincipalId = scope.PrincipalId });
        pipeline.Version = pipelineVersion; pipeline.Status = "published"; pipeline.Revision++; pipeline.UpdatedAt = now; pipeline.UpdatedByPrincipalId = scope.PrincipalId;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new { id = pipeline.Id, kind = "prompt_pipeline", version = pipelineVersion, revision = pipeline.Revision, content_hash = pipelineHash };
    }

    private static async Task<object> CreateAgentAsync(AgentConfigurationDbContext db, JsonElement input, TenantContext scope, DateTimeOffset now, CancellationToken ct)
    {
        var name = String(input, "display_name") ?? String(input, "name") ?? throw new ArgumentException("display_name is required.");
        var layer = (String(input, "layer") ?? "operation").Trim().ToLowerInvariant(); ValidateLayer(layer);
        var row = new AgentDefinitionRecord { Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, Slug = Slug(String(input, "slug") ?? name), DisplayName = name.Trim(), Layer = layer, Role = String(input, "role") ?? "", CapabilitiesJson = Raw(input, "capabilities") ?? "[]", ModelStrategyJson = Raw(input, "model_strategy"), ToolScopeJson = Raw(input, "tool_scope") ?? Raw(input, "allowed_tools") ?? "[]", Status = "draft", Revision = 1, CreatedAt = now, UpdatedAt = now, CreatedByPrincipalId = scope.PrincipalId, UpdatedByPrincipalId = scope.PrincipalId };
        db.AgentDefinitions.Add(row); await db.SaveChangesAsync(ct).ConfigureAwait(false); return new { id = row.Id, kind = "agent", slug = row.Slug, revision = row.Revision, status = row.Status };
    }

    private static async Task<object> CreateModeAsync(AgentConfigurationDbContext db, JsonElement input, TenantContext scope, DateTimeOffset now, CancellationToken ct)
    {
        var name = String(input, "display_name") ?? String(input, "name") ?? throw new ArgumentException("display_name is required.");
        var row = new AgentModeRecord { Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, Slug = Slug(String(input, "slug") ?? name), DisplayName = name.Trim(), Description = String(input, "description"), Status = "draft", Revision = 1, CreatedAt = now, UpdatedAt = now, CreatedByPrincipalId = scope.PrincipalId, UpdatedByPrincipalId = scope.PrincipalId };
        db.AgentModes.Add(row); await db.SaveChangesAsync(ct).ConfigureAwait(false); return new { id = row.Id, kind = "mode", slug = row.Slug, revision = row.Revision, status = row.Status };
    }

    private static async Task<object> CreatePipelineAsync(AgentConfigurationDbContext db, JsonElement input, TenantContext scope, DateTimeOffset now, CancellationToken ct)
    {
        var name = String(input, "display_name") ?? String(input, "name") ?? throw new ArgumentException("display_name is required.");
        var graph = Raw(input, "graph") ?? Raw(input, "graph_json") ?? "{}";
        var row = new PromptPipelineRecord { Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, Slug = Slug(String(input, "slug") ?? name), DisplayName = name.Trim(), Description = String(input, "description"), GraphJson = graph, Status = "draft", Revision = 1, CreatedAt = now, UpdatedAt = now, CreatedByPrincipalId = scope.PrincipalId, UpdatedByPrincipalId = scope.PrincipalId };
        db.PromptPipelines.Add(row); await db.SaveChangesAsync(ct).ConfigureAwait(false); return new { id = row.Id, kind = "prompt_pipeline", slug = row.Slug, revision = row.Revision, status = row.Status };
    }

    public static void ValidateLayer(string? layer)
    {
        var normalized = layer?.Trim().ToLowerInvariant();
        if (normalized is not "operation" and not "execution") throw new ArgumentException("Agent layer must be 'operation' or 'execution'; 'planning' is not allowed.", nameof(layer));
    }

    private static void CheckDraft(string status, long actual, long expected)
    {
        if (status == "archived") throw new InvalidOperationException("Archived configuration cannot be published.");
        if (!string.Equals(status, "draft", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only a draft configuration can be published; edit the draft to create a new version.");
        if (expected >= 0 && actual != expected) throw new DbUpdateConcurrencyException("Configuration revision does not match If-Match.");
    }

    private static string NormalizeKind(string kind) => kind.Trim().ToLowerInvariant() switch { "agent" or "agent_definition" => "agent", "mode" or "agent_mode" => "mode", "prompt" or "prompt_pipeline" => "prompt_pipeline", _ => kind.Trim().ToLowerInvariant() };
    private static string Slug(string value) => string.Join('-', value.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string? String(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static string? Raw(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? p.GetRawText() : null;
    private static JsonElement ParseJson(string? value) => string.IsNullOrWhiteSpace(value) ? JsonSerializer.SerializeToElement((object?)null) : JsonSerializer.Deserialize<JsonElement>(value);
}
