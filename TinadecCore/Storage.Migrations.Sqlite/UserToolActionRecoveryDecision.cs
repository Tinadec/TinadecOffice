using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608220013_UserToolActionRecoveryDecision")]
public sealed class UserToolActionRecoveryDecision : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table user_tool_actions add column recovery_decision text null;
        alter table user_tool_actions add column recovery_reason text null;
        alter table user_tool_actions add column recovered_at text null;
        """);

    protected override void Down(MigrationBuilder m) { }
}
