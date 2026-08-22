using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608220004_ActionApprovalNonce")]
public sealed class ActionApprovalNonce : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("alter table approval_requests add column if not exists nonce_hash varchar(128) null;");
    protected override void Down(MigrationBuilder m) { }
}
