using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Memory;
using TinadecCore.Lifecycle;
using TinadecCore.Tenancy;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(TenancyDbContext))]
[Migration("202607170001_TenancyInitial")]
public sealed class TenancyInitial : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table if not exists tenants (id text primary key, slug text not null unique, name text not null, status text not null, created_at text not null, updated_at text not null, deleted_at text null);
        create table if not exists principals (id text primary key, issuer text not null, subject text not null, display_name text not null, status text not null, created_at text not null, updated_at text not null, unique(issuer, subject));
        create table if not exists tenant_memberships (tenant_id text not null, principal_id text not null, role text not null, status text not null, created_at text not null, updated_at text not null, primary key(tenant_id, principal_id));
        create table if not exists workspaces (id text primary key, tenant_id text not null, slug text not null, name text not null, status text not null, created_at text not null, updated_at text not null, deleted_at text null, unique(tenant_id, slug));
        create table if not exists workspace_memberships (workspace_id text not null, principal_id text not null, role text not null, status text not null, created_at text not null, updated_at text not null, primary key(workspace_id, principal_id));
        """);
    protected override void Down(MigrationBuilder m) => m.Sql("drop table if exists workspace_memberships; drop table if exists workspaces; drop table if exists tenant_memberships; drop table if exists principals; drop table if exists tenants;");
}

[DbContext(typeof(MemoryDbContext))]
[Migration("202607170002_MemoryTenantScope")]
public sealed class MemoryTenantScope : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table projects add column tenant_id text not null default '00000000-0000-0000-0000-000000000000';
        alter table projects add column workspace_id text not null default '00000000-0000-0000-0000-000000000000';
        alter table sessions add column tenant_id text not null default '00000000-0000-0000-0000-000000000000';
        alter table sessions add column workspace_id text not null default '00000000-0000-0000-0000-000000000000';
        create index if not exists ix_projects_tenant_workspace_updated on projects(tenant_id, workspace_id, archived, updated_at);
        create index if not exists ix_sessions_tenant_workspace_project_updated on sessions(tenant_id, workspace_id, project_id, archived, updated_at);
        """);
    protected override void Down(MigrationBuilder m) { }
}

[DbContext(typeof(LifecycleDbContext))]
[Migration("202607170003_LifecycleTenantScope")]
public sealed class LifecycleTenantScope : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table runs add column tenant_id text not null default '00000000-0000-0000-0000-000000000000';
        alter table runs add column workspace_id text not null default '00000000-0000-0000-0000-000000000000';
        alter table event_index add column tenant_id text not null default '00000000-0000-0000-0000-000000000000';
        alter table event_index add column workspace_id text not null default '00000000-0000-0000-0000-000000000000';
        create index if not exists ix_runs_tenant_workspace_session_created on runs(tenant_id, workspace_id, session_id, created_at);
        create index if not exists ix_event_index_tenant_workspace_session_timestamp on event_index(tenant_id, workspace_id, session_id, timestamp);
        """);
    protected override void Down(MigrationBuilder m) { }
}
