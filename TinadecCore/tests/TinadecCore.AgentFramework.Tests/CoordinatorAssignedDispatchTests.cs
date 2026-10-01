using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// 按职责显式派发（2026-09-27）：协调者用固定的执行者 id（assignee / task_dispatch.agent）点名，
/// Core 照办；按 required_tools 反推执行者只作兜底。背景是派发采样——反推把「找引用」这类只读取证
/// 派给了持有同样两个工具的最窄成员 reviewer，显式点名则按职责正确落位。
/// </summary>
public sealed class CoordinatorAssignedDispatchTests
{
    private static readonly RuntimeAgentDefinition Search = Agent("search", "只读检索与取证，产出带出处的证据。",
        ["ls", "read_file", "file_search", "web_fetch"], 0);
    private static readonly RuntimeAgentDefinition Reviewer = Agent("reviewer", "只读审阅改动并给出风险评估。",
        ["read_file", "file_search"], 1);
    private static readonly RuntimeAgentDefinition Engineering = Agent("global_engineering", "修改工作区文件并运行命令自验。",
        ["read_file", "write_file", "shell"], 2);

    [Fact]
    public void DispatchRoster_CarriesResponsibilityTextAndTools()
    {
        var configuration = Configuration([Search, Engineering]) with
        {
            Graph = Graph(FrozenGraphTiers.SelfDispatch, [("meeting", "search")],
                [Template("global_engineering", ["write_file"]) with { Description = "工程执行。" }])
        };

        var roster = FullDuplexRunEngine.ComputeDispatchRoster(configuration);

        Assert.Equal(["search", "global_engineering"], roster.Select(target => target.Id).ToArray());
        Assert.Equal("只读检索与取证，产出带出处的证据。", roster[0].Description);
        Assert.Contains("web_fetch", roster[0].Tools);
        // A roster worker wins over a spawnable template with the same slug, as in selection.
        Assert.Equal("修改工作区文件并运行命令自验。", roster[1].Description);
    }

    [Fact]
    public void DispatchRosterOf_PrefersTheFrozenList()
    {
        var frozen = new[] { new FrozenDispatchTarget("only", "frozen", []) };
        var configuration = Configuration([Search]) with { DispatchRoster = frozen };

        Assert.Same(frozen, FullDuplexRunEngine.DispatchRosterOf(configuration));
    }

    [Fact]
    public void PlannerRoster_IsOneResponsibilityLinePerExecutor()
    {
        var text = PlanningAgent.BuildRosterInstructions(FullDuplexRunEngine.BuildFrozenPlannerRoster(Configuration([Search, Reviewer])));

        Assert.Contains("assignee", text);
        Assert.Contains("- search: 只读检索与取证，产出带出处的证据。 | 工具: file_search, ls, read_file, web_fetch", text);
        Assert.Contains("- reviewer: 只读审阅改动并给出风险评估。", text);
    }

    [Fact]
    public void Materialize_CanonicalizesAssigneeAgainstTheRoster()
    {
        var roster = FullDuplexRunEngine.ComputeDispatchRoster(Configuration([Search, Engineering]));

        var nodes = FullDuplexRunEngine.ValidateAndMaterializeGraph(
            [new PlannedTask { TaskKey = "find", Title = "找引用", Assignee = "SEARCH" }], 8, roster);

        Assert.Equal("search", Assert.Single(nodes).RequestedAgent);
    }

    [Fact]
    public void Materialize_UnknownAssigneeIsACorrectablePlanningError()
    {
        var roster = FullDuplexRunEngine.ComputeDispatchRoster(Configuration([Search, Engineering]));

        var error = Assert.Throws<FullDuplexRunEngine.InvalidTaskGraphException>(() => FullDuplexRunEngine.ValidateAndMaterializeGraph(
            [new PlannedTask { TaskKey = "fix", Title = "修复", Assignee = "coder" }], 8, roster));

        Assert.Contains("'coder'", error.Message);
        Assert.Contains("search (只读检索与取证", error.Message);
        Assert.Contains("global_engineering", error.Message);
    }

    [Fact]
    public void NamedExecutor_WinsOverLeastPrivilegeToolMatching()
    {
        var configuration = Configuration([Search, Reviewer, Engineering]);
        var task = Task(requiredTools: ["read_file", "file_search"]);

        // The sampled misroute: inferring from tools hands evidence work to the narrowest holder.
        Assert.Equal("reviewer", FullDuplexRunEngine.SelectWorker(configuration, task).Agent.Id);

        task.RequestedAgent = "search";
        var (selection, spawnable) = FullDuplexRunEngine.ResolveRequestedWorker(configuration, task);
        Assert.Equal("search", selection.Agent.Id);
        Assert.Equal("coordinator_assigned", selection.Reason);
        Assert.Null(spawnable);
    }

    [Fact]
    public void NamedSpawnableTemplate_StillNeedsTheTiersSpawnAuthority()
    {
        var template = Template("extra_worker", ["shell"]);
        var denied = Configuration([Search]) with
        {
            Graph = Graph(FrozenGraphTiers.Deterministic, [("meeting", "search")], [template])
        };
        var allowed = denied with
        {
            Graph = Graph(FrozenGraphTiers.SelfDispatch, [("meeting", "search")], [template])
        };

        var error = Assert.ThrowsAny<Exception>(() =>
            FullDuplexRunEngine.ResolveRequestedWorker(denied, Task(requestedAgent: "extra_worker")));
        Assert.Contains("graph_tier_spawn_denied", error.Message);

        var (selection, spawnable) = FullDuplexRunEngine.ResolveRequestedWorker(allowed, Task(requestedAgent: "extra_worker"));
        Assert.Equal("extra_worker", selection.Agent.Id);
        Assert.Same(template, spawnable);
    }

    [Fact]
    public void NamedSpawnableTemplate_CarriesItsFrozenSystemPromptToTheWorker()
    {
        var template = Template("prompted_worker", ["read_file"]) with
        {
            SystemPrompt = "DUTY: inspect only the assigned evidence and report file references."
        };
        var configuration = Configuration([]) with
        {
            Graph = Graph(FrozenGraphTiers.SelfDispatch, [("meeting", "prompted_worker")], [template])
        };

        var (selection, _) = FullDuplexRunEngine.ResolveRequestedWorker(configuration, Task(requestedAgent: "prompted_worker"));

        Assert.Equal(template.SystemPrompt, selection.Agent.SystemPrompt);
    }

    [Fact]
    public void UnknownNamedExecutor_FailsWithTheValidChoices()
    {
        var error = Assert.ThrowsAny<Exception>(() =>
            FullDuplexRunEngine.ResolveRequestedWorker(Configuration([Search]), Task(requestedAgent: "ghost")));

        Assert.Contains("'ghost'", error.Message);
        Assert.Contains("Valid ids: search", error.Message);
    }

    [Fact]
    public void WorkerHandles_CountPerAgentWithinTheRun()
    {
        var first = new DurableTaskNode { TaskKey = "a", WorkerAgentSlug = "search", WorkerHandle = "search#1" };
        var other = new DurableTaskNode { TaskKey = "b", WorkerAgentSlug = "global_engineering", WorkerHandle = "global_engineering#1" };
        var pending = new DurableTaskNode { TaskKey = "c", WorkerAgentSlug = "search" };

        Assert.Equal("search#2", FullDuplexRunEngine.NextWorkerHandle([first, other, pending], "search"));
        Assert.Equal("reviewer#1", FullDuplexRunEngine.NextWorkerHandle([first, other], "reviewer"));
    }

    [Fact]
    public void MeetingEvidence_NamesTheTaskTheExecutorAndEachCriterion()
    {
        var node = new DurableTaskNode
        {
            TaskKey = "find",
            Title = "找出 useUiePage 的引用",
            Status = "completed",
            ResultStatus = "completed",
            ResultSummary = "找到 3 处。",
            WorkerAgentSlug = "search",
            WorkerHandle = "search#1",
            Evidence = ["criterion_evidence:列出所有引用||HomePage.vue、MarketPage.vue、ChatroomPage.vue", "missing_criterion_evidence:说明用途", "closeout:none"]
        };

        var text = FullDuplexRunEngine.FormatTaskEvidenceForMeeting(node);

        Assert.StartsWith("- [completed] 找出 useUiePage 的引用 (search#1): 找到 3 处。", text);
        Assert.Contains("✓ 列出所有引用 — HomePage.vue", text);
        Assert.Contains("✗ no evidence: 说明用途", text);
        Assert.DoesNotContain("closeout", text);
    }

    [Fact]
    public void TaskWait_TargetsOnlyTheCallersOwnDispatches()
    {
        var master = new DurableTaskNode { TaskId = Guid.NewGuid(), TaskKey = "master" };
        var mine = Dispatched(master, "search#1", "completed");
        var other = Dispatched(new DurableTaskNode { TaskId = Guid.NewGuid() }, "search#2", "completed");

        var byHandle = FullDuplexRunEngine.ResolveWaitTargets([master, mine, other], master, ["search#1"], out var none);
        Assert.Same(mine, Assert.Single(byHandle));
        Assert.Empty(none);

        var byId = FullDuplexRunEngine.ResolveWaitTargets([master, mine, other], master, [mine.DispatchId!.Value.ToString()], out _);
        Assert.Same(mine, Assert.Single(byId));

        // Someone else's sub-task is not waitable by name.
        FullDuplexRunEngine.ResolveWaitTargets([master, mine, other], master, ["search#2"], out var unknown);
        Assert.Equal(["search#2"], unknown);
    }

    [Fact]
    public void TaskWait_WithNoIdsWaitsForEveryUnreportedDispatch()
    {
        var master = new DurableTaskNode { TaskId = Guid.NewGuid(), TaskKey = "master" };
        var reported = Dispatched(master, "search#1", "completed");
        var fresh = Dispatched(master, "search#2", "running");
        master.ReportedTaskIds.Add(reported.TaskId);

        var targets = FullDuplexRunEngine.ResolveWaitTargets([master, reported, fresh], master, [], out _);

        Assert.Same(fresh, Assert.Single(targets));
    }

    [Fact]
    public void WaitingTask_BecomesDispatchableOnlyWhenEveryAwaitedTaskIsTerminal()
    {
        var master = new DurableTaskNode { TaskId = Guid.NewGuid(), TaskKey = "master", Status = "waiting" };
        var a = Dispatched(master, "search#1", "completed");
        var b = Dispatched(master, "global_engineering#1", "running");
        master.AwaitingTaskIds = [a.TaskId, b.TaskId];
        IReadOnlyList<DurableTaskNode> tasks = [master, a, b];

        Assert.False(FullDuplexRunEngine.IsDispatchable(master, tasks));
        b.Status = "failed";
        Assert.True(FullDuplexRunEngine.IsDispatchable(master, tasks));
    }

    [Fact]
    public void TaskWaitResult_CarriesHandleStatusSummaryAndCriteria()
    {
        var master = new DurableTaskNode { TaskId = Guid.NewGuid(), TaskKey = "master" };
        var done = Dispatched(master, "search#1", "completed");
        done.ResultSummary = "找到 3 处。";
        done.Evidence = ["criterion_evidence:列出引用||HomePage.vue", "missing_criterion_evidence:说明用途"];

        using var json = System.Text.Json.JsonDocument.Parse(FullDuplexRunEngine.BuildTaskWaitResult([done]));
        var entry = Assert.Single(json.RootElement.GetProperty("tasks").EnumerateArray());

        Assert.Equal("search#1", entry.GetProperty("handle").GetString());
        Assert.Equal("completed", entry.GetProperty("status").GetString());
        Assert.Equal("找到 3 处。", entry.GetProperty("summary").GetString());
        var criteria = entry.GetProperty("criteria").EnumerateArray().ToArray();
        Assert.True(criteria[0].GetProperty("met").GetBoolean());
        Assert.Equal("HomePage.vue", criteria[0].GetProperty("evidence").GetString());
        Assert.False(criteria[1].GetProperty("met").GetBoolean());
    }

    [Fact]
    public void FollowUpBrief_HandsTheEarlierResultToTheNewExecutor()
    {
        var prior = Dispatched(new DurableTaskNode { TaskId = Guid.NewGuid() }, "search#1", "completed");
        prior.ResultSummary = "styles.css 里 12 处 !important。";

        var brief = FullDuplexRunEngine.FollowUpBrief(prior, "把其它 .css 文件也统计进去。");

        Assert.Contains("search#1", brief);
        Assert.Contains("styles.css 里 12 处 !important。", brief);
        Assert.EndsWith("New instruction: 把其它 .css 文件也统计进去。", brief);
    }

    [Fact]
    public void PlanUpdate_AcceptsAWellFormedPlan()
    {
        var error = FullDuplexRunEngine.TryParsePlan(
            """{"steps":[{"step":"读现状","status":"completed"},{"step":"改代码","status":"IN_PROGRESS"}],"explanation":"读完了"}""",
            out var steps, out var explanation);

        Assert.Null(error);
        Assert.Equal([new TaskPlanStep("读现状", "completed"), new TaskPlanStep("改代码", "in_progress")], steps);
        Assert.Equal("读完了", explanation);
    }

    [Theory]
    [InlineData("""{}""", "needs a steps array")]
    [InlineData("""{"steps":[]}""", "plan is empty")]
    [InlineData("""{"steps":[{"step":"a","status":"doing"}]}""", "status 'doing'")]
    [InlineData("""{"steps":[{"status":"pending"}]}""", "no step text")]
    [InlineData("""{"steps":[{"step":"a","status":"in_progress"},{"step":"b","status":"in_progress"}]}""", "More than one step is in_progress")]
    public void PlanUpdate_ExplainsWhatIsWrongWithABadPlan(string arguments, string expected)
    {
        var error = FullDuplexRunEngine.TryParsePlan(arguments, out _, out _);

        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Flows_HangHandedOffSubTasksOffTheirDispatcher()
    {
        var master = new DurableTaskNode { TaskId = Guid.NewGuid(), TaskKey = "master", WorkerAgentSlug = "solo_master", WorkerHandle = "solo_master#1", Status = "waiting" };
        var sub = Dispatched(master, "search#1", "running");
        var checkpoint = new FullDuplexCheckpointV1 { UserGoal = "g", Tasks = [master, sub] };

        var flows = OrchestrationGraphProjection.FlowsFromCheckpoint(checkpoint, "solo_master")
            .Select(flow => System.Text.Json.JsonSerializer.SerializeToElement(flow))
            .ToArray();

        Assert.Equal("solo_master", flows[0].GetProperty("from").GetString());
        Assert.Equal("plan", flows[0].GetProperty("via").GetString());
        Assert.Equal("solo_master#1", flows[1].GetProperty("from").GetString());
        Assert.Equal("search#1", flows[1].GetProperty("handle").GetString());
        Assert.Equal("task_dispatch", flows[1].GetProperty("via").GetString());
    }

    [Fact]
    public void ToolResults_ReachTheModelCompactAndReadable()
    {
        // TinadecTools writes non-ASCII as escape sequences and the adapter would indent a JsonElement.
        const string raw = """
            {
              "summary": "\u627e\u5230 3 \u5904",
              "ok": true
            }
            """;

        Assert.Equal("""{"summary":"找到 3 处","ok":true}""", ExecutionAgent.ModelFacingResult(raw));
        Assert.Equal("plain text", ExecutionAgent.ModelFacingResult("plain text"));
    }

    private static DurableTaskNode Dispatched(DurableTaskNode by, string handle, string status) => new()
    {
        TaskId = Guid.NewGuid(),
        TaskKey = handle.Replace('#', '-'),
        Title = handle,
        Status = status,
        ResultStatus = IsTerminal(status) ? status : null,
        WorkerHandle = handle,
        WorkerAgentSlug = handle.Split('#')[0],
        DispatchId = Guid.NewGuid(),
        DispatchedByTaskId = by.TaskId
    };

    private static bool IsTerminal(string status) => status is "completed" or "failed" or "blocked";

    private static DurableTaskNode Task(IReadOnlyList<string>? requiredTools = null, string? requestedAgent = null) => new()
    {
        TaskId = Guid.NewGuid(),
        TaskKey = "task",
        Title = "task",
        RequiredTools = [.. requiredTools ?? []],
        RequestedAgent = requestedAgent
    };

    private static FrozenGraph Graph(
        string tier,
        (string Source, string Target)[] edges,
        FrozenSpawnableTemplate[] spawnable)
    {
        var nodeKeys = edges.SelectMany(edge => new[] { edge.Source, edge.Target }).Distinct().ToList();
        if (nodeKeys.Count == 0) nodeKeys.Add("meeting");
        var nodes = nodeKeys.Select(key => new FrozenGraphNode(
            key,
            key,
            key == "meeting" ? "operation" : "execution",
            key == "meeting")).ToList();
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

    private static FrozenSpawnableTemplate Template(string slug, IReadOnlyList<string> tools) =>
        new(slug, Guid.NewGuid(), Guid.NewGuid(), $"hash-{slug}", "task_executor", tools, []);

    private static RuntimeAgentDefinition Agent(string id, string description, IReadOnlyList<string> tools, int order) =>
        new(id, "execution", "task_executor", "task", [], DirectUserOutput: false, "read")
        {
            AgentDefinitionId = Guid.NewGuid(),
            AgentVersionId = Guid.NewGuid(),
            VersionContentHash = $"hash-{id}",
            AllowedTools = tools,
            Description = description,
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
            [new RuntimeAgentDefinition("meeting", "operation", "session_coordinator", "session", [], DirectUserOutput: true, "manage")],
            executionAgents,
            [],
            "")
        {
            ToolManifest = executionAgents.SelectMany(agent => agent.AllowedTools).Distinct()
                .Select(id => new FrozenToolManifestEntry(id, id, System.Text.Json.JsonDocument.Parse("{}").RootElement, "low", false, false, "safe", [])).ToArray()
        };
}
