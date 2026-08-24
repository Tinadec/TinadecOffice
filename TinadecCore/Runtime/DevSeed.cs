using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("TinadecCore.Runtime.DevSeed");
        var tenant = services.GetRequiredService<ITenantContextAccessor>().Current;
        var seededChatRoute = false;
        var seededAgentProfiles = false;
        var seededFormalConfig = false;
        await using (var models = await services.GetRequiredService<IDbContextFactory<ModelControlDbContext>>().CreateDbContextAsync(ct))
        {
            var chatRoute = await models.Routes.AsNoTracking().SingleOrDefaultAsync(r => r.Purpose == "chat" && r.TenantId == tenant.TenantId && r.WorkspaceId == tenant.WorkspaceId && r.DeletedAt == null, ct);
            if (chatRoute is null)
            {
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
                seededChatRoute = true;
            }
        }

        await using (var agentsDb = await services.GetRequiredService<IDbContextFactory<AgentControlDbContext>>().CreateDbContextAsync(ct))
        {
            var existing = await agentsDb.Agents.AsNoTracking().AnyAsync(a => a.TenantId == tenant.TenantId && a.WorkspaceId == tenant.WorkspaceId && a.DeletedAt == null, ct);
            if (!existing)
            {
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
                seededAgentProfiles = true;
            }
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
                var meeting = NewAgent("meeting", "会议智能体", "operation", "session_coordinator", "[\"user.respond\",\"task.dispatch\",\"agent.create_temporary\",\"agent.create_persistent\",\"agent.create_profile\"]", "[\"*\"]",
                    description: "用户与系统的唯一对话入口，理解意图、拆解任务并派发执行。",
                    systemPrompt: "你是用户与系统的唯一对话入口。负责理解用户意图、拆解任务并按双层治理派发给执行层；对用户保持简洁、可执行的表达，汇报时先结论后依据。");
                var contextCompressor = NewAgent("context_compressor", "上下文压缩智能体", "operation", "context_maintenance", "[\"context.read\",\"context.patch\"]", "[\"*\"]",
                    description: "实时压缩会话上下文，保留任务关键证据。",
                    systemPrompt: "你擅长长文本提炼。在保留任务目标、已确认事实与未完成事项的前提下，把冗余对话压缩为结构化摘要；绝不丢弃审批与工具调用结论。");
                var skillRecommender = NewAgent("skill_recommender", "技能推荐智能体", "operation", "capability_advisor", "[\"tool.search\",\"agent.propose\"]", "[\"*\"]",
                    description: "为任务匹配合适的工具与技能组合。");
                var supervisor = NewAgent("supervisor", "监督智能体", "operation", "quality_controller", "[\"supervision.review\"]", "[\"*\"]",
                    description: "质检各智能体的产出，并在授权额度内代批低风险权限请求。",
                    systemPrompt: "你是质量与合规的把关者。审查执行层智能体的计划与结果，发现偏差即打回并给出修正指令；在用户已授予委托额度时可为低风险权限请求代批，超出额度必须上报用户，绝不越权。");
                var evolution = NewAgent("evolution", "进化智能体", "operation", "experience_curator", "[\"memory.candidate\",\"agent.candidate\",\"agent.create_persistent\"]", "[\"*\"]",
                    description: "从运行经验中提出新的智能体配置候选，供用户决定是否持久化。",
                    systemPrompt: "你负责智能体配置的演化。观察运行中的重复模式与能力缺口，起草新的智能体定义（角色/提示词/工具范围），以临时实例参与验证；只有用户明确确认后才转为持久化配置。");
                var gitSteward = NewAgent("git_steward", "Git 变更治理智能体", "operation", "git_steward", "[\"git.review\",\"git.commit_plan\",\"approval.request\"]", "[]");
                var taskPlanner = NewAgent("task_planner", "任务规划智能体", "execution", "execution_coordinator", "[\"task.plan\",\"task.replan\",\"agent.create_temporary\"]", "[\"*\"]",
                    description: "执行层协调者：把会议层的任务分解为可派发的详细计划。",
                    systemPrompt: "你把上层任务拆解为带成功标准与依赖关系的子任务图，按成员专长派发给 worker；规划必须可验证，每步都有明确的完成判据。");
                var codeWorker = NewAgent("worker.code", "代码执行智能体", "execution", "task_executor", "[\"tool.code\",\"tool.file\"]", "[\"write_file\",\"read_file\",\"shell.execute\",\"mcp_invoke\"]");
                var documentWorker = NewAgent("worker.document", "文档生成智能体", "execution", "task_executor", "[\"tool.document\"]", "[\"write_file\",\"read_file\"]");
                var dataWorker = NewAgent("worker.data", "数据处理智能体", "execution", "task_executor", "[\"tool.data\"]", "[\"read_file\",\"shell.execute\"]");
                var browserWorker = NewAgent("worker.browser", "浏览器检索智能体", "execution", "task_executor", "[\"tool.search\",\"tool.browser\"]", "[\"browser.search\",\"browser.fetch\",\"mcp_search\",\"mcp_invoke\"]");
                var fileWorker = NewAgent("worker.file", "文件操作智能体", "execution", "task_executor", "[\"tool.file\"]", "[\"write_file\",\"read_file\"]");
                var generalWorker = NewAgent("worker.general", "通用执行智能体", "execution", "task_executor", "[\"task.execute\"]", "[\"*\"]");
                var gitWorker = NewAgent("worker.git", "Git 执行智能体", "execution", "git_specialist", "[\"tool.git\"]", "[\"git_status\",\"git_diff\",\"git_stage\",\"git_unstage\",\"git_commit\",\"git_push\",\"git_push_readiness\",\"git_fetch\",\"git_pull\",\"git_branch_list\",\"git_checkout\",\"git_branch_create\",\"git_branch_delete\",\"git_branch_rename\",\"git_worktree_list\",\"git_worktree_create\",\"git_worktree_remove\",\"git_merge\",\"git_rebase\",\"git_conflict_preview\",\"git_conflict_resolve\"]");
                var agents = new[] { meeting, contextCompressor, skillRecommender, supervisor, evolution, gitSteward, taskPlanner, codeWorker, documentWorker, dataWorker, browserWorker, fileWorker, generalWorker, gitWorker };
                cfgDb.AgentDefinitions.AddRange(agents);
                foreach (var a in agents)
                {
var snap = JsonSerializer.Serialize(new
                    {
                        id = a.Id,
                        slug = a.Slug,
                        display_name = a.DisplayName,
                        layer = a.Layer,
                        role = a.Role,
                        capabilities = JsonSerializer.Deserialize<JsonElement>(a.CapabilitiesJson ?? "[]"),
                        tool_scope = JsonSerializer.Deserialize<JsonElement>(a.ToolScopeJson ?? "[]"),
                        model_strategy = new { kind = "inherit" },
                        system_prompt = a.SystemPrompt,
                        description = a.Description,
                        enabled = a.Enabled
                    });
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
                var opNodes = new[] { meeting, contextCompressor, skillRecommender, supervisor, evolution, gitSteward };
                var exNodes = new[] { taskPlanner, codeWorker, documentWorker, dataWorker, browserWorker, fileWorker, generalWorker, gitWorker };
                var allNodes = opNodes.Select((a, i) => (NodeKey: $"meeting-{i + 1}", Agent: a)).Concat(exNodes.Select((a, i) => (NodeKey: $"executor-{i + 1}", Agent: a))).ToArray();
                foreach (var (key, a) in allNodes)
                    cfgDb.ModeNodes.Add(new AgentConfiguration.ModeNodeRecord { Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, ModeId = mode.Id, NodeKey = key, AgentDefinitionId = a.Id, Layer = a.Layer, Label = a.DisplayName, Status = "published", Revision = 1, CreatedAt = now2, UpdatedAt = now2 });
                var modeSnap = JsonSerializer.Serialize(new { mode_id = mode.Id, nodes = allNodes.Select(n => new { key = n.NodeKey, agent = n.Agent.Id, layer = n.Agent.Layer }) });
                cfgDb.ModeVersions.Add(new AgentConfiguration.ModeVersionRecord { Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, AgentModeId = mode.Id, Version = 1, SnapshotJson = modeSnap, TopologyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(modeSnap))).ToLowerInvariant(), Status = "published", Revision = 1, CreatedAt = now2, CreatedByPrincipalId = tenant.PrincipalId });
                cfgDb.WorkspaceDefaults.Add(new AgentConfiguration.WorkspaceDefaultsRecord { TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, DefaultAgentDefinitionId = meeting.Id, DefaultAgentModeId = mode.Id, DefaultPromptPipelineId = pipeline.Id, Status = "active", Revision = 1, CreatedAt = now2, UpdatedAt = now2 });
                await cfgDb.SaveChangesAsync(ct);
                seededFormalConfig = true;

                AgentConfiguration.AgentDefinitionRecord NewAgent(string slug, string display, string layer, string role, string caps, string tools, string? description = null, string? systemPrompt = null) => new()
                {
                    Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, Slug = slug, DisplayName = display, Layer = layer, Role = role,
                    CapabilitiesJson = caps, ModelStrategyJson = "{\"kind\":\"inherit\"}", ToolScopeJson = tools,
                    SystemPrompt = systemPrompt, Description = description, Enabled = true,
                    Status = "published", Revision = 1, Version = 1, CreatedAt = now2, UpdatedAt = now2, CreatedByPrincipalId = tenant.PrincipalId, UpdatedByPrincipalId = tenant.PrincipalId
                };
            }
        }

        if (seededChatRoute || seededAgentProfiles || seededFormalConfig)
        {
            logger.LogInformation(
                "DevSeed baseline seeded (chat_route={ChatRoute}, agent_profiles={Agents}, formal_config={Formal}).",
                seededChatRoute, seededAgentProfiles, seededFormalConfig);
        }
    }
}
