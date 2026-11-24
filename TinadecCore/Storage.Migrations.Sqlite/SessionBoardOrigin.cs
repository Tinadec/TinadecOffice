using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Memory;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(MemoryDbContext))]
[Migration("202611240001_SessionBoardOrigin")]
public sealed class SessionBoardOrigin : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table sessions add column origin text null;
        alter table sessions add column board_task_id text null;
        """);

    protected override void Down(MigrationBuilder m) { }
}
