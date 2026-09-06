using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.Sqlite;

/// <summary>
/// Event idempotency (plan §3.3 item 6): terminal run events (run.failed,
/// task.cancelled, run.recovered, ...) persist exactly once per idempotency
/// key, mirroring the run stream's key semantics. SQLite treats NULLs as
/// distinct in unique indexes, so unkeyed events are unaffected.
/// </summary>
[DbContext(typeof(LifecycleDbContext))]
[Migration("202609060001_EventIdempotencyKey")]
public sealed class EventIdempotencyKey : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table event_index add column idempotency_key text null;
        create unique index if not exists ix_event_index_run_idempotency
            on event_index(run_id, idempotency_key);
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("""
        drop index if exists ix_event_index_run_idempotency;
        alter table event_index drop column idempotency_key;
        """);
}
