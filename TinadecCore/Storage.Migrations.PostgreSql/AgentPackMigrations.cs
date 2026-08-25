using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Storage.Migrations.PostgreSql;

[DbContext(typeof(AgentConfigurationDbContext))]
[Migration("202608250001_AgentPacks")]
public sealed class AgentPacks : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table workspace_defaults add column if not exists default_agent_version_id uuid null;
        alter table workspace_defaults add column if not exists default_mode_version_id uuid null;
        alter table workspace_defaults add column if not exists default_prompt_version_id uuid null;

        create table if not exists agent_pack_installations (
            id uuid primary key, tenant_id uuid not null, workspace_id uuid not null,
            pack_id varchar(256) not null, owner varchar(256) not null, product_id varchar(256) not null, name varchar(256) not null,
            active_version_id uuid null, status varchar(32) not null, revision bigint not null,
            created_at timestamptz not null, updated_at timestamptz not null,
            created_by_principal_id uuid not null, updated_by_principal_id uuid not null
        );
        create unique index if not exists ix_agent_pack_installations_scope_pack on agent_pack_installations(tenant_id, workspace_id, pack_id);
        create index if not exists ix_agent_pack_installations_scope_status on agent_pack_installations(tenant_id, workspace_id, status, updated_at);

        create table if not exists agent_pack_versions (
            id uuid primary key, tenant_id uuid not null, workspace_id uuid not null,
            installation_id uuid not null, pack_version varchar(64) not null,
            manifest_content_reference varchar(2048) not null, manifest_hash varchar(64) not null, manifest_length bigint not null,
            previous_version_id uuid null, created_at timestamptz not null, created_by_principal_id uuid not null
        );
        create unique index if not exists ix_agent_pack_versions_installation_version on agent_pack_versions(installation_id, pack_version);
        create index if not exists ix_agent_pack_versions_scope_created on agent_pack_versions(tenant_id, workspace_id, installation_id, created_at);

        create table if not exists agent_pack_managed_resources (
            id uuid primary key, tenant_id uuid not null, workspace_id uuid not null,
            installation_id uuid not null, resource_kind varchar(32) not null, resource_key varchar(256) not null,
            logical_entity_id uuid not null, created_at timestamptz not null
        );
        create unique index if not exists ix_agent_pack_managed_resources_pack_key on agent_pack_managed_resources(installation_id, resource_kind, resource_key);
        create unique index if not exists ix_agent_pack_managed_resources_logical on agent_pack_managed_resources(tenant_id, workspace_id, resource_kind, logical_entity_id);

        create table if not exists agent_pack_resource_bindings (
            id uuid primary key, tenant_id uuid not null, workspace_id uuid not null,
            pack_version_id uuid not null, resource_kind varchar(32) not null, resource_key varchar(256) not null,
            logical_entity_id uuid not null, version_id uuid not null, content_hash varchar(128) not null,
            disposition varchar(32) not null, created_at timestamptz not null
        );
        create unique index if not exists ix_agent_pack_resource_bindings_version_key on agent_pack_resource_bindings(pack_version_id, resource_kind, resource_key);
        create index if not exists ix_agent_pack_resource_bindings_scope_version on agent_pack_resource_bindings(tenant_id, workspace_id, version_id);

        create table if not exists agent_pack_previews (
            id uuid primary key, tenant_id uuid not null, workspace_id uuid not null, principal_id uuid not null,
            action varchar(32) not null, pack_id varchar(256) not null, owner varchar(256) not null, pack_version varchar(64) not null,
            manifest_content_reference varchar(2048) not null, manifest_hash varchar(64) not null, manifest_length bigint not null,
            base_installation_revision bigint not null, base_defaults_revision bigint null, target_pack_version_id uuid null,
            status varchar(32) not null, created_at timestamptz not null, expires_at timestamptz not null, consumed_at timestamptz null
        );
        create index if not exists ix_agent_pack_previews_scope_expiry on agent_pack_previews(tenant_id, workspace_id, principal_id, expires_at);

        create table if not exists agent_pack_default_adoptions (
            id uuid primary key, tenant_id uuid not null, workspace_id uuid not null,
            installation_id uuid not null, pack_version_id uuid not null,
            previous_agent_definition_id uuid null, previous_agent_version_id uuid null,
            previous_agent_mode_id uuid null, previous_mode_version_id uuid null,
            previous_prompt_pipeline_id uuid null, previous_prompt_version_id uuid null,
            applied_agent_definition_id uuid null, applied_agent_version_id uuid null,
            applied_agent_mode_id uuid null, applied_mode_version_id uuid null,
            applied_prompt_pipeline_id uuid null, applied_prompt_version_id uuid null,
            created_at timestamptz not null
        );
        create unique index if not exists ix_agent_pack_default_adoptions_version on agent_pack_default_adoptions(installation_id, pack_version_id);

        create table if not exists agent_pack_operations (
            id uuid primary key, tenant_id uuid not null, workspace_id uuid not null, principal_id uuid not null,
            idempotency_key varchar(256) not null, operation varchar(64) not null, request_hash varchar(64) not null,
            status_code integer not null, response_json text not null, created_at timestamptz not null
        );
        create unique index if not exists ix_agent_pack_operations_idempotency on agent_pack_operations(tenant_id, workspace_id, principal_id, operation, idempotency_key);
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("""
        drop table if exists agent_pack_operations;
        drop table if exists agent_pack_default_adoptions;
        drop table if exists agent_pack_previews;
        drop table if exists agent_pack_resource_bindings;
        drop table if exists agent_pack_managed_resources;
        drop table if exists agent_pack_versions;
        drop table if exists agent_pack_installations;
        alter table workspace_defaults drop column if exists default_prompt_version_id;
        alter table workspace_defaults drop column if exists default_mode_version_id;
        alter table workspace_defaults drop column if exists default_agent_version_id;
        """);
}
