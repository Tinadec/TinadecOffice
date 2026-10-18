using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(AgentConfigurationDbContext))]
[Migration("202610190001_CorrectionModePolicy")]
public sealed class CorrectionModePolicy : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        -- The projection schema bootstrapper adds model columns before the
        -- wrapped configuration context is first queried. Keeping this marker
        -- migration a no-op makes old databases upgrade once without making a
        -- second schema pass fail on a duplicate column.
        select 1;
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("""
        select 1;
        """);
}
