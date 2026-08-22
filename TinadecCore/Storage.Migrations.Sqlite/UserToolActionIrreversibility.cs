using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608220012_UserToolActionIrreversibility")]
public sealed class UserToolActionIrreversibility : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table user_tool_actions add column non_reversible integer not null default 0;
        alter table user_tool_actions add column compensation_guidance text null;
        """);

    protected override void Down(MigrationBuilder m) { }
}
