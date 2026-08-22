using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(MemoryDbContext))]
[Migration("202608170001_AutonomousMemory")]
public sealed class AutonomousMemory : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table if not exists messages (id text primary key, tenant_id text not null, workspace_id text not null, session_id text not null, run_id text null, turn_id text null, client_message_id text null, sequence integer not null, role text not null, content_reference text not null, content_hash text not null, content_length integer not null, created_at text not null);
        create unique index if not exists ix_messages_session_sequence on messages(tenant_id, workspace_id, session_id, sequence);
        create unique index if not exists ix_messages_client_id on messages(tenant_id, workspace_id, session_id, client_message_id) where client_message_id is not null;
        create table if not exists turns (id text primary key, tenant_id text not null, workspace_id text not null, session_id text not null, user_message_id text not null, assistant_message_id text null, run_id text null, kind text not null, status text not null, base_context_revision integer not null, result_context_revision integer not null, created_at text not null, updated_at text not null, completed_at text null);
        create unique index if not exists ix_turns_user_message on turns(tenant_id, workspace_id, user_message_id);
        create table if not exists context_snapshots (id text primary key, tenant_id text not null, workspace_id text not null, session_id text not null, run_id text null, revision integer not null, content_reference text not null, content_hash text not null, content_length integer not null, created_at text not null);
        create unique index if not exists ix_context_snapshots_session_revision on context_snapshots(tenant_id, workspace_id, session_id, revision);
        create table if not exists context_patches (id text primary key, tenant_id text not null, workspace_id text not null, session_id text not null, run_id text null, agent_instance_id text null, base_revision integer not null, applied_revision integer null, status text not null, content_reference text not null, content_hash text not null, content_length integer not null, created_at text not null, applied_at text null);
        create table if not exists memory_candidates (id text primary key, tenant_id text not null, workspace_id text not null, project_id text null, agent_profile_id text null, source_run_id text not null, generated_by_instance_id text not null, scope text not null, kind text not null, status text not null, confidence real not null, content_reference text not null, content_hash text not null, content_length integer not null, decision_reason text null, promoted_memory_item_id text null, created_by_principal_id text not null, decided_by_principal_id text null, created_at text not null, updated_at text not null);
        create table if not exists memory_items (id text primary key, tenant_id text not null, workspace_id text not null, project_id text null, principal_id text null, agent_profile_id text null, scope text not null, kind text not null, status text not null, current_version integer not null, current_version_id text not null, created_by_principal_id text not null, created_at text not null, updated_at text not null, revoked_at text null, superseded_by_id text null);
        create table if not exists memory_versions (id text primary key, memory_item_id text not null, version integer not null, source_candidate_id text null, content_reference text not null, content_hash text not null, content_length integer not null, created_by_principal_id text not null, created_at text not null);
        create unique index if not exists ix_memory_versions_item_version on memory_versions(memory_item_id, version);
        """);
    protected override void Down(MigrationBuilder m) { }
}

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608170002_AutonomousLifecycle")]
public sealed class AutonomousLifecycle : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table runs add column turn_id text null;
        alter table runs add column context_revision integer not null default 0;
        alter table runs add column configuration_version integer not null default 0;
        alter table runs add column configuration_hash text not null default '';
        alter table runs add column application_mode text not null default 'conversation';
        alter table runs add column agent_mode text not null default 'auto';
        alter table runs add column permission_mode text not null default 'default';
        alter table runs add column runtime_profile_id text not null default 'conversation.auto';
        create table if not exists tool_executions (id text primary key, tenant_id text not null, workspace_id text not null, project_id text null, session_id text not null, run_id text not null, task_id text not null, agent_instance_id text not null, approval_id text null, tool_id text not null, risk text not null, requires_approval integer not null, status text not null, parameters_hash text not null, parameters_reference text not null, parameters_length integer not null, result_reference text null, result_hash text null, result_length integer null, attempt integer not null, created_at text not null, updated_at text not null, completed_at text null);
        create index if not exists ix_tool_executions_run_created on tool_executions(tenant_id, workspace_id, run_id, created_at);
        """);
    protected override void Down(MigrationBuilder m) { }
}

[DbContext(typeof(AgentControlDbContext))]
[Migration("202608170003_AutonomousAgentControl")]
public sealed class AutonomousAgentControl : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table if not exists agent_instances (id text primary key, tenant_id text not null, workspace_id text not null, session_id text not null, run_id text not null, task_node_id text null, parent_instance_id text null, profile_id text null, created_by_profile_id text null, layer text not null, role text not null, generation_depth integer not null, generated integer not null, status text not null, definition_reference text not null, definition_hash text not null, definition_length integer not null, created_at text not null, updated_at text not null, released_at text null);
        create table if not exists agent_candidates (id text primary key, tenant_id text not null, workspace_id text not null, project_id text null, source_run_id text not null, source_instance_id text not null, generated_by_instance_id text not null, name text not null, layer text not null, agent_type text not null, status text not null, confidence_score real not null, proposal_reference text not null, proposal_hash text not null, proposal_length integer not null, promoted_agent_id text null, decision_reason text null, created_by_principal_id text not null, decided_by_principal_id text null, created_at text not null, updated_at text not null);
        create table if not exists runtime_profile_overrides (id text primary key, tenant_id text not null, workspace_id text not null, profile_id text not null, version integer not null, enabled integer not null, content_reference text not null, content_hash text not null, content_length integer not null, created_by_principal_id text not null, created_at text not null);
        """);
    protected override void Down(MigrationBuilder m) { }
}

[DbContext(typeof(AgentControlDbContext))]
[Migration("202608220002_AgentInstanceVersionBinding")]
public sealed class AgentInstanceVersionBinding : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table agent_instances add column agent_definition_id text not null default '00000000-0000-0000-0000-000000000000';
        alter table agent_instances add column agent_version_id text not null default '00000000-0000-0000-0000-000000000000';
        alter table agent_instances add column agent_version_hash text not null default '';
        create index if not exists ix_agent_instances_run_version on agent_instances(tenant_id, workspace_id, run_id, agent_version_id);
        """);
    protected override void Down(MigrationBuilder m) { }
}

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608180004_DurableLifecycle")]
public sealed class DurableLifecycle : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table runs add column frozen_configuration_reference text null;
        alter table runs add column frozen_configuration_hash text null;
        alter table runs add column frozen_configuration_length integer null;
        alter table runs add column frozen_configuration_schema_version text null;
        alter table runs add column frozen_configuration_at text null;
        alter table runs add column current_checkpoint_id text null;
        alter table runs add column checkpoint_revision integer not null default 0;
        alter table runs add column lease_owner text null;
        alter table runs add column lease_expires_at text null;
        alter table runs add column lease_heartbeat_at text null;
        alter table runs add column lease_expires_unix_milliseconds integer null;
        alter table runs add column lease_heartbeat_unix_milliseconds integer null;
        alter table runs add column recovery_count integer not null default 0;
        create unique index if not exists ix_runs_trigger_message on runs(tenant_id, workspace_id, session_id, trigger_message_id);
        create index if not exists ix_runs_status_lease_expires on runs(status, lease_expires_at);
        create table if not exists run_checkpoints (id text primary key, tenant_id text not null, workspace_id text not null, run_id text not null, revision integer not null, phase text not null, idempotency_key text not null, content_reference text not null, content_hash text not null, content_length integer not null, applied_through_event_sequence integer not null, created_at text not null);
        create unique index if not exists ix_run_checkpoints_run_revision on run_checkpoints(run_id, revision);
        create unique index if not exists ix_run_checkpoints_run_key on run_checkpoints(run_id, idempotency_key);
        create table if not exists run_stream (id text primary key, tenant_id text not null, workspace_id text not null, run_id text not null, turn_id text not null, message_id text null, sequence integer not null, kind text not null, idempotency_key text null, delta_reference text null, delta_hash text null, delta_length integer null, usage_reference text null, usage_hash text null, usage_length integer null, finish_reason text null, error_category text null, safe_error_message text null, created_at text not null);
        create unique index if not exists ix_run_stream_run_sequence on run_stream(run_id, sequence);
        create index if not exists ix_run_stream_run_turn_sequence on run_stream(run_id, turn_id, sequence);
        create unique index if not exists ix_run_stream_run_key on run_stream(run_id, idempotency_key);
        create table if not exists run_stream_cursors (run_id text primary key, next_sequence integer not null, updated_at text not null);
        """);
    protected override void Down(MigrationBuilder m) { }
}

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608220003_WorkspaceSnapshots")]
public sealed class WorkspaceSnapshots : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table if not exists workspace_snapshots (id text primary key, tenant_id text not null, workspace_id text not null, project_id text not null, kind text not null, status text not null, is_git integer not null, workspace_hash text not null, content_reference text not null, content_hash text not null, content_length integer not null, file_count integer not null, base_snapshot_id text null, idempotency_key text null, last_restore_idempotency_key text null, conflict_json text null, applied_file_count integer not null default 0, restored_at text null, created_at text not null);
        create unique index if not exists ix_workspace_snapshots_idempotency on workspace_snapshots(tenant_id, workspace_id, project_id, idempotency_key) where idempotency_key is not null;
        create index if not exists ix_workspace_snapshots_project_created on workspace_snapshots(tenant_id, workspace_id, project_id, created_at);
        """);
    protected override void Down(MigrationBuilder m) { }
}

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608220006_SessionMetadataSnapshots")]
public sealed class SessionMetadataSnapshots : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table if not exists session_metadata_snapshots (id text primary key, tenant_id text not null, workspace_id text not null, session_id text not null, project_id text not null, kind text not null, source text not null, schema_version text not null, revision integer not null, content_reference text not null, content_hash text not null, content_length integer not null, captured_at text not null);
        create unique index if not exists ix_session_metadata_snapshots_scope_revision on session_metadata_snapshots(tenant_id, workspace_id, session_id, kind, source, revision);
        create unique index if not exists ix_session_metadata_snapshots_scope_hash on session_metadata_snapshots(tenant_id, workspace_id, session_id, kind, source, content_hash);
        create index if not exists ix_session_metadata_snapshots_scope_captured on session_metadata_snapshots(tenant_id, workspace_id, session_id, captured_at);
        """);
    protected override void Down(MigrationBuilder m) { }
}
