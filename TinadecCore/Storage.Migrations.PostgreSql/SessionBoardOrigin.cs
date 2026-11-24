using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Memory;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(MemoryDbContext))]
[Migration("202611240001_SessionBoardOrigin")]
public sealed class SessionBoardOrigin : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table sessions add column if not exists origin text null;
        alter table sessions add column if not exists board_task_id uuid null;
        """);

    protected override void Down(MigrationBuilder m) { }
}
