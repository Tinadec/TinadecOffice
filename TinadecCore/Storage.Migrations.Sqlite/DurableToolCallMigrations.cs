using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608180006_DurableToolCallKeys")]
public sealed class DurableToolCallKeys : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        -- Existing historical executions predate durable function-call identity.
        -- Give each a stable legacy key before enforcing run-local uniqueness so
        -- migration neither discards audit rows nor merges unrelated attempts.
        alter table tool_executions add column tool_call_key text null;
        update tool_executions
        set tool_call_key = 'legacy:' || id
        where tool_call_key is null or trim(tool_call_key) = '';
        create unique index if not exists ix_tool_executions_run_id_tool_call_key
        on tool_executions(run_id, tool_call_key);
        """);

    protected override void Down(MigrationBuilder m) { }
}
