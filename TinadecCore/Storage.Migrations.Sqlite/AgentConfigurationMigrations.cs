using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Storage.Migrations.Sqlite;

/// <summary>
/// Isolated agent configuration migration. Covers formal agents, modes, prompts and versions.
/// Does NOT reuse legacy agent_instances/agent_profiles. Tables are created idempotently;
/// if migrations lag, <see cref="TinadecCore.Persistence.DbContextSchemaBootstrapper"/> also
/// creates missing tables from the model on first startup. PG counterpart lives in the
/// PostgreSql migration project.
/// </summary>
[DbContext(typeof(AgentConfigurationDbContext))]
[Migration("202608220001_AgentConfiguration")]
public sealed class AgentConfiguration : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table if not exists agent_definitions (
            id text primary key,
            tenant_id text not null, workspace_id text not null,
            slug text not null, display_name text not null,
            layer text not null, role text not null,
            capabilities_json text null, base_prompt_pipeline_id text null,
            model_strategy_json text null, tool_scope_json text null,
            status text not null, revision integer not null, version integer not null,
            created_at text not null, updated_at text not null, archived_at text null,
            created_by_principal_id text not null, updated_by_principal_id text not null
        );
        create unique index if not exists ix_agent_definitions_slug_draft on agent_definitions(tenant_id, workspace_id, slug) where status = 'draft';
        create index if not exists ix_agent_definitions_tenant_status on agent_definitions(tenant_id, workspace_id, status, updated_at);
        create index if not exists ix_agent_definitions_layer_status on agent_definitions(tenant_id, workspace_id, layer, status);

        create table if not exists agent_versions (
            id text primary key,
            tenant_id text not null, workspace_id text not null,
            agent_definition_id text not null, version integer not null,
            layer text not null, role text not null,
            snapshot_json text not null, content_hash text null, content_length integer not null,
            status text not null, revision integer not null, created_at text not null, created_by_principal_id text not null
        );
        create unique index if not exists ix_agent_versions_def_version on agent_versions(agent_definition_id, version);
        create index if not exists ix_agent_versions_tenant_def_created on agent_versions(tenant_id, workspace_id, agent_definition_id, created_at);

        create table if not exists agent_modes (
            id text primary key,
            tenant_id text not null, workspace_id text not null,
            slug text not null, display_name text not null, description text null,
            status text not null, revision integer not null, version integer not null,
            created_at text not null, updated_at text not null, archived_at text null,
            created_by_principal_id text not null, updated_by_principal_id text not null
        );
        create unique index if not exists ix_agent_modes_slug_draft on agent_modes(tenant_id, workspace_id, slug) where status = 'draft';
        create index if not exists ix_agent_modes_tenant_status on agent_modes(tenant_id, workspace_id, status, updated_at);

        create table if not exists mode_versions (
            id text primary key,
            tenant_id text not null, workspace_id text not null,
            agent_mode_id text not null, version integer not null,
            snapshot_json text null, topology_hash text null, warning_json text null,
            status text not null, revision integer not null, created_at text not null, created_by_principal_id text not null
        );
        create unique index if not exists ix_mode_versions_mode_version on mode_versions(agent_mode_id, version);
        create index if not exists ix_mode_versions_tenant_mode_created on mode_versions(tenant_id, workspace_id, agent_mode_id, created_at);

        create table if not exists mode_nodes (
            id text primary key,
            tenant_id text not null, workspace_id text not null,
            mode_id text not null, node_key text not null, agent_definition_id text not null,
            layer text not null, label text null, position_json text null, config_json text null,
            status text not null, revision integer not null,
            created_at text not null, updated_at text not null, archived_at text null
        );
        create unique index if not exists ix_mode_nodes_mode_key_draft on mode_nodes(tenant_id, workspace_id, mode_id, node_key) where status = 'draft';
        create index if not exists ix_mode_nodes_tenant_mode_status on mode_nodes(tenant_id, workspace_id, mode_id, status);

        create table if not exists mode_edges (
            id text primary key,
            tenant_id text not null, workspace_id text not null,
            mode_id text not null, edge_key text not null,
            source_node_key text not null, target_node_key text not null, condition_json text null,
            status text not null, revision integer not null,
            created_at text not null, updated_at text not null, archived_at text null
        );
        create unique index if not exists ix_mode_edges_mode_key_draft on mode_edges(tenant_id, workspace_id, mode_id, edge_key) where status = 'draft';
        create index if not exists ix_mode_edges_tenant_mode_status on mode_edges(tenant_id, workspace_id, mode_id, status);

        create table if not exists canvas_layouts (
            id text primary key,
            tenant_id text not null, workspace_id text not null,
            mode_id text not null, layout_json text not null,
            status text not null, revision integer not null,
            created_at text not null, updated_at text not null, archived_at text null
        );
        create unique index if not exists ix_canvas_layouts_mode_draft on canvas_layouts(tenant_id, workspace_id, mode_id) where status = 'draft';
        create index if not exists ix_canvas_layouts_tenant_status on canvas_layouts(tenant_id, workspace_id, status, updated_at);

        create table if not exists prompt_pipelines (
            id text primary key,
            tenant_id text not null, workspace_id text not null,
            slug text not null, display_name text not null, description text null,
            graph_json text not null,
            status text not null, revision integer not null, version integer not null,
            created_at text not null, updated_at text not null, archived_at text null,
            created_by_principal_id text not null, updated_by_principal_id text not null
        );
        create unique index if not exists ix_prompt_pipelines_slug_draft on prompt_pipelines(tenant_id, workspace_id, slug) where status = 'draft';
        create index if not exists ix_prompt_pipelines_tenant_status on prompt_pipelines(tenant_id, workspace_id, status, updated_at);

        create table if not exists prompt_versions (
            id text primary key,
            tenant_id text not null, workspace_id text not null,
            prompt_pipeline_id text not null, version integer not null,
            graph_json text not null, content_hash text null, content_length integer not null,
            status text not null, revision integer not null, created_at text not null, created_by_principal_id text not null
        );
        create unique index if not exists ix_prompt_versions_pipeline_version on prompt_versions(prompt_pipeline_id, version);
        create index if not exists ix_prompt_versions_tenant_pipeline_created on prompt_versions(tenant_id, workspace_id, prompt_pipeline_id, created_at);

        create table if not exists prompt_nodes (
            id text primary key,
            tenant_id text not null, workspace_id text not null,
            prompt_pipeline_id text not null, node_key text not null,
            kind text not null, config_json text not null,
            status text not null, revision integer not null,
            created_at text not null, updated_at text not null, archived_at text null
        );
        create unique index if not exists ix_prompt_nodes_pipeline_key_draft on prompt_nodes(tenant_id, workspace_id, prompt_pipeline_id, node_key) where status = 'draft';
        create index if not exists ix_prompt_nodes_tenant_pipeline_status on prompt_nodes(tenant_id, workspace_id, prompt_pipeline_id, status);

        create table if not exists workspace_defaults (
            tenant_id text not null, workspace_id text not null,
            default_agent_definition_id text null, default_agent_mode_id text null, default_prompt_pipeline_id text null,
            status text not null, revision integer not null,
            created_at text not null, updated_at text not null, archived_at text null,
            primary key (tenant_id, workspace_id)
        );
        create index if not exists ix_workspace_defaults_status on workspace_defaults(tenant_id, workspace_id, status);
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("""
        drop table if exists workspace_defaults;
        drop table if exists prompt_nodes;
        drop table if exists prompt_versions;
        drop table if exists prompt_pipelines;
        drop table if exists canvas_layouts;
        drop table if exists mode_edges;
        drop table if exists mode_nodes;
        drop table if exists mode_versions;
        drop table if exists agent_modes;
        drop table if exists agent_versions;
        drop table if exists agent_definitions;
        """);
}
