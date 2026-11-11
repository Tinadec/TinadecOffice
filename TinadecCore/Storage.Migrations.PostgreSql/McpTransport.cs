using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Tools;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(ToolsSettingsDbContext))]
[Migration("202611110001_McpTransport")]
public sealed class McpTransport : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table tool_mcp_resources add column if not exists transport TEXT NOT NULL DEFAULT 'stdio';
        alter table tool_mcp_resources add column if not exists url TEXT NULL;
        alter table tool_mcp_resources add column if not exists header_references_json TEXT NOT NULL DEFAULT '{}';
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("""
        alter table tool_mcp_resources drop column if exists header_references_json;
        alter table tool_mcp_resources drop column if exists url;
        alter table tool_mcp_resources drop column if exists transport;
        """);
}
