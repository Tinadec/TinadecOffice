using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Memory;

namespace TinadecCore.Storage.Migrations.PostgreSql;

/// <summary>
/// A run's applied patches are read on every context build of every agent in it (steering and
/// recorded facts, todo D2), so they are looked up by run and revision instead of scanning the session.
/// </summary>
[DbContext(typeof(MemoryDbContext))]
[Migration("202609300001_ContextPatchRunIndex")]
public sealed class ContextPatchRunIndex : Migration
{
    protected override void Up(MigrationBuilder m) =>
        m.Sql("create index if not exists ix_context_patches_run_revision on context_patches(session_id, run_id, applied_revision);");

    protected override void Down(MigrationBuilder m) =>
        m.Sql("drop index if exists ix_context_patches_run_revision;");
}
