using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Storage.Migrations.Sqlite;

/// <summary>
/// Promotes agent profile fields (system_prompt/description/enabled) into the versioned
/// agent_definitions row so Desktop edits go through draft→publish with a single source
/// of truth. Columns are nullable/defaulted so databases created by older Core builds
/// migrate in place.
/// </summary>
[DbContext(typeof(AgentConfigurationDbContext))]
[Migration("202608230001_AgentDefinitionProfileFields")]
public sealed class AgentDefinitionProfileFields : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table agent_definitions add column system_prompt text null;
        alter table agent_definitions add column description text null;
        alter table agent_definitions add column enabled integer not null default 1;
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("""
        alter table agent_definitions drop column enabled;
        alter table agent_definitions drop column description;
        alter table agent_definitions drop column system_prompt;
        """);
}
