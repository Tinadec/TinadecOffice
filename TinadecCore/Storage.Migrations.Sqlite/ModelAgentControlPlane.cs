using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.AgentConfiguration;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Models;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(AgentConfigurationDbContext))]
[Migration("202608270001_AgentModelDirectory")]
public sealed class AgentModelDirectory : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table agent_definitions add column source_kind text not null default 'custom';
        alter table agent_definitions add column source_key text not null default '';
        alter table agent_definitions add column managed integer not null default 0;
        update agent_definitions set source_key = slug where source_key = '';
        update agent_definitions
        set source_kind = 'pack', managed = 1,
            source_key = coalesce((select installation_id || ':' || resource_key from agent_pack_managed_resources
                where resource_kind = 'agent' and logical_entity_id = agent_definitions.id limit 1), slug)
        where exists (select 1 from agent_pack_managed_resources
            where resource_kind = 'agent' and logical_entity_id = agent_definitions.id);
        create unique index if not exists ix_agent_definitions_source_identity
            on agent_definitions(tenant_id, workspace_id, source_kind, source_key);
        alter table mode_nodes add column model_strategy_override_json text null;
        """);

    protected override void Down(MigrationBuilder m) { }
}

[DbContext(typeof(ModelControlDbContext))]
[Migration("202608270002_ModelRoutesV2")]
public sealed class ModelRoutesV2 : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table if not exists model_provider_instances (
            id text primary key, tenant_id text not null, workspace_id text null, project_id text null,
            scope text not null, driver text not null, display_name text not null, connection_kind text not null,
            secret_reference text null, enabled integer not null, revision integer not null, current_version_id text not null,
            created_by_principal_id text not null, updated_by_principal_id text not null,
            created_at text not null, updated_at text not null, deleted_at text null);
        create table if not exists model_provider_versions (
            id text primary key, provider_id text not null, version integer not null,
            content_reference text not null, content_hash text not null, content_length integer not null,
            created_by_principal_id text not null, created_at text not null);
        create unique index if not exists ix_model_provider_versions_provider_version on model_provider_versions(provider_id, version);
        create table if not exists model_routes (
            id text primary key, tenant_id text not null, workspace_id text null, project_id text null,
            scope text not null, purpose text not null, revision integer not null, current_version_id text not null,
            created_by_principal_id text not null, updated_by_principal_id text not null,
            created_at text not null, updated_at text not null, deleted_at text null);
        create table if not exists model_route_versions (
            id text primary key, route_id text not null, version integer not null,
            provider_id text null, model text null, created_by_principal_id text not null, created_at text not null);
        create unique index if not exists ix_model_route_versions_route_version on model_route_versions(route_id, version);
        create table if not exists model_route_candidates (
            id text primary key,
            route_version_id text not null,
            position integer not null,
            provider_instance_id text not null,
            model text null
        );
        create unique index if not exists ix_model_route_candidates_version_position
            on model_route_candidates(route_version_id, position);
        create index if not exists ix_model_route_candidates_provider_model
            on model_route_candidates(provider_instance_id, model);
        insert or ignore into model_route_candidates(id, route_version_id, position, provider_instance_id, model)
        select lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))),2) || '-a' || substr(lower(hex(randomblob(2))),2) || '-' || lower(hex(randomblob(6))),
               id, 0, provider_id, model
        from model_route_versions
        where provider_id is not null;
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("drop table if exists model_route_candidates;");
}

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608270003_ModelInvocations")]
public sealed class ModelInvocations : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table if not exists model_invocations (
            id text primary key, call_id text not null, attempt integer not null,
            tenant_id text not null, workspace_id text not null, session_id text not null,
            run_id text not null, turn_id text null, agent_instance_id text null,
            agent_definition_id text not null, agent_version_id text not null, mode_version_id text not null,
            strategy_source text not null, route_id text null, route_version_id text null,
            provider_instance_id text not null, provider_version_id text not null,
            model text null, protocol text not null, fallback_position integer not null,
            status text not null, error_category text null, safe_error_message text null,
            input_tokens integer null, output_tokens integer null, total_tokens integer null,
            started_at text not null, completed_at text null
        );
        create unique index if not exists ix_model_invocations_call_attempt on model_invocations(call_id, attempt);
        create index if not exists ix_model_invocations_run_started on model_invocations(tenant_id, workspace_id, run_id, started_at);
        create index if not exists ix_model_invocations_agent_started on model_invocations(tenant_id, workspace_id, agent_definition_id, started_at);
        create index if not exists ix_model_invocations_mode_started on model_invocations(tenant_id, workspace_id, mode_version_id, started_at);
        create index if not exists ix_model_invocations_provider_model_started on model_invocations(tenant_id, workspace_id, provider_instance_id, model, started_at);
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("drop table if exists model_invocations;");
}

[DbContext(typeof(AgentControlDbContext))]
[Migration("202608270004_RetireAgentProfiles")]
public sealed class RetireAgentProfiles : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        drop table if exists agent_profile_versions;
        drop table if exists agent_profiles;
        """);

    protected override void Down(MigrationBuilder m) { }
}

[DbContext(typeof(MemoryDbContext))]
[Migration("202608270005_StructuredMeetingModelOverride")]
public sealed class StructuredMeetingModelOverride : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table sessions add column meeting_model_override_provider_instance_id text null;
        alter table sessions add column meeting_model_override_model text null;
        """);

    protected override void Down(MigrationBuilder m) { }
}
