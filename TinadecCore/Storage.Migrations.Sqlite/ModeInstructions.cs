using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Storage.Migrations.Sqlite;

/// <summary>Sqlite counterpart of the PostgreSql ModeInstructions migration.</summary>
[DbContext(typeof(AgentConfigurationDbContext))]
[Migration("202611040001_ModeInstructions")]
public sealed class ModeInstructions : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table agent_modes add column instructions text null;
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("""
        alter table agent_modes drop column instructions;
        """);
}
