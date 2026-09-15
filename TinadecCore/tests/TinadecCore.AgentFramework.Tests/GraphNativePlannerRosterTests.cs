using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// 缺口①修复回归：规划名册图原生 + 声明边回落收敛。
/// 背景 run d035fa40 —— 规划模型输出散文 → PlanningAgent 回落单任务（整句目标、零需求）→
/// 就近覆盖平局规则把写文件目标派给只读 search，run 假完成。本组测试钉住三层防线：
/// (1) 择人面 = 声明边目标 ∪ spawn 白名单，role 字符串不再做硬白名单；
/// (2) 规划指令明确「任务数组即派发」，模型不再找不存在的 dispatch 工具；
/// (3) 声明边模式下回落单任务被拒绝（plan_parse_failed 可见），free_form 保留回落。
/// </summary>
public sealed class GraphNativePlannerRosterTests
{
    // ──────────────────────────────────────────────────────────
    // 修复 A：图原生规划名册
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void PlannerRoster_DeclaredEdges_IncludeTargets_RegardlessOfRoleVocabulary()
    {
        // 执行智能体持自定角色词汇（"engineer"），旧过滤（worker.*/task_executor/git_specialist）
        // 会把它从规划名册剔除；图原生名册必须按声明边收录它。
        var engineering = Agent("global_engineering", "execution", "engineer", ["tool.code"], ["write_file"], 0);
        var search = Agent("search", "execution", "researcher", ["tool.search"], ["read_file"], 1);
        var configuration = Configuration([engineering, search]) with
        {
            Graph = Graph(FrozenGraphTiers.SelfDispatch,
                edges: [("meeting", "search"), ("meeting", "global_engineering")],
                spawnable: [])
        };

        var roster = FullDuplexRunEngine.BuildFrozenPlannerRoster(configuration);

        Assert.Equal(["search", "global_engineering"], roster.Select(entry => entry.Name).ToArray());
    }

    [Fact]
    public void PlannerRoster_SelfDispatch_AddsSpawnableWhitelistBeyondEdges()
    {
        var search = Agent("search", "execution", "researcher", ["tool.search"], ["read_file"], 0);
        var configuration = Configuration([search]) with
        {
            Graph = Graph(FrozenGraphTiers.SelfDispatch,
                edges: [("meeting", "search")],
                spawnable: [Template("extra_worker", tools: ["shell"], capabilities: ["tool.code"])])
        };

        var roster = FullDuplexRunEngine.BuildFrozenPlannerRoster(configuration);

        Assert.Equal(["search", "extra_worker"], roster.Select(entry => entry.Name).ToArray());
        var spawned = Assert.Single(roster, entry => entry.Name == "extra_worker");
        Assert.Equal(["shell"], spawned.AllowedTools);
        Assert.Equal(["tool.code"], spawned.Capabilities);
    }

    [Fact]
    public void PlannerRoster_Deterministic_ExcludesSpawnableWhitelist()
    {
        var search = Agent("search", "execution", "researcher", ["tool.search"], ["read_file"], 0);
        var configuration = Configuration([search]) with
        {
            Graph = Graph(FrozenGraphTiers.Deterministic,
                edges: [("meeting", "search")],
                spawnable: [Template("extra_worker", tools: ["shell"], capabilities: [])])
        };

        var roster = FullDuplexRunEngine.BuildFrozenPlannerRoster(configuration);

        Assert.Equal(["search"], roster.Select(entry => entry.Name).ToArray());
    }

    [Fact]
    public void PlannerRoster_FreeForm_UsesSpawnableWhitelist()
    {
        // free_director 形态：执行层为空，总监体从 spawnable 白名单搭建执行层。
        var configuration = Configuration([]) with
        {
            Graph = Graph(FrozenGraphTiers.FreeForm,
                edges: [],
                spawnable: [Template("search", tools: ["read_file"], capabilities: []),
                            Template("global_engineering", tools: ["write_file"], capabilities: [])])
        };

        var roster = FullDuplexRunEngine.BuildFrozenPlannerRoster(configuration);

        Assert.Equal(["search", "global_engineering"], roster.Select(entry => entry.Name).ToArray());
    }

    [Fact]
    public void PlannerRoster_FreeForm_KeepsExecutionAgentsWithoutSpawnWhitelist()
    {
        // office/夹具形态：free_form 有执行层但包未声明 agent_types 白名单——
        // free_form 对任意 worker 放行，名册必须保留全部启用执行智能体。
        var code = Agent("worker.code", "execution", "task_executor", ["tool.code"], ["write_file"], 0);
        var data = Agent("worker.data", "execution", "task_executor", ["tool.data"], ["read_file"], 1);
        var configuration = Configuration([code, data]) with
        {
            Graph = Graph(FrozenGraphTiers.FreeForm, edges: [], spawnable: [])
        };

        var roster = FullDuplexRunEngine.BuildFrozenPlannerRoster(configuration);

        Assert.Equal(["worker.code", "worker.data"], roster.Select(entry => entry.Name).ToArray());
    }

    [Fact]
    public void PlannerRoster_NullGraph_FallsBackToAllEnabledExecutionAgents()
    {
        // 仅手工构造的测试配置没有 Graph 段：回落为全部启用执行智能体（无 role 过滤），排除 task_planner。
        var legacy = Agent("worker.code", "execution", "task_executor", ["tool.code"], ["write_file"], 0);
        var custom = Agent("analyst", "execution", "data_analyst", ["tool.data"], ["read_file"], 1);
        var planner = Agent("task_planner", "execution", "execution_coordinator", ["task.plan"], ["*"], 2);
        var configuration = Configuration([legacy, custom, planner]);

        var roster = FullDuplexRunEngine.BuildFrozenPlannerRoster(configuration);

        Assert.Equal(["worker.code", "analyst"], roster.Select(entry => entry.Name).ToArray());
    }

    // ──────────────────────────────────────────────────────────
    // 修复 C：声明边回落收敛
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void PlanningFallback_IsRefusedOnDeclaredEdges_WithPlanParseFailedReason()
    {
        var configuration = Configuration([Agent("search", "execution", "researcher", [], ["read_file"], 0)]) with
        {
            Graph = Graph(FrozenGraphTiers.SelfDispatch,
                edges: [("meeting", "search")],
                spawnable: [])
        };
        var fallback = new PlannedTask { Title = "整句用户目标", IsFallback = true };

        var ex = Assert.Throws<FullDuplexRunEngine.InvalidTaskGraphException>(
            () => FullDuplexRunEngine.RefuseSilentPlanningFallbackOnDeclaredEdges(configuration, [fallback]));
        // InvalidTaskGraphException carries the structured reason; the engine's
        // retry loop surfaces it through FailRunAsync("invalid_task_graph", …).
        Assert.Contains("plan_parse_failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanningFallback_IsKeptOnFreeForm()
    {
        var configuration = Configuration([Agent("search", "execution", "researcher", [], ["read_file"], 0)]) with
        {
            Graph = Graph(FrozenGraphTiers.FreeForm, edges: [], spawnable: [])
        };

        FullDuplexRunEngine.RefuseSilentPlanningFallbackOnDeclaredEdges(
            configuration, [new PlannedTask { Title = "整句用户目标", IsFallback = true }]);
    }

    [Fact]
    public void PlanningFallback_AuthoredTasksPassOnDeclaredEdges()
    {
        var configuration = Configuration([Agent("search", "execution", "researcher", [], ["read_file"], 0)]) with
        {
            Graph = Graph(FrozenGraphTiers.SelfDispatch,
                edges: [("meeting", "search")],
                spawnable: [])
        };

        FullDuplexRunEngine.RefuseSilentPlanningFallbackOnDeclaredEdges(
            configuration, [new PlannedTask { Title = "检索取证", RequiredTools = ["read_file"] }]);
    }

    // ──────────────────────────────────────────────────────────
    // Fixtures
    // ──────────────────────────────────────────────────────────

    private static FrozenGraph Graph(
        string tier,
        (string Source, string Target)[] edges,
        FrozenSpawnableTemplate[] spawnable)
    {
        var nodeKeys = edges.SelectMany(edge => new[] { edge.Source, edge.Target })
            .Distinct().ToList();
        if (nodeKeys.Count == 0) nodeKeys.Add("meeting");
        var nodes = nodeKeys.Select(key => new FrozenGraphNode(
            key,
            key,
            string.Equals(key, "meeting", StringComparison.Ordinal) ? "operation" : "execution",
            string.Equals(key, "meeting", StringComparison.Ordinal))).ToList();
        return new FrozenGraph(
            tier,
            "meeting",
            "meeting",
            nodes,
            edges.Select(edge => new DeclaredGraphEdge($"{edge.Source}-to-{edge.Target}", edge.Source, edge.Target)).ToList())
        {
            SpawnableTemplates = spawnable
        };
    }

    private static FrozenSpawnableTemplate Template(string slug, IReadOnlyList<string> tools, IReadOnlyList<string> capabilities) =>
        new(slug, Guid.NewGuid(), Guid.NewGuid(), $"hash-{slug}", "engineer", tools, capabilities);

    private static RuntimeAgentDefinition Agent(
        string id,
        string layer,
        string role,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string> tools,
        int order) =>
        new(id, layer, role, id == "meeting" ? "session" : "task", capabilities,
            DirectUserOutput: id == "meeting", "read")
        {
            AgentDefinitionId = Guid.NewGuid(),
            AgentVersionId = Guid.NewGuid(),
            VersionContentHash = $"hash-{id}",
            AllowedTools = tools,
            RosterOrder = order
        };

    private static FrozenRunConfigurationV1 Configuration(IReadOnlyList<RuntimeAgentDefinition> executionAgents) =>
        new(
            "frozen-run-configuration/v3",
            "baseline",
            1,
            Guid.Parse("00000000-0000-0000-0000-0000000000aa"),
            "mode:test:1",
            "ask",
            new SpawnPolicy(2, 8, 4),
            new SchedulingPolicy(2, 2, true),
            new SupervisionPolicy(true, 2),
            new ContextPolicy(8192, 24, true),
            new MemoryPolicy(true, 8, ["workspace"], ["fact"]),
            new ToolRuntimePolicy("tinadec-tools", true, true, 120, 4),
            [Agent("meeting", "operation", "session_coordinator", [], [], 0)],
            executionAgents,
            [],
            "");
}
