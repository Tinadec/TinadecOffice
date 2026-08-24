using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608220005_ToolLeaseUses")]
public sealed class ToolLeaseUses : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql(
        "alter table tool_executions add column lease_uses integer not null default 1;");

    protected override void Down(MigrationBuilder m) { }
}
