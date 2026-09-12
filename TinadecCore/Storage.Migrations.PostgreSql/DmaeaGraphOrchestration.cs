using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Storage.Migrations.PostgreSql;

/// <summary>
/// DmaEA graph orchestration (Phase 1): agent_templates(_versions), mode_bindings,
/// tool_definitions(_versions). Shipped in the same batch as the SQLite counterpart;
/// DbContextSchemaBootstrapper remains the additive fallback if migrations lag.
/// </summary>
[DbContext(typeof(AgentConfigurationDbContext))]
[Migration("202609120001_DmaeaGraphOrchestration")]
public sealed class DmaeaGraphOrchestration : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table if not exists agent_templates (
            id uuid primary key,
            tenant_id uuid not null, workspace_id uuid not null,
            slug varchar(128) not null, display_name varchar(256) not null,
            layer varchar(32) not null, role varchar(128) not null,
            capabilities_json text null, model_strategy_json text null, default_tool_scope_json text null,
            description text null,
            source_kind varchar(32) not null, source_key varchar(512) not null,
            managed boolean not null, enabled boolean not null,
            status varchar(32) not null, revision bigint not null, version integer not null,
            created_at timestamptz not null, updated_at timestamptz not null, archived_at timestamptz null,
            created_by_principal_id uuid not null, updated_by_principal_id uuid not null
        );
        create unique index if not exists ix_agent_templates_slug_draft on agent_templates(tenant_id, workspace_id, slug) where status = 'draft';
        create index if not exists ix_agent_templates_tenant_status on agent_templates(tenant_id, workspace_id, status, updated_at);
        create index if not exists ix_agent_templates_source on agent_templates(tenant_id, workspace_id, source_kind, source_key);

        create table if not exists agent_template_versions (
            id uuid primary key,
            tenant_id uuid not null, workspace_id uuid not null,
            agent_template_id uuid not null, version integer not null,
            snapshot_json text not null, content_hash varchar(128) null, content_length bigint not null,
            status varchar(32) not null, revision bigint not null,
            created_at timestamptz not null, created_by_principal_id uuid not null
        );
        create unique index if not exists ix_agent_template_versions_template_version on agent_template_versions(agent_template_id, version);
        create index if not exists ix_agent_template_versions_tenant_created on agent_template_versions(tenant_id, workspace_id, agent_template_id, created_at);

        create table if not exists mode_bindings (
            id uuid primary key,
            tenant_id uuid not null, workspace_id uuid not null,
            mode_id uuid not null, mode_version_id uuid null,
            node_key varchar(128) not null, agent_template_id uuid not null, agent_template_slug varchar(128) null,
            duty_description_ref varchar(512) null, tool_switches_json text null, envelope_json text null,
            includes_core_reserved boolean not null, instance_naming_json text null,
            status varchar(32) not null, revision bigint not null,
            created_at timestamptz not null, updated_at timestamptz not null, archived_at timestamptz null
        );
        create unique index if not exists ix_mode_bindings_mode_node_draft on mode_bindings(tenant_id, workspace_id, mode_id, node_key) where status = 'draft';
        create index if not exists ix_mode_bindings_tenant_mode_status on mode_bindings(tenant_id, workspace_id, mode_id, status);

        create table if not exists tool_definitions (
            id uuid primary key,
            tenant_id uuid not null, workspace_id uuid not null,
            slug varchar(128) not null, display_name varchar(256) not null, description text null,
            status varchar(32) not null, revision bigint not null, version integer not null,
            created_at timestamptz not null, updated_at timestamptz not null, archived_at timestamptz null,
            created_by_principal_id uuid not null, updated_by_principal_id uuid not null
        );
        create unique index if not exists ix_tool_definitions_slug_draft on tool_definitions(tenant_id, workspace_id, slug) where status = 'draft';
        create index if not exists ix_tool_definitions_tenant_status on tool_definitions(tenant_id, workspace_id, status, updated_at);

        create table if not exists tool_definition_versions (
            id uuid primary key,
            tenant_id uuid not null, workspace_id uuid not null,
            tool_definition_id uuid not null, version integer not null,
            definition_json text not null, entry_hash varchar(128) null, content_hash varchar(128) null, content_length bigint not null,
            status varchar(32) not null, revision bigint not null,
            created_at timestamptz not null, created_by_principal_id uuid not null
        );
        create unique index if not exists ix_tool_definition_versions_definition_version on tool_definition_versions(tool_definition_id, version);
        create index if not exists ix_tool_definition_versions_tenant_created on tool_definition_versions(tenant_id, workspace_id, tool_definition_id, created_at);
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("""
        drop table if exists tool_definition_versions;
        drop table if exists tool_definitions;
        drop table if exists mode_bindings;
        drop table if exists agent_template_versions;
        drop table if exists agent_templates;
        """);
}
