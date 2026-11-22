using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Companion;
using TinadecCore.Persistence;
using Xunit;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Verifies idempotent startup reconciliation (<see cref="DbContextSchemaBootstrapper.EnsureTablesAsync"/>)
/// adds consolidated run lease columns onto <c>companion_runs</c>, <c>max_bot_hop_depth</c> onto
/// <c>companion_settings</c>, and the new <c>companion_schedules</c> / <c>companion_plans</c> /
/// <c>companion_todos</c> tables onto an existing Companion SQLite database.
/// </summary>
public sealed class CompanionEffectSchemaTests
{
    [Fact]
    public async Task EnsureTables_ReconcilesRunLeaseColumnsSettingsAndPlanningTablesIdempotently()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tinadec-companion-schema-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<CompanionDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;

            await using (var db = new CompanionDbContext(options))
            {
                await db.Database.ExecuteSqlRawAsync("""
                    create table companion_runs (
                        id text primary key, tenant_id text not null, companion_id text not null,
                        conversation_id text not null, trigger_message_id text not null, status text not null,
                        depth integer not null, config_revision integer not null, config_snapshot_json text not null,
                        loaded_memory_ids_json text not null, loaded_skill_binding_ids_json text not null,
                        rounds integer not null, error_code text null, error_message text null,
                        reply_message_id text null, created_at text not null, started_at text null, finished_at text null);
                    create table companion_settings (
                        tenant_id text primary key, main_companion_id text null, recruit_limit integer not null,
                        autonomy_paused integer not null, revision integer not null, updated_at text not null);
                    """);

                await DbContextSchemaBootstrapper.EnsureTablesAsync(db);

                var runColumns = await ColumnsAsync(db, "companion_runs");
                Assert.Contains("channel_kind", runColumns);
                Assert.Contains("thread_key", runColumns);
                Assert.Contains("attempts", runColumns);
                Assert.Contains("available_at", runColumns);
                Assert.Contains("available_at_unix_ms", runColumns);
                Assert.Contains("claim_token", runColumns);
                Assert.Contains("claimed_at", runColumns);
                Assert.Contains("claimed_at_unix_ms", runColumns);
                Assert.Contains("last_error", runColumns);

                var settingsColumns = await ColumnsAsync(db, "companion_settings");
                Assert.Contains("max_bot_hop_depth", settingsColumns);

                Assert.NotEmpty(await ColumnsAsync(db, "companion_schedules"));
                Assert.NotEmpty(await ColumnsAsync(db, "companion_plans"));
                Assert.NotEmpty(await ColumnsAsync(db, "companion_todos"));
            }

            await using (var db = new CompanionDbContext(options))
            {
                var runId = Guid.NewGuid();
                var now = DateTimeOffset.UtcNow;
                db.Runs.Add(new CompanionRunRecord
                {
                    Id = runId,
                    TenantId = Guid.NewGuid(),
                    CompanionId = Guid.NewGuid(),
                    ConversationId = Guid.NewGuid(),
                    TriggerMessageId = Guid.NewGuid(),
                    Status = "queued",
                    ChannelKind = "desktop_chat",
                    AvailableAt = now,
                    AvailableAtUnixMs = now.ToUnixTimeMilliseconds(),
                    CreatedAt = now
                });
                await db.SaveChangesAsync();
                Assert.Equal("queued", (await db.Runs.SingleAsync(x => x.Id == runId)).Status);
            }

            await using (var db = new CompanionDbContext(options))
                await DbContextSchemaBootstrapper.EnsureTablesAsync(db);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static async Task<string[]> ColumnsAsync(CompanionDbContext db, string table) =>
        (await db.Database.SqlQueryRaw<string>($"SELECT name AS \"Value\" FROM pragma_table_info('{table}')").ToListAsync()).ToArray();
}
