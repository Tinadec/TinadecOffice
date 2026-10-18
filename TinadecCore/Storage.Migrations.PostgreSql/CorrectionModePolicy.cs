using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(AgentConfigurationDbContext))]
[Migration("202610190001_CorrectionModePolicy")]
public sealed class CorrectionModePolicy : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        -- The projection schema bootstrapper owns idempotent model-column
        -- reconciliation. This marker migration records that the policy surface
        -- was introduced without racing a second column pass.
        select 1;
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("""
        select 1;
        """);
}
