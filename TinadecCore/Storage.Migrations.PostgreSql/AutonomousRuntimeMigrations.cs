using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(MemoryDbContext))]
[Migration("202608170001_AutonomousMemory")]
public sealed class AutonomousMemory : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table if not exists messages (id uuid primary key, tenant_id uuid not null, workspace_id uuid not null, session_id uuid not null, run_id uuid null, turn_id uuid null, client_message_id varchar(256) null, sequence bigint not null, role varchar(32) not null, content_reference varchar(1024) not null, content_hash varchar(128) not null, content_length bigint not null, created_at timestamptz not null);
        create unique index if not exists ix_messages_session_sequence on messages(tenant_id, workspace_id, session_id, sequence);
        create unique index if not exists ix_messages_client_id on messages(tenant_id, workspace_id, session_id, client_message_id) where client_message_id is not null;
        create table if not exists turns (id uuid primary key, tenant_id uuid not null, workspace_id uuid not null, session_id uuid not null, user_message_id uuid not null, assistant_message_id uuid null, run_id uuid null, kind varchar(32) not null, status varchar(32) not null, base_context_revision bigint not null, result_context_revision bigint not null, created_at timestamptz not null, updated_at timestamptz not null, completed_at timestamptz null);
        create unique index if not exists ix_turns_user_message on turns(tenant_id, workspace_id, user_message_id);
        create table if not exists context_snapshots (id uuid primary key, tenant_id uuid not null, workspace_id uuid not null, session_id uuid not null, run_id uuid null, revision bigint not null, content_reference varchar(1024) not null, content_hash varchar(128) not null, content_length bigint not null, created_at timestamptz not null);
        create unique index if not exists ix_context_snapshots_session_revision on context_snapshots(tenant_id, workspace_id, session_id, revision);
        create table if not exists context_patches (id uuid primary key, tenant_id uuid not null, workspace_id uuid not null, session_id uuid not null, run_id uuid null, agent_instance_id uuid null, base_revision bigint not null, applied_revision bigint null, status varchar(32) not null, content_reference varchar(1024) not null, content_hash varchar(128) not null, content_length bigint not null, created_at timestamptz not null, applied_at timestamptz null);
        create table if not exists memory_candidates (id uuid primary key, tenant_id uuid not null, workspace_id uuid not null, project_id uuid null, agent_profile_id uuid null, source_run_id uuid not null, generated_by_instance_id uuid not null, scope varchar(32) not null, kind varchar(64) not null, status varchar(32) not null, confidence double precision not null, content_reference varchar(1024) not null, content_hash varchar(128) not null, content_length bigint not null, decision_reason text null, promoted_memory_item_id uuid null, created_by_principal_id uuid not null, decided_by_principal_id uuid null, created_at timestamptz not null, updated_at timestamptz not null);
        create table if not exists memory_items (id uuid primary key, tenant_id uuid not null, workspace_id uuid not null, project_id uuid null, principal_id uuid null, agent_profile_id uuid null, scope varchar(32) not null, kind varchar(64) not null, status varchar(32) not null, current_version integer not null, current_version_id uuid not null, created_by_principal_id uuid not null, created_at timestamptz not null, updated_at timestamptz not null, revoked_at timestamptz null, superseded_by_id uuid null);
        create table if not exists memory_versions (id uuid primary key, memory_item_id uuid not null, version integer not null, source_candidate_id uuid null, content_reference varchar(1024) not null, content_hash varchar(128) not null, content_length bigint not null, created_by_principal_id uuid not null, created_at timestamptz not null);
        create unique index if not exists ix_memory_versions_item_version on memory_versions(memory_item_id, version);
        """);
    protected override void Down(MigrationBuilder m) { }
}

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608170002_AutonomousLifecycle")]
public sealed class AutonomousLifecycle : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table runs add column if not exists turn_id uuid null;
        alter table runs add column if not exists context_revision bigint not null default 0;
        alter table runs add column if not exists configuration_version bigint not null default 0;
        alter table runs add column if not exists configuration_hash varchar(128) not null default '';
        alter table runs add column if not exists application_mode varchar(64) not null default 'conversation';
        alter table runs add column if not exists agent_mode varchar(64) not null default 'auto';
        alter table runs add column if not exists permission_mode varchar(64) not null default 'default';
        alter table runs add column if not exists runtime_profile_id varchar(256) not null default 'conversation.auto';
        create table if not exists tool_executions (id uuid primary key, tenant_id uuid not null, workspace_id uuid not null, project_id uuid null, session_id uuid not null, run_id uuid not null, task_id uuid not null, agent_instance_id uuid not null, approval_id uuid null, tool_id varchar(256) not null, risk varchar(32) not null, requires_approval boolean not null, status varchar(32) not null, parameters_hash varchar(128) not null, parameters_reference varchar(1024) not null, parameters_length bigint not null, result_reference varchar(1024) null, result_hash varchar(128) null, result_length bigint null, attempt integer not null, created_at timestamptz not null, updated_at timestamptz not null, completed_at timestamptz null);
        create index if not exists ix_tool_executions_run_created on tool_executions(tenant_id, workspace_id, run_id, created_at);
        """);
    protected override void Down(MigrationBuilder m) { }
}

[DbContext(typeof(AgentControlDbContext))]
[Migration("202608170003_AutonomousAgentControl")]
public sealed class AutonomousAgentControl : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table if not exists agent_instances (id uuid primary key, tenant_id uuid not null, workspace_id uuid not null, session_id uuid not null, run_id uuid not null, task_node_id uuid null, parent_instance_id uuid null, profile_id uuid null, created_by_profile_id uuid null, layer varchar(32) not null, role varchar(128) not null, generation_depth integer not null, generated boolean not null, status varchar(32) not null, definition_reference varchar(1024) not null, definition_hash varchar(128) not null, definition_length bigint not null, created_at timestamptz not null, updated_at timestamptz not null, released_at timestamptz null);
        create table if not exists agent_candidates (id uuid primary key, tenant_id uuid not null, workspace_id uuid not null, project_id uuid null, source_run_id uuid not null, source_instance_id uuid not null, generated_by_instance_id uuid not null, name varchar(256) not null, layer varchar(32) not null, agent_type varchar(128) not null, status varchar(32) not null, confidence_score double precision not null, proposal_reference varchar(1024) not null, proposal_hash varchar(128) not null, proposal_length bigint not null, promoted_agent_id uuid null, decision_reason text null, created_by_principal_id uuid not null, decided_by_principal_id uuid null, created_at timestamptz not null, updated_at timestamptz not null);
        create table if not exists runtime_profile_overrides (id uuid primary key, tenant_id uuid not null, workspace_id uuid not null, profile_id varchar(256) not null, version integer not null, enabled boolean not null, content_reference varchar(1024) not null, content_hash varchar(128) not null, content_length bigint not null, created_by_principal_id uuid not null, created_at timestamptz not null);
        """);
    protected override void Down(MigrationBuilder m) { }
}

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608180004_DurableLifecycle")]
public sealed class DurableLifecycle : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table runs add column if not exists frozen_configuration_reference varchar(1024) null;
        alter table runs add column if not exists frozen_configuration_hash varchar(128) null;
        alter table runs add column if not exists frozen_configuration_length bigint null;
        alter table runs add column if not exists frozen_configuration_schema_version varchar(64) null;
        alter table runs add column if not exists frozen_configuration_at timestamptz null;
        alter table runs add column if not exists current_checkpoint_id uuid null;
        alter table runs add column if not exists checkpoint_revision bigint not null default 0;
        alter table runs add column if not exists lease_owner varchar(256) null;
        alter table runs add column if not exists lease_expires_at timestamptz null;
        alter table runs add column if not exists lease_heartbeat_at timestamptz null;
        alter table runs add column if not exists lease_expires_unix_milliseconds bigint null;
        alter table runs add column if not exists lease_heartbeat_unix_milliseconds bigint null;
        alter table runs add column if not exists recovery_count integer not null default 0;
        create unique index if not exists ix_runs_trigger_message on runs(tenant_id, workspace_id, session_id, trigger_message_id);
        create index if not exists ix_runs_status_lease_expires on runs(status, lease_expires_at);
        create table if not exists run_checkpoints (id uuid primary key, tenant_id uuid not null, workspace_id uuid not null, run_id uuid not null, revision bigint not null, phase varchar(64) not null, idempotency_key varchar(256) not null, content_reference varchar(1024) not null, content_hash varchar(128) not null, content_length bigint not null, applied_through_event_sequence bigint not null, created_at timestamptz not null);
        create unique index if not exists ix_run_checkpoints_run_revision on run_checkpoints(run_id, revision);
        create unique index if not exists ix_run_checkpoints_run_key on run_checkpoints(run_id, idempotency_key);
        create table if not exists run_stream (id uuid primary key, tenant_id uuid not null, workspace_id uuid not null, run_id uuid not null, turn_id uuid not null, message_id uuid null, sequence bigint not null, kind varchar(32) not null, idempotency_key varchar(256) null, delta_reference varchar(1024) null, delta_hash varchar(128) null, delta_length bigint null, usage_reference varchar(1024) null, usage_hash varchar(128) null, usage_length bigint null, finish_reason varchar(128) null, error_category varchar(128) null, safe_error_message varchar(4096) null, created_at timestamptz not null);
        create unique index if not exists ix_run_stream_run_sequence on run_stream(run_id, sequence);
        create index if not exists ix_run_stream_run_turn_sequence on run_stream(run_id, turn_id, sequence);
        create unique index if not exists ix_run_stream_run_key on run_stream(run_id, idempotency_key);
        create table if not exists run_stream_cursors (run_id uuid primary key, next_sequence bigint not null, updated_at timestamptz not null);
        """);
    protected override void Down(MigrationBuilder m) { }
}
