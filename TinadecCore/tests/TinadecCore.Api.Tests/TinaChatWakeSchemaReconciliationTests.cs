using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Persistence;
using TinadecCore.TinaChat;
using Xunit;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The TinaChat wake queue and the outcome-announcement columns are bootstrap-owned like the rest
/// of this context (no migration assembly). A database created before this batch must gain the
/// table and both columns at startup, and EF must write through the reconciled shape — the exact
/// failure mode behind the historical "no column named nonce_secret_reference" approvals outage.
/// </summary>
public sealed class TinaChatWakeSchemaReconciliationTests
{
    [Fact]
    public async Task EnsureTables_CreatesTheWakeQueueAndReconcilesExecutionOutcomeColumns()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tinadec-tinachat-wakes-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<TinaChatDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;

            await using (var db = new TinaChatDbContext(options))
            {
                // A database from before the wake queue: the execution table exists in its old,
                // narrower shape and no wake table exists at all.
                await db.Database.ExecuteSqlRawAsync("""
                    create table tina_chat_executions (
                        id text primary key, tenant_id text not null, workspace_id text not null,
                        conversation_id text not null, intent_id text not null, participant_id text not null,
                        session_id text not null, mode_version_id text not null, project_id text null,
                        run_id text null, revision integer not null);
                    """);

                await DbContextSchemaBootstrapper.EnsureTablesAsync(db);

                Assert.Contains("status", await ColumnsAsync(db, "tina_chat_wakes"));
                Assert.Contains("available_at", await ColumnsAsync(db, "tina_chat_wakes"));
                Assert.Contains("source_message_ids_json", await ColumnsAsync(db, "tina_chat_wakes"));
                Assert.Contains("result_message_id", await ColumnsAsync(db, "tina_chat_executions"));
                Assert.Contains("participant_id", await ColumnsAsync(db, "tina_chat_session_identities"));
                Assert.Contains("result_run_status", await ColumnsAsync(db, "tina_chat_executions"));
            }

            // The failure mode under guard: writing through the new members must succeed on the
            // reconciled database, not only read back its schema.
            await using (var db = new TinaChatDbContext(options))
            {
                var execution = new ChatExecution
                {
                    Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(),
                    ConversationId = Guid.NewGuid(), IntentId = Guid.NewGuid(), ParticipantId = Guid.NewGuid(),
                    SessionId = Guid.NewGuid(), ModeVersionId = Guid.NewGuid(), RunId = Guid.NewGuid(),
                    ResultRunStatus = "completed"
                };
                db.Executions.Add(execution);
                db.Wakes.Add(new ChatWake
                {
                    TenantId = execution.TenantId, WorkspaceId = execution.WorkspaceId,
                    ConversationId = execution.ConversationId, ParticipantId = execution.ParticipantId,
                    Reason = "message", SourceMessageIdsJson = "[]", Status = "pending",
                    AvailableAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync();

                Assert.Equal("completed", (await db.Executions.SingleAsync(x => x.Id == execution.Id)).ResultRunStatus);
                Assert.Equal(1, await db.Wakes.CountAsync());
            }

            // Idempotent on a freshly created database: a second pass adds nothing and fails nothing.
            await using (var db = new TinaChatDbContext(options))
                await DbContextSchemaBootstrapper.EnsureTablesAsync(db);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static async Task<string[]> ColumnsAsync(TinaChatDbContext db, string table) =>
        (await db.Database.SqlQueryRaw<string>($"SELECT name AS \"Value\" FROM pragma_table_info('{table}')").ToListAsync()).ToArray();
}
