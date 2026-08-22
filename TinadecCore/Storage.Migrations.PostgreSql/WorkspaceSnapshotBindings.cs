using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608220009_WorkspaceSnapshotBindings")]
public sealed class WorkspaceSnapshotBindings : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table tool_executions add column if not exists workspace_snapshot_id uuid null;
        alter table tool_executions add column if not exists workspace_snapshot_hash varchar(128) null;
        create index if not exists ix_tool_executions_workspace_snapshot_id on tool_executions(workspace_snapshot_id);
        """);

    protected override void Down(MigrationBuilder m) { }
}
