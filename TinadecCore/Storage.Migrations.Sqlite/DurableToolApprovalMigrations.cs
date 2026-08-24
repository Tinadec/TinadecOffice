using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608180005_DurableToolApprovals")]
public sealed class DurableToolApprovals : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        -- Approval requests used to be created by the idempotent schema
        -- bootstrapper, after provider migrations had already run.  Create the
        -- pre-durable shape here so a fresh database can safely apply this
        -- incremental migration before that bootstrapper runs.
        create table if not exists approval_requests (
            id text primary key,
            tenant_id text not null,
            workspace_id text not null,
            project_id text null,
            session_id text null,
            run_id text null,
            task_id text null,
            policy_version_id text null,
            kind text not null,
            tool_id text not null,
            request_hash text not null,
            parameters_reference text not null,
            summary text not null,
            status text not null,
            expires_at text not null,
            requested_by_principal_id text not null,
            consumed_by_execution_id text null,
            created_at text not null,
            updated_at text not null
        );
        create index if not exists ix_approval_requests_tenant_workspace_status_expires_at on approval_requests(tenant_id, workspace_id, status, expires_at);
        create index if not exists ix_approval_requests_run_id_tool_id_request_hash on approval_requests(run_id, tool_id, request_hash);

        alter table approval_requests add column agent_instance_id text null;
        alter table approval_requests add column execution_id text null;
        alter table approval_requests add column risk text not null default 'low';
        alter table approval_requests add column decision text null;
        alter table approval_requests add column decision_reason text null;
        alter table approval_requests add column decided_at text null;
        alter table approval_requests add column consumed_at text null;
        create index if not exists ix_approval_requests_execution_id on approval_requests(execution_id);

        alter table tool_executions add column mutates_workspace integer not null default 0;
        alter table tool_executions add column error_category text null;
        alter table tool_executions add column safe_error_message text null;
        """);

    protected override void Down(MigrationBuilder m) { }
}
