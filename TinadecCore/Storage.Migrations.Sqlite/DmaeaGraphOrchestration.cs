using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Storage.Migrations.Sqlite;

/// <summary>
/// DmaEA graph orchestration (Phase 1): agent_templates(_versions), mode_bindings,
/// tool_definitions(_versions). Tables are created idempotently; if migrations lag,
/// DbContextSchemaBootstrapper also creates missing tables/columns from the model on
/// first startup. PG counterpart lives in the PostgreSql migration project (same batch).
/// </summary>
[DbContext(typeof(AgentConfigurationDbContext))]
[Migration("202609120001_DmaeaGraphOrchestration")]
public sealed class DmaeaGraphOrchestration : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table if not exists agent_templates (
            id text primary key,
            tenant_id text not null, workspace_id text not null,
            slug text not null, display_name text not null,
            layer text not null, role text not null,
            capabilities_json text null, model_strategy_json text null, default_tool_scope_json text null,
            description text null,
            source_kind text not null, source_key text not null,
            managed integer not null, enabled integer not null,
            status text not null, revision integer not null, version integer not null,
            created_at text not null, updated_at text not null, archived_at text null,
            created_by_principal_id text not null, updated_by_principal_id text not null
        );
        create unique index if not exists ix_agent_templates_slug_draft on agent_templates(tenant_id, workspace_id, slug) where status = 'draft';
        create index if not exists ix_agent_templates_tenant_status on agent_templates(tenant_id, workspace_id, status, updated_at);
        create index if not exists ix_agent_templates_source on agent_templates(tenant_id, workspace_id, source_kind, source_key);

        create table if not exists agent_template_versions (
            id text primary key,
            tenant_id text not null, workspace_id text not null,
            agent_template_id text not null, version integer not null,
            snapshot_json text not null, content_hash text null, content_length integer not null,
            status text not null, revision integer not null,
            created_at text not null, created_by_principal_id text not null
        );
        create unique index if not exists ix_agent_template_versions_template_version on agent_template_versions(agent_template_id, version);
        create index if not exists ix_agent_template_versions_tenant_created on agent_template_versions(tenant_id, workspace_id, agent_template_id, created_at);

        create table if not exists mode_bindings (
            id text primary key,
            tenant_id text not null, workspace_id text not null,
            mode_id text not null, mode_version_id text null,
            node_key text not null, agent_template_id text not null, agent_template_slug text null,
            duty_description_ref text null, tool_switches_json text null, envelope_json text null,
            includes_core_reserved integer not null, instance_naming_json text null,
            status text not null, revision integer not null,
            created_at text not null, updated_at text not null, archived_at text null
        );
        create unique index if not exists ix_mode_bindings_mode_node_draft on mode_bindings(tenant_id, workspace_id, mode_id, node_key) where status = 'draft';
        create index if not exists ix_mode_bindings_tenant_mode_status on mode_bindings(tenant_id, workspace_id, mode_id, status);

        create table if not exists tool_definitions (
            id text primary key,
            tenant_id text not null, workspace_id text not null,
            slug text not null, display_name text not null, description text null,
            status text not null, revision integer not null, version integer not null,
            created_at text not null, updated_at text not null, archived_at text null,
            created_by_principal_id text not null, updated_by_principal_id text not null
        );
        create unique index if not exists ix_tool_definitions_slug_draft on tool_definitions(tenant_id, workspace_id, slug) where status = 'draft';
        create index if not exists ix_tool_definitions_tenant_status on tool_definitions(tenant_id, workspace_id, status, updated_at);

        create table if not exists tool_definition_versions (
            id text primary key,
            tenant_id text not null, workspace_id text not null,
            tool_definition_id text not null, version integer not null,
            definition_json text not null, entry_hash text null, content_hash text null, content_length integer not null,
            status text not null, revision integer not null,
            created_at text not null, created_by_principal_id text not null
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
