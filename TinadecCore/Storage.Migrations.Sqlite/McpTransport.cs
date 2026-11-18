using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Tools;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(ToolsSettingsDbContext))]
[Migration("202611110001_McpTransport")]
public sealed class McpTransport : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table tool_mcp_resources add column transport TEXT NOT NULL DEFAULT 'stdio';
        alter table tool_mcp_resources add column url TEXT NULL;
        alter table tool_mcp_resources add column header_references_json TEXT NOT NULL DEFAULT '{}';
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("""
        alter table tool_mcp_resources drop column header_references_json;
        alter table tool_mcp_resources drop column url;
        alter table tool_mcp_resources drop column transport;
        """);
}
