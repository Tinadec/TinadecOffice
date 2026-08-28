using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Memory;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(MemoryDbContext))]
[Migration("202608280001_LifecycleStatus")]
public sealed class LifecycleStatus : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table projects add column lifecycle_status text not null default 'active';
        alter table projects add column trashed_at text null;
        update projects set lifecycle_status = 'archived' where archived = 1;
        alter table sessions add column lifecycle_status text not null default 'active';
        alter table sessions add column trashed_at text null;
        update sessions set lifecycle_status = 'archived' where archived = 1;
        drop index if exists IX_projects_archived_updated_at;
        drop index if exists ix_projects_tenant_id_workspace_id_archived_updated_at;
        drop index if exists ix_projects_tenant_workspace_updated;
        drop index if exists IX_sessions_project_id_archived_updated_at;
        drop index if exists ix_sessions_tenant_id_workspace_id_project_id_archived_updated_at;
        drop index if exists ix_sessions_tenant_workspace_project_updated;
        alter table projects drop column archived;
        alter table sessions drop column archived;
        create index if not exists ix_projects_tenant_id_workspace_id_lifecycle_status_updated_at
            on projects(tenant_id, workspace_id, lifecycle_status, updated_at);
        create index if not exists ix_projects_lifecycle_status_updated_at
            on projects(lifecycle_status, updated_at);
        create index if not exists ix_sessions_tenant_id_workspace_id_project_id_lifecycle_status_updated_at
            on sessions(tenant_id, workspace_id, project_id, lifecycle_status, updated_at);
        """);

    protected override void Down(MigrationBuilder m) { }
}
