using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Storage.Migrations.PostgreSql;

/// <summary>
/// Adds the mode-topology extension fields: team-level collaboration
/// <c>instructions</c> and the mode-level <c>bindings_json</c> array on
/// <c>agent_modes</c>, plus the per-node <c>relationship_json</c> contract on
/// <c>mode_nodes</c>. All nullable so every existing row (and every pack mode,
/// which never declares them) keeps its exact serialized shape.
/// </summary>
[DbContext(typeof(AgentConfigurationDbContext))]
[Migration("202611040001_ModeInstructions")]
public sealed class ModeInstructions : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.Sql("""
            alter table agent_modes add column if not exists instructions text null;
            """);
        m.Sql("""
            alter table agent_modes add column if not exists bindings_json text null;
            """);
        m.Sql("""
            alter table mode_nodes add column if not exists relationship_json text null;
            """);
    }

    protected override void Down(MigrationBuilder m)
    {
        m.Sql("""
            alter table agent_modes drop column if exists instructions;
            """);
        m.Sql("""
            alter table agent_modes drop column if exists bindings_json;
            """);
        m.Sql("""
            alter table mode_nodes drop column if exists relationship_json;
            """);
    }
}
