using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608180005_DurableToolApprovals")]
public sealed class DurableToolApprovals : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        -- Keep this migration self-sufficient.  Earlier versions relied on an
        -- idempotent model bootstrapper to create approval_requests after
        -- migrations, so a fresh database must receive its pre-durable shape
        -- before the ALTER statements below.
        create table if not exists approval_requests (
            id uuid primary key,
            tenant_id uuid not null,
            workspace_id uuid not null,
            project_id uuid null,
            session_id uuid null,
            run_id uuid null,
            task_id uuid null,
            policy_version_id uuid null,
            kind varchar(64) not null,
            tool_id varchar(256) not null,
            request_hash varchar(128) not null,
            parameters_reference varchar(1024) not null,
            summary varchar(4096) not null,
            status varchar(32) not null,
            expires_at timestamptz not null,
            requested_by_principal_id uuid not null,
            consumed_by_execution_id uuid null,
            created_at timestamptz not null,
            updated_at timestamptz not null
        );
        create index if not exists ix_approval_requests_tenant_workspace_status_expires_at on approval_requests(tenant_id, workspace_id, status, expires_at);
        create index if not exists ix_approval_requests_run_id_tool_id_request_hash on approval_requests(run_id, tool_id, request_hash);

        alter table approval_requests add column if not exists agent_instance_id uuid null;
        alter table approval_requests add column if not exists execution_id uuid null;
        alter table approval_requests add column if not exists risk varchar(32) not null default 'low';
        alter table approval_requests add column if not exists decision varchar(32) null;
        alter table approval_requests add column if not exists decision_reason text null;
        alter table approval_requests add column if not exists decided_at timestamptz null;
        alter table approval_requests add column if not exists consumed_at timestamptz null;
        create index if not exists ix_approval_requests_execution_id on approval_requests(execution_id);

        alter table tool_executions add column if not exists mutates_workspace boolean not null default false;
        alter table tool_executions add column if not exists error_category varchar(128) null;
        alter table tool_executions add column if not exists safe_error_message varchar(4096) null;
        """);

    protected override void Down(MigrationBuilder m) { }
}
