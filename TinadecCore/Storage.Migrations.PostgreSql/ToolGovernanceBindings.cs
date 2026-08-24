using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608220001_ToolGovernanceBindings")]
public sealed class ToolGovernanceBindings : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table tool_executions add column if not exists permission_request_id uuid null;
        alter table tool_executions add column if not exists authorization_decision_id uuid null;
        alter table tool_executions add column if not exists capability_lease_id uuid null;
        create index if not exists ix_tool_executions_permission_request_id on tool_executions(permission_request_id);
        create index if not exists ix_tool_executions_capability_lease_id on tool_executions(capability_lease_id);
        """);

    protected override void Down(MigrationBuilder m) { }
}
