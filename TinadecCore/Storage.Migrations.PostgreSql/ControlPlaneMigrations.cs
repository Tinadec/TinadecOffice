using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Memory;
using TinadecCore.Lifecycle;
using TinadecCore.Tenancy;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(TenancyDbContext))]
[Migration("202607170001_TenancyInitial")]
public sealed class TenancyInitial : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table if not exists tenants (id uuid primary key, slug varchar(128) not null unique, name varchar(256) not null, status varchar(32) not null, created_at timestamptz not null, updated_at timestamptz not null, deleted_at timestamptz null);
        create table if not exists principals (id uuid primary key, issuer varchar(512) not null, subject varchar(512) not null, display_name varchar(256) not null, status varchar(32) not null, created_at timestamptz not null, updated_at timestamptz not null, unique(issuer, subject));
        create table if not exists tenant_memberships (tenant_id uuid not null, principal_id uuid not null, role varchar(32) not null, status varchar(32) not null, created_at timestamptz not null, updated_at timestamptz not null, primary key(tenant_id, principal_id));
        create table if not exists workspaces (id uuid primary key, tenant_id uuid not null, slug varchar(128) not null, name varchar(256) not null, status varchar(32) not null, created_at timestamptz not null, updated_at timestamptz not null, deleted_at timestamptz null, unique(tenant_id, slug));
        create table if not exists workspace_memberships (workspace_id uuid not null, principal_id uuid not null, role varchar(32) not null, status varchar(32) not null, created_at timestamptz not null, updated_at timestamptz not null, primary key(workspace_id, principal_id));
        """);
    protected override void Down(MigrationBuilder m) => m.Sql("drop table if exists workspace_memberships; drop table if exists workspaces; drop table if exists tenant_memberships; drop table if exists principals; drop table if exists tenants;");
}

[DbContext(typeof(MemoryDbContext))]
[Migration("202607170002_MemoryTenantScope")]
public sealed class MemoryTenantScope : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table projects add column if not exists tenant_id uuid not null default '00000000-0000-0000-0000-000000000000';
        alter table projects add column if not exists workspace_id uuid not null default '00000000-0000-0000-0000-000000000000';
        alter table sessions add column if not exists tenant_id uuid not null default '00000000-0000-0000-0000-000000000000';
        alter table sessions add column if not exists workspace_id uuid not null default '00000000-0000-0000-0000-000000000000';
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
        alter table runs add column if not exists tenant_id uuid not null default '00000000-0000-0000-0000-000000000000';
        alter table runs add column if not exists workspace_id uuid not null default '00000000-0000-0000-0000-000000000000';
        alter table event_index add column if not exists tenant_id uuid not null default '00000000-0000-0000-0000-000000000000';
        alter table event_index add column if not exists workspace_id uuid not null default '00000000-0000-0000-0000-000000000000';
        create index if not exists ix_runs_tenant_workspace_session_created on runs(tenant_id, workspace_id, session_id, created_at);
        create index if not exists ix_event_index_tenant_workspace_session_timestamp on event_index(tenant_id, workspace_id, session_id, timestamp);
        """);
    protected override void Down(MigrationBuilder m) { }
}
