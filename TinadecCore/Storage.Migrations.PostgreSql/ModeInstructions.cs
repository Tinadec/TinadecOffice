using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Storage.Migrations.PostgreSql;

/// <summary>
/// Adds the team-level collaboration instruction to a mode. Nullable so every
/// existing row (and every pack mode, which never declares one) keeps its exact
/// serialized shape.
/// </summary>
[DbContext(typeof(AgentConfigurationDbContext))]
[Migration("202611040001_ModeInstructions")]
public sealed class ModeInstructions : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table agent_modes add column if not exists instructions text null;
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("""
        alter table agent_modes drop column if exists instructions;
        """);
}
