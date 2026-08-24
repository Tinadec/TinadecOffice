using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608220009_WorkspaceSnapshotBindings")]
public sealed class WorkspaceSnapshotBindings : Migration
{
    // SQLite upgrades are handled by StorageLifecycleService's additive schema
    // bootstrap. Keeping this migration intentionally empty makes upgrades safe
    // for databases that were already aligned by that bootstrap.
    protected override void Up(MigrationBuilder m) { }

    protected override void Down(MigrationBuilder m) { }
}
