using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.AgentConfiguration;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Models;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(AgentConfigurationDbContext))]
[Migration("202608270001_AgentModelDirectory")]
public sealed class AgentModelDirectory : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table agent_definitions add column if not exists source_kind varchar(32) not null default 'custom';
        alter table agent_definitions add column if not exists source_key varchar(512) not null default '';
        alter table agent_definitions add column if not exists managed boolean not null default false;
        update agent_definitions set source_key = slug where source_key = '';
        update agent_definitions a
        set source_kind = 'pack', managed = true, source_key = r.installation_id::text || ':' || r.resource_key
        from agent_pack_managed_resources r
        where r.resource_kind = 'agent' and r.logical_entity_id = a.id;
        create unique index if not exists ix_agent_definitions_source_identity
            on agent_definitions(tenant_id, workspace_id, source_kind, source_key);
        alter table mode_nodes add column if not exists model_strategy_override_json text null;
        """);

    protected override void Down(MigrationBuilder m) { }
}

[DbContext(typeof(ModelControlDbContext))]
[Migration("202608270002_ModelRoutesV2")]
public sealed class ModelRoutesV2 : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table if not exists model_provider_instances (
            id uuid primary key, tenant_id uuid not null, workspace_id uuid null, project_id uuid null,
            scope varchar(32) not null, driver varchar(128) not null, display_name varchar(256) not null,
            connection_kind varchar(64) not null, secret_reference varchar(512) null, enabled boolean not null,
            revision bigint not null, current_version_id uuid not null, created_by_principal_id uuid not null,
            updated_by_principal_id uuid not null, created_at timestamptz not null, updated_at timestamptz not null,
            deleted_at timestamptz null);
        create table if not exists model_provider_versions (
            id uuid primary key, provider_id uuid not null, version integer not null,
            content_reference varchar(1024) not null, content_hash varchar(128) not null, content_length bigint not null,
            created_by_principal_id uuid not null, created_at timestamptz not null);
        create unique index if not exists ix_model_provider_versions_provider_version on model_provider_versions(provider_id, version);
        create table if not exists model_routes (
            id uuid primary key, tenant_id uuid not null, workspace_id uuid null, project_id uuid null,
            scope varchar(32) not null, purpose varchar(128) not null, revision bigint not null, current_version_id uuid not null,
            created_by_principal_id uuid not null, updated_by_principal_id uuid not null,
            created_at timestamptz not null, updated_at timestamptz not null, deleted_at timestamptz null);
        create table if not exists model_route_versions (
            id uuid primary key, route_id uuid not null, version integer not null,
            provider_id uuid null, model varchar(512) null, created_by_principal_id uuid not null, created_at timestamptz not null);
        create unique index if not exists ix_model_route_versions_route_version on model_route_versions(route_id, version);
        create table if not exists model_route_candidates (
            id uuid primary key,
            route_version_id uuid not null,
            position integer not null,
            provider_instance_id uuid not null,
            model varchar(512) null
        );
        create unique index if not exists ix_model_route_candidates_version_position
            on model_route_candidates(route_version_id, position);
        create index if not exists ix_model_route_candidates_provider_model
            on model_route_candidates(provider_instance_id, model);
        insert into model_route_candidates(id, route_version_id, position, provider_instance_id, model)
        select gen_random_uuid(), id, 0, provider_id, model from model_route_versions
        where provider_id is not null
        on conflict (route_version_id, position) do nothing;
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("drop table if exists model_route_candidates;");
}

[DbContext(typeof(LifecycleDbContext))]
[Migration("202608270003_ModelInvocations")]
public sealed class ModelInvocations : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table if not exists model_invocations (
            id uuid primary key, call_id uuid not null, attempt integer not null,
            tenant_id uuid not null, workspace_id uuid not null, session_id uuid not null,
            run_id uuid not null, turn_id uuid null, agent_instance_id uuid null,
            agent_definition_id uuid not null, agent_version_id uuid not null, mode_version_id uuid not null,
            strategy_source varchar(64) not null, route_id uuid null, route_version_id uuid null,
            provider_instance_id uuid not null, provider_version_id uuid not null,
            model varchar(512) null, protocol varchar(64) not null, fallback_position integer not null,
            status varchar(32) not null, error_category varchar(128) null, safe_error_message varchar(4096) null,
            input_tokens bigint null, output_tokens bigint null, total_tokens bigint null,
            started_at timestamptz not null, completed_at timestamptz null
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
        alter table sessions add column if not exists meeting_model_override_provider_instance_id uuid null;
        alter table sessions add column if not exists meeting_model_override_model text null;
        alter table sessions drop column if exists meeting_model;
        alter table sessions drop column if exists meeting_provider_id;
        """);

    protected override void Down(MigrationBuilder m) { }
}
