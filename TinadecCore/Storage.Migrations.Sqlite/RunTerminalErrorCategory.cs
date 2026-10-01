using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.Sqlite;

/// <summary>
/// Persists the classified terminal failure together with the failed run status so
/// a crash between the status claim and the error stream cannot degrade a model or
/// worker failure into a generic runtime error during recovery.
/// </summary>
[DbContext(typeof(LifecycleDbContext))]
[Migration("202609180001_RunTerminalErrorCategory")]
public sealed class RunTerminalErrorCategory : Migration
{
    protected override void Up(MigrationBuilder m) =>
        m.Sql("alter table runs add column terminal_error_category text null;");

    protected override void Down(MigrationBuilder m) =>
        m.Sql("alter table runs drop column terminal_error_category;");
}
