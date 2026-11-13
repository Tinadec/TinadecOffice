using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Data;
using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

public sealed class ToolSettingsStore(IDbContextFactory<ToolsSettingsDbContext> factory, ITenantContextAccessor tenant, IServiceProvider services) : IToolSettingsStore
{
    public JsonElement GetSchema() => ToolSettingsSchema.Schema();
    public async Task<ToolSettingsDocumentDto> GetAsync(Guid? agentDefinitionId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var actor = tenant.Current;
        var rows = await db.Settings.AsNoTracking().Where(x => x.TenantId == actor.TenantId && x.WorkspaceId == actor.WorkspaceId).ToListAsync(cancellationToken);
        return Document(rows, agentDefinitionId);
    }
    public async Task<ToolSettingsDocumentDto> SaveAsync(Guid? agentDefinitionId, JsonElement settings, long expectedRevision, CancellationToken cancellationToken = default, Guid? projectId = null)
    {
        var validated = ToolSettingsSchema.Validate(settings);
        if (validated.TryGetProperty("mcp", out var mcp) && mcp.TryGetProperty("server_resource_ids", out var servers) && servers.ValueKind == JsonValueKind.Array)
        {
            var registry = services.GetRequiredService<IMcpResourceRegistry>();
            foreach (var id in servers.EnumerateArray().Select(x => x.GetGuid()))
            {
                var resource = await registry.GetAsync(id, cancellationToken);
                if (resource is null || resource.ProjectId is not null && resource.ProjectId != projectId) throw new ToolSettingsException("mcp_binding_not_found", "An MCP binding is invisible, stale or outside the selected project.");
            }
        }
        if (validated.TryGetProperty("skills", out var skills) && skills.TryGetProperty("resource_ids", out var resourceIds) && resourceIds.ValueKind == JsonValueKind.Array)
        {
            var ids = resourceIds.EnumerateArray().Select(x => x.GetGuid()).ToArray();
            var resources = services.GetService<IToolSkillResourceService>();
            var catalog = resources is null ? [] : (await resources.ListAsync(projectId, cancellationToken)).Skills;
            if (ids.Any(id => !catalog.Any(resource => resource.ResourceId == id && resource.Valid))) throw new ToolSettingsException("skill_binding_not_found", "A Skill binding is invisible, stale, invalid or outside the selected project.");
        }
        var actor = tenant.Current;
        // The revision-conflict mapping stays outside the retry: a real concurrent edit must
        // surface as 412, not be retried until it accidentally succeeds.
        try
        {
            return await Persistence.SqliteTransientRetry.WriteInTransactionAsync(async ct =>
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var rows = await db.Settings.Where(x => x.TenantId == actor.TenantId && x.WorkspaceId == actor.WorkspaceId).ToListAsync(ct);
                var scope = Scope(agentDefinitionId);
                var row = rows.SingleOrDefault(x => x.ScopeKey == scope);
                if ((row?.Revision ?? 0) != expectedRevision) throw new ToolSettingsException("revision_conflict", "Tool settings changed since they were read.", 412);
                var shared = rows.SingleOrDefault(x => x.ScopeKey == "shared");
                var sharedEffective = ToolSettingsSchema.Merge(ToolSettingsSchema.Defaults, shared is null ? Empty : Parse(shared.SettingsJson));
                if (agentDefinitionId is null)
                {
                    var changedDefaults = ToolSettingsSchema.Merge(ToolSettingsSchema.Defaults, validated);
                    foreach (var other in rows.Where(x => x.ScopeKey != "shared"))
                        try { ToolSettingsSchema.MergeAgent(changedDefaults, Parse(other.SettingsJson)); }
                        catch (ToolSettingsException ex) { throw new ToolSettingsException(ex.Code, $"Shared defaults conflict with {other.ScopeKey}: {ex.Message}", ex.StatusCode); }
                }
                else ToolSettingsSchema.MergeAgent(sharedEffective, validated);
                if (row is null) { row = new() { Id = Guid.NewGuid(), TenantId = actor.TenantId, WorkspaceId = actor.WorkspaceId, ScopeKey = scope }; db.Settings.Add(row); rows.Add(row); }
                row.SettingsJson = validated.GetRawText(); row.Revision++; row.UpdatedAt = DateTimeOffset.UtcNow; row.UpdatedByPrincipalId = actor.PrincipalId;
                db.SettingVersions.Add(new() { Id = Guid.NewGuid(), SettingsId = row.Id, Revision = row.Revision, SettingsJson = row.SettingsJson, CreatedAt = row.UpdatedAt });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Document(rows, agentDefinitionId);
            }, cancellationToken);
        }
        catch (DbUpdateException) { throw new ToolSettingsException("revision_conflict", "Tool settings changed concurrently. Reload before saving.", 412); }
    }
    // A reset is itself a revision, preserving monotonic preconditions and audit history.
    public Task<ToolSettingsDocumentDto> ResetAsync(Guid? agentDefinitionId, long expectedRevision, CancellationToken cancellationToken = default) => SaveAsync(agentDefinitionId, Empty, expectedRevision, cancellationToken);
    internal static JsonElement Empty => JsonDocument.Parse("{}").RootElement.Clone();
    internal static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static string Scope(Guid? id) => id is null ? "shared" : "agent:" + id.Value.ToString("N");
    private static ToolSettingsDocumentDto Document(IReadOnlyList<ToolSettingsRecord> rows, Guid? id)
    {
        var row = rows.SingleOrDefault(x => x.ScopeKey == Scope(id));
        var shared = rows.SingleOrDefault(x => x.ScopeKey == "shared");
        var effective = ToolSettingsSchema.Merge(ToolSettingsSchema.Defaults, shared is null ? Empty : Parse(shared.SettingsJson));
        if (id is not null && row is not null) effective = ToolSettingsSchema.MergeAgent(effective, Parse(row.SettingsJson));
        return new() { Revision = row?.Revision ?? 0, AgentDefinitionId = id, Settings = row is null ? Empty : Parse(row.SettingsJson), EffectiveSettings = effective, SettingsHash = ToolSettingsSchema.Hash(effective), UpdatedAt = row?.UpdatedAt };
    }
}
