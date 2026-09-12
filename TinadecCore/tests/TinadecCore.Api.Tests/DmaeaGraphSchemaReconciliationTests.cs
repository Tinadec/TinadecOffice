using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.AgentConfiguration;
using TinadecCore.Memory;
using TinadecCore.Persistence;
using Xunit;

namespace TinadecCore.Api.Tests;

/// <summary>
/// DmaEA graph orchestration tables (agent_templates(_versions), mode_bindings,
/// tool_definitions(_versions)) are bootstrap-owned like the rest of this context:
/// the schema bootstrapper must create/reconcile them on databases created before
/// the 202609120001 migration shipped, and EF inserts through the new records must
/// succeed on the reconciled shape (the GovernanceNonceMaterial empty-migration
/// failure mode — a table the model maps that bootstrap has not created yet).
/// </summary>
public sealed class DmaeaGraphSchemaReconciliationTests
{
    [Fact]
    public async Task EnsureTables_CreatesDmaeaGraphTables_AndRecordInsertsSucceed()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tinadec-dmaea-schema-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AgentConfigurationDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;

            await using (var db = new AgentConfigurationDbContext(options))
            {
                // A database from before the DmaEA graph migration: none of the new
                // tables exist, the legacy agent tables do.
                await db.Database.ExecuteSqlRawAsync("""
                    create table agent_definitions (
                        id text primary key, tenant_id text not null, workspace_id text not null,
                        slug text not null, display_name text not null, layer text not null, role text not null,
                        capabilities_json text null, base_prompt_pipeline_id text null, model_strategy_json text null,
                        tool_scope_json text null, system_prompt text null, description text null,
                        source_kind text not null, source_key text not null, managed integer not null,
                        enabled integer not null, status text not null, revision integer not null,
                        version integer not null, created_at text not null, updated_at text not null,
                        archived_at text null, created_by_principal_id text not null, updated_by_principal_id text not null);
                    """);

                await DbContextSchemaBootstrapper.EnsureTablesAsync(db);

                foreach (var table in new[] { "agent_templates", "agent_template_versions", "mode_bindings", "tool_definitions", "tool_definition_versions" })
                {
                    var columns = await db.Database
                        .SqlQueryRaw<string>($"SELECT name AS \"Value\" FROM pragma_table_info('{table}')")
                        .ToListAsync();
                    Assert.NotEmpty(columns);
                    Assert.Contains("revision", columns);
                }
            }

            // The failure mode under guard: inserting through the new records must
            // succeed on the reconciled database.
            var tenantId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await using (var db = new AgentConfigurationDbContext(options))
            {
                var template = new AgentTemplateRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenantId, WorkspaceId = workspaceId,
                    Slug = "meeting", DisplayName = "Meeting", Layer = "operation", Role = "orchestrator",
                    CapabilitiesJson = """["user.respond","task.dispatch"]""",
                    Status = "published", Revision = 1, Version = 1,
                    CreatedAt = now, UpdatedAt = now
                };
                db.AgentTemplates.Add(template);
                db.AgentTemplateVersions.Add(new AgentTemplateVersionRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenantId, WorkspaceId = workspaceId,
                    AgentTemplateId = template.Id, Version = 1,
                    SnapshotJson = """{"slug":"meeting"}""", Status = "published", Revision = 1, CreatedAt = now
                });
                db.ModeBindings.Add(new ModeBindingRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenantId, WorkspaceId = workspaceId,
                    ModeId = Guid.NewGuid(), NodeKey = "meeting", AgentTemplateId = template.Id,
                    AgentTemplateSlug = "meeting", EnvelopeJson = """{"spawn":{"max_depth":1}}""",
                    IncludesCoreReserved = true, Status = "published", Revision = 1, CreatedAt = now, UpdatedAt = now
                });
                var toolDefinition = new ToolDefinitionRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenantId, WorkspaceId = workspaceId,
                    Slug = "read_file", DisplayName = "Read File",
                    Status = "published", Revision = 1, Version = 1,
                    CreatedAt = now, UpdatedAt = now
                };
                db.ToolDefinitions.Add(toolDefinition);
                db.ToolDefinitionVersions.Add(new ToolDefinitionVersionRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenantId, WorkspaceId = workspaceId,
                    ToolDefinitionId = toolDefinition.Id, Version = 1,
                    DefinitionJson = JsonSerializer.Serialize(new { schema = "string", name_only = true }),
                    Status = "published", Revision = 1, CreatedAt = now
                });
                await db.SaveChangesAsync();

                Assert.Equal(1, await db.AgentTemplates.CountAsync());
                Assert.Equal(1, await db.ModeBindings.CountAsync());
                Assert.Equal(1, await db.ToolDefinitionVersions.CountAsync());
            }
        }
        finally
        {
            try { File.Delete(databasePath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task EnsureTables_ReconcilesSessionIdentityColumns_AndSessionInsertSucceeds()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tinadec-session-identity-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<MemoryDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;

            await using (var db = new MemoryDbContext(options))
            {
                // Pre-identity sessions table: no conversation columns.
                await db.Database.ExecuteSqlRawAsync("""
                    create table sessions (
                        id text primary key, tenant_id text not null, workspace_id text not null,
                        project_id text null, title text not null, status text not null, mode text not null,
                        summary text null, history_revision integer not null, mode_version_id text null,
                        meeting_model_override_provider_instance_id text null, meeting_model_override_model text null,
                        created_at text not null, updated_at text not null, lifecycle_status text not null, trashed_at text null);
                    """);

                await DbContextSchemaBootstrapper.EnsureTablesAsync(db);

                var columns = await db.Database
                    .SqlQueryRaw<string>("SELECT name AS \"Value\" FROM pragma_table_info('sessions')")
                    .ToListAsync();
                Assert.Contains("conversation_node_key", columns);
                Assert.Contains("conversation_template_slug", columns);
            }

            // Inserting a session with the frozen identity must succeed.
            await using (var db = new MemoryDbContext(options))
            {
                var now = DateTimeOffset.UtcNow;
                db.Sessions.Add(new SessionRecord
                {
                    Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(),
                    Title = "t", ModeVersionId = Guid.NewGuid(),
                    ConversationNodeKey = "meeting", ConversationTemplateSlug = "meeting",
                    CreatedAt = now, UpdatedAt = now
                });
                await db.SaveChangesAsync();
                Assert.Equal(1, await db.Sessions.CountAsync());
            }
        }
        finally
        {
            try { File.Delete(databasePath); } catch (IOException) { }
        }
    }
}
