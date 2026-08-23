using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Storage.Migrations.PostgreSql;

/// <summary>
/// PostgreSQL counterpart of the Sqlite AgentDefinitionProfileFields migration:
/// adds system_prompt/description/enabled to agent_definitions for full versioned profiles.
/// </summary>
[DbContext(typeof(AgentConfigurationDbContext))]
[Migration("202608230001_AgentDefinitionProfileFields")]
public sealed class AgentDefinitionProfileFields : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table agent_definitions add column if not exists system_prompt text null;
        alter table agent_definitions add column if not exists description text null;
        alter table agent_definitions add column if not exists enabled boolean not null default true;
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("""
        alter table agent_definitions drop column if exists enabled;
        alter table agent_definitions drop column if exists description;
        alter table agent_definitions drop column if exists system_prompt;
        """);
}
