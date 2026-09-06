using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Storage.Migrations.Sqlite;

/// <summary>
/// 智能体运行时绑定（配置体验改造 A）：模型来源与工具范围的"用户覆盖记录"，
/// 与 agent definition 的 draft/publish 分离，pack 管理的智能体也可写。
/// </summary>
[DbContext(typeof(AgentConfigurationDbContext))]
[Migration("202609060002_AgentRuntimeBindings")]
public sealed class AgentRuntimeBindings : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table if not exists agent_runtime_bindings (
            agent_definition_id text primary key,
            tenant_id text not null, workspace_id text not null,
            mode text not null,
            provider_instance_id text null, model text null,
            tool_scope_override_json text null,
            revision integer not null,
            updated_at text not null, updated_by_principal_id text null
        );
        create index if not exists ix_agent_runtime_bindings_tenant_workspace_updated
            on agent_runtime_bindings(tenant_id, workspace_id, updated_at);
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("""
        drop table if exists agent_runtime_bindings;
        """);
}

/// <summary>
/// mode='route' 的绑定需要记住路由用途（model_routes.purpose）。复用 model 列会污染
/// 语义，所以单独加一列；0002 已发布，不改其迁移体。
/// </summary>
[DbContext(typeof(AgentConfigurationDbContext))]
[Migration("202609060003_AgentRuntimeBindingRoutePurpose")]
public sealed class AgentRuntimeBindingRoutePurpose : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql(
        "alter table agent_runtime_bindings add column route_purpose text null;");

    protected override void Down(MigrationBuilder m) => m.Sql(
        "alter table agent_runtime_bindings drop column route_purpose;");
}
