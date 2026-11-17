using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Storage.Migrations.Sqlite;

/// <summary>Sqlite counterpart of the PostgreSql ModeInstructions migration.</summary>
[DbContext(typeof(AgentConfigurationDbContext))]
[Migration("202611040001_ModeInstructions")]
public sealed class ModeInstructions : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.Sql("""
            alter table agent_modes add column instructions text null;
            """);
        m.Sql("""
            alter table agent_modes add column bindings_json text null;
            """);
        m.Sql("""
            alter table mode_nodes add column relationship_json text null;
            """);
    }

    protected override void Down(MigrationBuilder m)
    {
        m.Sql("""
            alter table agent_modes drop column instructions;
            """);
        m.Sql("""
            alter table agent_modes drop column bindings_json;
            """);
        m.Sql("""
            alter table mode_nodes drop column relationship_json;
            """);
    }
}
