using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TinadecCore.Lifecycle;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608220007_UserToolActionsAndNonce")]
public sealed class UserToolActionsAndNonce : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table approval_requests add column nonce_secret_reference text null;
        alter table approval_requests add column user_tool_action_id text null;
        create table if not exists user_tool_actions (
            id text primary key, tenant_id text not null, workspace_id text not null, project_id text not null,
            principal_id text not null, tool_id text not null, parameters_reference text not null,
            parameters_length integer not null, parameters_hash text not null, risk text not null,
            mutates_workspace integer not null, requires_approval integer not null, status text not null,
            idempotency_key text null, permission_request_id text null, authorization_decision_id text null,
            capability_lease_id text null, action_approval_id text null, snapshot_id text null,
            snapshot_hash text null, snapshot_override integer not null default 0, snapshot_override_reason text null,
            result_reference text null, result_hash text null, result_length integer null,
            error_category text null, safe_error_message text null, attempt integer not null default 1,
            created_at text not null, updated_at text not null, completed_at text null
        );
        create unique index if not exists ix_user_tool_actions_idempotency on user_tool_actions(tenant_id, workspace_id, idempotency_key) where idempotency_key is not null;
        create unique index if not exists ix_approval_requests_user_tool_action on approval_requests(user_tool_action_id) where user_tool_action_id is not null;
        create index if not exists ix_user_tool_actions_created on user_tool_actions(tenant_id, workspace_id, created_at);
        """);
    protected override void Down(MigrationBuilder m) { }
}
