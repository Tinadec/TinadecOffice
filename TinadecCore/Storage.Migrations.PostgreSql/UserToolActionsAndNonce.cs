using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608220007_UserToolActionsAndNonce")]
public sealed class UserToolActionsAndNonce : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table approval_requests add column if not exists nonce_secret_reference varchar(512) null;
        alter table approval_requests add column if not exists user_tool_action_id uuid null;
        create table if not exists user_tool_actions (
            id uuid primary key, tenant_id uuid not null, workspace_id uuid not null, project_id uuid not null,
            principal_id uuid not null, tool_id varchar(256) not null, parameters_reference varchar(1024) not null,
            parameters_length bigint not null, parameters_hash varchar(128) not null, risk varchar(32) not null,
            mutates_workspace boolean not null, requires_approval boolean not null, status varchar(32) not null,
            idempotency_key varchar(256) null, permission_request_id uuid null, authorization_decision_id uuid null,
            capability_lease_id uuid null, action_approval_id uuid null, snapshot_id uuid null,
            snapshot_hash varchar(128) null, snapshot_override boolean not null default false, snapshot_override_reason varchar(4096) null,
            result_reference varchar(1024) null, result_hash varchar(128) null, result_length bigint null,
            error_category varchar(128) null, safe_error_message varchar(4096) null, attempt integer not null default 1,
            created_at timestamptz not null, updated_at timestamptz not null, completed_at timestamptz null
        );
        create unique index if not exists ix_user_tool_actions_idempotency on user_tool_actions(tenant_id, workspace_id, idempotency_key) where idempotency_key is not null;
        create unique index if not exists ix_approval_requests_user_tool_action on approval_requests(user_tool_action_id) where user_tool_action_id is not null;
        create index if not exists ix_user_tool_actions_created on user_tool_actions(tenant_id, workspace_id, created_at);
        """);
    protected override void Down(MigrationBuilder m) { }
}
