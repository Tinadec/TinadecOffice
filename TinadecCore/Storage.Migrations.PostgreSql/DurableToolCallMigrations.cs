using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608180006_DurableToolCallKeys")]
public sealed class DurableToolCallKeys : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table tool_executions add column if not exists tool_call_key varchar(512) null;
        update tool_executions
        set tool_call_key = 'legacy:' || id::text
        where tool_call_key is null or btrim(tool_call_key) = '';
        alter table tool_executions alter column tool_call_key set not null;
        create unique index if not exists ix_tool_executions_run_id_tool_call_key
        on tool_executions(run_id, tool_call_key);
        """);

    protected override void Down(MigrationBuilder m) { }
}
