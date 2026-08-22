using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608220004_ActionApprovalNonce")]
public sealed class ActionApprovalNonce : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("alter table approval_requests add column nonce_hash text null;");
    protected override void Down(MigrationBuilder m) { }
}
