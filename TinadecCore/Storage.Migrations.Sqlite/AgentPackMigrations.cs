using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Storage.Migrations.Sqlite;

[DbContext(typeof(AgentConfigurationDbContext))]
[Migration("202608250001_AgentPacks")]
public sealed class AgentPacks : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        alter table workspace_defaults add column default_agent_version_id text null;
        alter table workspace_defaults add column default_mode_version_id text null;
        alter table workspace_defaults add column default_prompt_version_id text null;

        create table if not exists agent_pack_installations (
            id text primary key, tenant_id text not null, workspace_id text not null,
            pack_id text not null, owner text not null, product_id text not null, name text not null,
            active_version_id text null, status text not null, revision integer not null,
            created_at text not null, updated_at text not null,
            created_by_principal_id text not null, updated_by_principal_id text not null
        );
        create unique index if not exists ix_agent_pack_installations_scope_pack on agent_pack_installations(tenant_id, workspace_id, pack_id);
        create index if not exists ix_agent_pack_installations_scope_status on agent_pack_installations(tenant_id, workspace_id, status, updated_at);

        create table if not exists agent_pack_versions (
            id text primary key, tenant_id text not null, workspace_id text not null,
            installation_id text not null, pack_version text not null,
            manifest_content_reference text not null, manifest_hash text not null, manifest_length integer not null,
            previous_version_id text null, created_at text not null, created_by_principal_id text not null
        );
        create unique index if not exists ix_agent_pack_versions_installation_version on agent_pack_versions(installation_id, pack_version);
        create index if not exists ix_agent_pack_versions_scope_created on agent_pack_versions(tenant_id, workspace_id, installation_id, created_at);

        create table if not exists agent_pack_managed_resources (
            id text primary key, tenant_id text not null, workspace_id text not null,
            installation_id text not null, resource_kind text not null, resource_key text not null,
            logical_entity_id text not null, created_at text not null
        );
        create unique index if not exists ix_agent_pack_managed_resources_pack_key on agent_pack_managed_resources(installation_id, resource_kind, resource_key);
        create unique index if not exists ix_agent_pack_managed_resources_logical on agent_pack_managed_resources(tenant_id, workspace_id, resource_kind, logical_entity_id);

        create table if not exists agent_pack_resource_bindings (
            id text primary key, tenant_id text not null, workspace_id text not null,
            pack_version_id text not null, resource_kind text not null, resource_key text not null,
            logical_entity_id text not null, version_id text not null, content_hash text not null,
            disposition text not null, created_at text not null
        );
        create unique index if not exists ix_agent_pack_resource_bindings_version_key on agent_pack_resource_bindings(pack_version_id, resource_kind, resource_key);
        create index if not exists ix_agent_pack_resource_bindings_scope_version on agent_pack_resource_bindings(tenant_id, workspace_id, version_id);

        create table if not exists agent_pack_previews (
            id text primary key, tenant_id text not null, workspace_id text not null, principal_id text not null,
            action text not null, pack_id text not null, owner text not null, pack_version text not null,
            manifest_content_reference text not null, manifest_hash text not null, manifest_length integer not null,
            base_installation_revision integer not null, base_defaults_revision integer null, target_pack_version_id text null,
            status text not null, created_at text not null, expires_at text not null, consumed_at text null
        );
        create index if not exists ix_agent_pack_previews_scope_expiry on agent_pack_previews(tenant_id, workspace_id, principal_id, expires_at);

        create table if not exists agent_pack_default_adoptions (
            id text primary key, tenant_id text not null, workspace_id text not null,
            installation_id text not null, pack_version_id text not null,
            previous_agent_definition_id text null, previous_agent_version_id text null,
            previous_agent_mode_id text null, previous_mode_version_id text null,
            previous_prompt_pipeline_id text null, previous_prompt_version_id text null,
            applied_agent_definition_id text null, applied_agent_version_id text null,
            applied_agent_mode_id text null, applied_mode_version_id text null,
            applied_prompt_pipeline_id text null, applied_prompt_version_id text null,
            created_at text not null
        );
        create unique index if not exists ix_agent_pack_default_adoptions_version on agent_pack_default_adoptions(installation_id, pack_version_id);

        create table if not exists agent_pack_operations (
            id text primary key, tenant_id text not null, workspace_id text not null, principal_id text not null,
            idempotency_key text not null, operation text not null, request_hash text not null,
            status_code integer not null, response_json text not null, created_at text not null
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
        alter table workspace_defaults drop column default_prompt_version_id;
        alter table workspace_defaults drop column default_mode_version_id;
        alter table workspace_defaults drop column default_agent_version_id;
        """);
}
