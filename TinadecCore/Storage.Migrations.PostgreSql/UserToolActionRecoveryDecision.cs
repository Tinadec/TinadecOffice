using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608220013_UserToolActionRecoveryDecision")]
public sealed class UserToolActionRecoveryDecision : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table user_tool_actions add column if not exists recovery_decision varchar(32) null;
        alter table user_tool_actions add column if not exists recovery_reason varchar(4096) null;
        alter table user_tool_actions add column if not exists recovered_at timestamptz null;
        """);

    protected override void Down(MigrationBuilder m) { }
}
