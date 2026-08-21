using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;
using TinadecCore.Models;
using TinadecCore.Persistence;

namespace TinadecCore.Runtime;

/// <summary>
/// Development bootstrap: idempotently creates a <c>chat</c> model route with an
/// OpenAI provider and the default planner/executor agent pair when none exist for
/// the current tenant workspace. Runs only when the configuration is missing, so
/// production control-plane writes are never overwritten. The provider is created
/// without a stored API key — real model calls require configuring the key first.
/// </summary>
public static class DevSeed
{
    public static async Task SeedIfMissingAsync(IServiceProvider services, CancellationToken ct)
    {
        var tenant = services.GetRequiredService<ITenantContextAccessor>().Current;
        await using (var models = await services.GetRequiredService<IDbContextFactory<ModelControlDbContext>>().CreateDbContextAsync(ct))
        {
            var chatRoute = await models.Routes.AsNoTracking().SingleOrDefaultAsync(r => r.Purpose == "chat" && r.TenantId == tenant.TenantId && r.WorkspaceId == tenant.WorkspaceId && r.DeletedAt == null, ct);
            if (chatRoute is not null) return;
            var now = DateTimeOffset.UtcNow;
            var provider = new ModelProviderRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId,
                WorkspaceId = tenant.WorkspaceId,
                Driver = "openai",
                DisplayName = "OpenAI (dev default)",
                Scope = "workspace",
                ConnectionKind = "api-key",
                Enabled = true,
                Revision = 0,
                CreatedByPrincipalId = tenant.PrincipalId,
                CreatedAt = now
            };
            var content = services.GetRequiredService<IContentStore>();
            var configJson = JsonSerializer.Serialize(new Dictionary<string, object> { ["base_url"] = "https://api.openai.com/v1", ["model"] = "gpt-4o-mini", ["capabilities"] = new[] { "chat" } });
            await using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(configJson)))
            {
                var stored = await content.PutAsync(new ContentWriteRequest(tenant.TenantId, tenant.WorkspaceId, "model-config", "application/json", ms), ct);
                var providerVersion = new ModelProviderVersionRecord
                {
                    Id = Guid.NewGuid(),
                    ProviderId = provider.Id,
                    Version = 1,
                    ContentReference = stored.Value,
                    ContentHash = stored.Sha256,
                    ContentLength = stored.Length,
                    CreatedByPrincipalId = tenant.PrincipalId,
                    CreatedAt = now
                };
                provider.CurrentVersionId = providerVersion.Id;
                models.Providers.Add(provider);
                models.ProviderVersions.Add(providerVersion);
            }
            var route = new ModelRouteRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId,
                WorkspaceId = tenant.WorkspaceId,
                Purpose = "chat",
                Scope = "workspace",
                Revision = 0,
                CreatedByPrincipalId = tenant.PrincipalId,
                CreatedAt = now
            };
            var routeVersion = new ModelRouteVersionRecord
            {
                Id = Guid.NewGuid(),
                RouteId = route.Id,
                Version = 1,
                ProviderId = provider.Id,
                Model = "gpt-4o-mini",
                CreatedByPrincipalId = tenant.PrincipalId,
                CreatedAt = now
            };
            route.CurrentVersionId = routeVersion.Id;
            models.Routes.Add(route);
            models.RouteVersions.Add(routeVersion);
            await models.SaveChangesAsync(ct);
        }

        await using (var agentsDb = await services.GetRequiredService<IDbContextFactory<AgentControlDbContext>>().CreateDbContextAsync(ct))
        {
            var existing = await agentsDb.Agents.AsNoTracking().AnyAsync(a => a.TenantId == tenant.TenantId && a.WorkspaceId == tenant.WorkspaceId && a.DeletedAt == null, ct);
            if (existing) return;
            var now = DateTimeOffset.UtcNow;
            var content = services.GetRequiredService<IContentStore>();
            var agentDefs = new[]
            {
                new { name = "planner", layer = "planning", agent_type = "planner", system_prompt = "You are the planning layer agent. Break the user goal into executable subtasks.", model_route_purpose = "chat" },
                new { name = "executor", layer = "execution", agent_type = "executor", system_prompt = "You are the execution layer agent. Execute the assigned task and output a completion summary.", model_route_purpose = "chat" }
            };
            foreach (var def in agentDefs)
            {
                var agentRow = new AgentProfileRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.TenantId,
                    WorkspaceId = tenant.WorkspaceId,
                    Scope = "workspace",
                    Name = def.name,
                    Layer = def.layer,
                    AgentType = def.agent_type,
                    Enabled = true,
                    Revision = 0,
                    CreatedByPrincipalId = tenant.PrincipalId,
                    CreatedAt = now
                };
                var agentBody = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["mode"] = "default",
                    ["description"] = def.name,
                    ["model_route_purpose"] = def.model_route_purpose,
                    ["allowed_tools"] = Array.Empty<string>(),
                    ["capabilities"] = Array.Empty<string>(),
                    ["system_prompt"] = def.system_prompt
                });
                await using (var ms2 = new MemoryStream(Encoding.UTF8.GetBytes(agentBody)))
                {
                    var stored2 = await content.PutAsync(new ContentWriteRequest(tenant.TenantId, tenant.WorkspaceId, "agent-profile", "application/json", ms2), ct);
                    var agentVersion = new AgentProfileVersionRecord
                    {
                        Id = Guid.NewGuid(),
                        AgentId = agentRow.Id,
                        Version = 1,
                        ContentReference = stored2.Value,
                        ContentHash = stored2.Sha256,
                        ContentLength = stored2.Length,
                        CreatedByPrincipalId = tenant.PrincipalId,
                        CreatedAt = now
                    };
                    agentRow.CurrentVersionId = agentVersion.Id;
                    agentsDb.Agents.Add(agentRow);
                    agentsDb.Versions.Add(agentVersion);
                }
            }
            await agentsDb.SaveChangesAsync(ct);
        }

        // Formal isolated configuration baseline (new spec): meeting + execution + prompt + mode
        await using (var cfgDb = await services.GetRequiredService<IDbContextFactory<AgentConfiguration.AgentConfigurationDbContext>>().CreateDbContextAsync(ct))
        {
            var hasFormal = await cfgDb.AgentDefinitions.AsNoTracking().AnyAsync(a => a.TenantId == tenant.TenantId && a.WorkspaceId == tenant.WorkspaceId, ct);
            if (!hasFormal)
            {
                var now2 = DateTimeOffset.UtcNow;

                // Formal space.full_duplex agent set (see docs/双层智能体架构.md + docs/全双工智能体配置文档.md).
                // Operation: meeting (only user entry), context_compressor, skill_recommender, supervisor, evolution.
                // Execution: task_planner (coordinator) + worker pool specializations.
                var meeting = NewAgent("meeting", "会议智能体", "operation", "session_coordinator", "[\"user.respond\",\"task.dispatch\",\"agent.create_temporary\",\"agent.create_persistent\",\"agent.create_profile\"]", "[\"*\"]");
                var contextCompressor = NewAgent("context_compressor", "上下文压缩智能体", "operation", "context_maintenance", "[\"context.read\",\"context.patch\"]", "[\"*\"]");
                var skillRecommender = NewAgent("skill_recommender", "技能推荐智能体", "operation", "capability_advisor", "[\"tool.search\",\"agent.propose\"]", "[\"*\"]");
                var supervisor = NewAgent("supervisor", "监督智能体", "operation", "quality_controller", "[\"supervision.review\"]", "[\"*\"]");
                var evolution = NewAgent("evolution", "进化智能体", "operation", "experience_curator", "[\"memory.candidate\",\"agent.candidate\",\"agent.create_persistent\"]", "[\"*\"]");
                var taskPlanner = NewAgent("task_planner", "任务规划智能体", "execution", "execution_coordinator", "[\"task.plan\",\"task.replan\",\"agent.create_temporary\"]", "[\"*\"]");
                var codeWorker = NewAgent("worker.code", "代码执行智能体", "execution", "task_executor", "[\"tool.code\",\"tool.file\"]", "[\"write_file\",\"read_file\",\"shell.execute\",\"mcp_invoke\"]");
                var documentWorker = NewAgent("worker.document", "文档生成智能体", "execution", "task_executor", "[\"tool.document\"]", "[\"write_file\",\"read_file\"]");
                var dataWorker = NewAgent("worker.data", "数据处理智能体", "execution", "task_executor", "[\"tool.data\"]", "[\"read_file\",\"shell.execute\"]");
                var browserWorker = NewAgent("worker.browser", "浏览器检索智能体", "execution", "task_executor", "[\"tool.search\",\"tool.browser\"]", "[\"browser.search\",\"browser.fetch\",\"mcp_search\",\"mcp_invoke\"]");
                var fileWorker = NewAgent("worker.file", "文件操作智能体", "execution", "task_executor", "[\"tool.file\"]", "[\"write_file\",\"read_file\"]");
                var generalWorker = NewAgent("worker.general", "通用执行智能体", "execution", "task_executor", "[\"task.execute\"]", "[\"*\"]");
                var agents = new[] { meeting, contextCompressor, skillRecommender, supervisor, evolution, taskPlanner, codeWorker, documentWorker, dataWorker, browserWorker, fileWorker, generalWorker };
                cfgDb.AgentDefinitions.AddRange(agents);
                foreach (var a in agents)
                {
                    var snap = JsonSerializer.Serialize(new { id = a.Id, slug = a.Slug, display_name = a.DisplayName, layer = a.Layer, role = a.Role, model_strategy = new { kind = "inherit" } });
                    cfgDb.AgentVersions.Add(new AgentConfiguration.AgentVersionRecord { Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, AgentDefinitionId = a.Id, Version = 1, Layer = a.Layer, Role = a.Role, SnapshotJson = snap, ContentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(snap))).ToLowerInvariant(), ContentLength = snap.Length, Status = "published", Revision = 1, CreatedAt = now2, CreatedByPrincipalId = tenant.PrincipalId });
                }
                var pipeline = new AgentConfiguration.PromptPipelineRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, Slug = "baseline-prompt", DisplayName = "Baseline Prompt", GraphJson = "{\"nodes\":[{\"id\":\"template\",\"type\":\"template\"},{\"id\":\"assemble\",\"type\":\"assemble\"}],\"edges\":[{\"source\":\"template\",\"target\":\"assemble\"}]}",
                    Status = "published", Revision = 1, Version = 1, CreatedAt = now2, UpdatedAt = now2, CreatedByPrincipalId = tenant.PrincipalId, UpdatedByPrincipalId = tenant.PrincipalId
                };
                cfgDb.PromptPipelines.Add(pipeline);
                var pSnap = pipeline.GraphJson;
                cfgDb.PromptVersions.Add(new AgentConfiguration.PromptVersionRecord { Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, PromptPipelineId = pipeline.Id, Version = 1, GraphJson = pSnap, ContentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(pSnap))).ToLowerInvariant(), ContentLength = pSnap.Length, Status = "published", Revision = 1, CreatedAt = now2, CreatedByPrincipalId = tenant.PrincipalId });
                var mode = new AgentConfiguration.AgentModeRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, Slug = "default-mode", DisplayName = "Default Mode (baseline)", Description = "space.full_duplex baseline",
                    Status = "published", Revision = 1, Version = 1, CreatedAt = now2, UpdatedAt = now2, CreatedByPrincipalId = tenant.PrincipalId, UpdatedByPrincipalId = tenant.PrincipalId
                };
                cfgDb.AgentModes.Add(mode);
                var opNodes = new[] { meeting, contextCompressor, skillRecommender, supervisor, evolution };
                var exNodes = new[] { taskPlanner, codeWorker, documentWorker, dataWorker, browserWorker, fileWorker, generalWorker };
                var allNodes = opNodes.Select((a, i) => (NodeKey: $"meeting-{i + 1}", Agent: a)).Concat(exNodes.Select((a, i) => (NodeKey: $"executor-{i + 1}", Agent: a))).ToArray();
                foreach (var (key, a) in allNodes)
                    cfgDb.ModeNodes.Add(new AgentConfiguration.ModeNodeRecord { Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, ModeId = mode.Id, NodeKey = key, AgentDefinitionId = a.Id, Layer = a.Layer, Label = a.DisplayName, Status = "published", Revision = 1, CreatedAt = now2, UpdatedAt = now2 });
                var modeSnap = JsonSerializer.Serialize(new { mode_id = mode.Id, nodes = allNodes.Select(n => new { key = n.NodeKey, agent = n.Agent.Id, layer = n.Agent.Layer }) });
                cfgDb.ModeVersions.Add(new AgentConfiguration.ModeVersionRecord { Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, AgentModeId = mode.Id, Version = 1, SnapshotJson = modeSnap, TopologyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(modeSnap))).ToLowerInvariant(), Status = "published", Revision = 1, CreatedAt = now2, CreatedByPrincipalId = tenant.PrincipalId });
                cfgDb.WorkspaceDefaults.Add(new AgentConfiguration.WorkspaceDefaultsRecord { TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, DefaultAgentDefinitionId = meeting.Id, DefaultAgentModeId = mode.Id, DefaultPromptPipelineId = pipeline.Id, Status = "active", Revision = 1, CreatedAt = now2, UpdatedAt = now2 });
                await cfgDb.SaveChangesAsync(ct);

                AgentConfiguration.AgentDefinitionRecord NewAgent(string slug, string display, string layer, string role, string caps, string tools) => new()
                {
                    Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, Slug = slug, DisplayName = display, Layer = layer, Role = role,
                    CapabilitiesJson = caps, ModelStrategyJson = "{\"kind\":\"inherit\"}", ToolScopeJson = tools,
                    Status = "published", Revision = 1, Version = 1, CreatedAt = now2, UpdatedAt = now2, CreatedByPrincipalId = tenant.PrincipalId, UpdatedByPrincipalId = tenant.PrincipalId
                };
            }
        }
    }
}
