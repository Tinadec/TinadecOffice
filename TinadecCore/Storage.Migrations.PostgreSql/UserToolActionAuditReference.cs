using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608220010_UserToolActionAuditReference")]
public sealed class UserToolActionAuditReference : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("alter table user_tool_actions add column if not exists audit_reference varchar(256) not null default ''; update user_tool_actions set audit_reference = 'user-tool-action:' || replace(id::text, '-', '') where audit_reference = '';");

    protected override void Down(MigrationBuilder m) { }
}
