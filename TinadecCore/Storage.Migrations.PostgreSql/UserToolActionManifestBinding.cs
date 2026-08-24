using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608220011_UserToolActionManifestBinding")]
public sealed class UserToolActionManifestBinding : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table user_tool_actions add column if not exists provider_protocol_version integer not null default 0;
        alter table user_tool_actions add column if not exists provider_manifest_hash varchar(128) not null default '';
        alter table user_tool_actions add column if not exists tool_descriptor_reference varchar(1024) not null default '';
        alter table user_tool_actions add column if not exists tool_descriptor_hash varchar(128) not null default '';
        alter table user_tool_actions add column if not exists tool_descriptor_length bigint not null default 0;
        """);

    protected override void Down(MigrationBuilder m) { }
}
