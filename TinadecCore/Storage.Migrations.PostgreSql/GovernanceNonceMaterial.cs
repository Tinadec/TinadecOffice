using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Governance;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(GovernanceDbContext))]
[Migration("202608220008_GovernanceNonceMaterial")]
public sealed class GovernanceNonceMaterial : Migration
{
    protected override void Up(MigrationBuilder m) { }
    protected override void Down(MigrationBuilder m) { }
}
