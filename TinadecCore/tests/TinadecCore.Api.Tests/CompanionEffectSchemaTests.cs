using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Companion;
using TinadecCore.Persistence;
using Xunit;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The companion effect queue is bootstrap-owned like the rest of this context (no migration
/// assembly), so a database created before it existed only gains the table if startup reconciliation
/// adds it. Both halves matter: the table has to appear, and EF has to be able to write through it.
/// </summary>
public sealed class CompanionEffectSchemaTests
{
    [Fact]
    public async Task EnsureTables_AddsTheEffectQueueToAnOlderCompanionDatabase()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tinadec-companion-effects-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<CompanionDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;

            await using (var db = new CompanionDbContext(options))
            {
                // A database from before the queue: runs exist, effects do not.
                await db.Database.ExecuteSqlRawAsync("""
                    create table companion_runs (
                        id text primary key, tenant_id text not null, companion_id text not null,
                        conversation_id text not null, trigger_message_id text not null, status text not null,
                        depth integer not null, config_revision integer not null, config_snapshot_json text not null,
                        loaded_memory_ids_json text not null, loaded_skill_binding_ids_json text not null,
                        rounds integer not null, error_code text null, error_message text null,
                        reply_message_id text null, created_at text not null, started_at text null, finished_at text null);
                    """);

                await DbContextSchemaBootstrapper.EnsureTablesAsync(db);

                var columns = await ColumnsAsync(db, "companion_effects");
                Assert.Contains("status", columns);
                Assert.Contains("kind", columns);
                Assert.Contains("available_at", columns);
                Assert.Contains("available_at_unix_ms", columns);
                Assert.Contains("attempts", columns);
                Assert.Contains("claim_token", columns);
                Assert.Contains("claimed_at", columns);
                Assert.Contains("claimed_at_unix_ms", columns);
                Assert.Contains("last_error", columns);
                Assert.Contains("payload_json", columns);
            }

            await using (var db = new CompanionDbContext(options))
            {
                var runId = Guid.NewGuid();
                var now = DateTimeOffset.UtcNow;
                db.Effects.Add(new CompanionEffectRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = Guid.NewGuid(),
                    CompanionId = Guid.NewGuid(),
                    ConversationId = Guid.NewGuid(),
                    RunId = runId,
                    Kind = "run_reply",
                    PayloadJson = "{}",
                    Status = "pending",
                    AvailableAt = now,
                    AvailableAtUnixMs = now.ToUnixTimeMilliseconds(),
                    CreatedAt = now,
                    UpdatedAt = now
                });
                await db.SaveChangesAsync();
                Assert.Equal("pending", (await db.Effects.SingleAsync(x => x.RunId == runId)).Status);
            }

            // Idempotent on the reconciled database: a second pass adds and fails nothing.
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
