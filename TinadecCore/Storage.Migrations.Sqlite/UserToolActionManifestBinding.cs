using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608220011_UserToolActionManifestBinding")]
public sealed class UserToolActionManifestBinding : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table user_tool_actions add column provider_protocol_version integer not null default 0;
        alter table user_tool_actions add column provider_manifest_hash text not null default '';
        alter table user_tool_actions add column tool_descriptor_reference text not null default '';
        alter table user_tool_actions add column tool_descriptor_hash text not null default '';
        alter table user_tool_actions add column tool_descriptor_length integer not null default 0;
        """);

    protected override void Down(MigrationBuilder m) { }
}
