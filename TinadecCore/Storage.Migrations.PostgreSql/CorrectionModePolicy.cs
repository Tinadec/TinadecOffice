using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(AgentConfigurationDbContext))]
[Migration("202610190001_CorrectionModePolicy")]
public sealed class CorrectionModePolicy : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table agent_modes add column if not exists runtime_policy_json text null;
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("""
        alter table agent_modes drop column if exists runtime_policy_json;
        """);
}
