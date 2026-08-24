using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Governance;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(GovernanceDbContext))]
[Migration("202608220008_GovernanceNonceMaterial")]
public sealed class GovernanceNonceMaterial : Migration
{
    // Governance tables are created by the model bootstrap because the v1
    // database had no dedicated governance migration history. The current
    // model includes the nonce reference in the initial CREATE TABLE shape.
    protected override void Up(MigrationBuilder m) { }
    protected override void Down(MigrationBuilder m) { }
}
