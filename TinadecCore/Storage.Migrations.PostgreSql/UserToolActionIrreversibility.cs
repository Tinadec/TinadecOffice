using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608220012_UserToolActionIrreversibility")]
public sealed class UserToolActionIrreversibility : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table user_tool_actions add column if not exists non_reversible boolean not null default false;
        alter table user_tool_actions add column if not exists compensation_guidance varchar(4096) null;
        """);

    protected override void Down(MigrationBuilder m) { }
}
