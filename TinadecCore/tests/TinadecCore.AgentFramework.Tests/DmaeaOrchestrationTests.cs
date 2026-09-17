using Microsoft.Extensions.AI;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// DmaEA runtime tests. Planning/execution agents run against a fake chat client via the
/// injected chat-client factory (no HTTP).
/// </summary>
public sealed class DmaeaOrchestrationTests
{
    // ──────────────────────────────────────────────────────────
    // Planning layer
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PlanningAgent_ParsesTaskArrayFromModelOutput()
    {
        var resolver = new FakeChatResolver(true);
        var client = new StubChatClient("[{\"title\":\"任务A\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]");
        var planner = new PlanningAgent(new FakeFactory(resolver, client));
        var ctx = Context("用户目标");

        var tasks = await planner.PlanAsync(ctx, [Planner()], CancellationToken.None);

        var task = Assert.Single(tasks);
        Assert.Equal("任务A", task.Title);
        Assert.Equal(new[] { "完成" }, task.SuccessCriteria);
        Assert.Equal("low", task.Risk);
    }

    [Fact]
    public async Task PlanningAgent_DegradesToSingleTaskWhenOutputIsNotJson()
    {
        var resolver = new FakeChatResolver(true);
        var client = new StubChatClient("I will think about it later.");
        var planner = new PlanningAgent(new FakeFactory(resolver, client));

        var tasks = await planner.PlanAsync(Context("用户目标"), [Planner()], CancellationToken.None);

        var task = Assert.Single(tasks);
        Assert.Equal("用户目标", task.Title);
        Assert.Equal("Task is complete when the goal is satisfied", task.SuccessCriteria[0]);
        // 缺口①修复 C：回落任务必须可被引擎识别，声明边模式下拒绝静默派发。
        Assert.True(task.IsFallback);
        Assert.False(planner.LastPlanWasParsed);
    }

    /// <summary>
    /// 「你好」这类目标，模型会**正确地**返回空数组：没有要执行的子任务，会议智能体直接
    /// 从对话作答。空数组曾被当成解析失败（`Length > 0` 才算解析成功），于是注入回落目标
    /// 任务，而声明边模式拒绝派发回落任务——于是一句问候在任何模型、任何 provider 下都必然
    /// 把 run 打成 plan_parse_failed。空计划是结果，不是错误。
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("```json\n[]\n```")]
    [InlineData("<think>没有需要执行的子任务。</think>\n[]")]
    [InlineData("好的，这个目标不需要执行任何子任务：\n[]\n以上。")]
    public async Task PlanningAgent_TreatsParsedEmptyArrayAsNoWorkRatherThanParseFailure(string modelOutput)
    {
        var client = new StubChatClient(modelOutput);
        var planner = new PlanningAgent(new FakeFactory(new FakeChatResolver(true), client));

        var tasks = await planner.PlanAsync(Context("你好"), [Planner()], CancellationToken.None);

        Assert.Empty(tasks);
        Assert.True(planner.LastPlanWasParsed);
    }

    [Fact]
    public async Task PlanningAgent_InstructionsExplainTaskArrayIsTheDispatchMechanism()
    {
        // 缺口①修复 B（提示词汇表断层）：对话身份「派发」的唯一机制是输出 JSON 任务数组；
        // 治理层零工具，用户文本一旦要求「通过 dispatch 工具派发」，没有这句话模型就会
        // 找工具找不到而以散文拒绝（run d035fa40）。
        var client = new StubChatClient("[]");
        var planner = new PlanningAgent(new FakeFactory(new FakeChatResolver(true), client));

        _ = await planner.PlanAsync(Context("目标"), [Planner()], CancellationToken.None);

        Assert.NotNull(client.LastInstructions);
        Assert.Contains("唯一机制", client.LastInstructions, StringComparison.Ordinal);
        Assert.Contains("不需要任何 dispatch 工具", client.LastInstructions, StringComparison.Ordinal);
        Assert.Contains("不要用散文拒绝或解释", client.LastInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanningAgent_ReceivesFrozenSpecialistRosterInInstructions()
    {
        var client = new StubChatClient("""[{"task_key":"code-task","title":"Code","description":"","success_criteria":["done"],"dependencies":[],"required_capabilities":["tool.code"],"required_tools":["write_file"],"priority":1,"risk":"low"}]""");
        var planner = new PlanningAgent(new FakeFactory(new FakeChatResolver(true), client));
        var specialist = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "worker.code",
            Layer = "execution",
            AgentType = "task_executor",
            Capabilities = ["tool.file", "tool.code"],
            AllowedTools = ["write_file", "read_file"],
            Enabled = true
        };

        _ = await planner.PlanAsync(Context("Implement"), [specialist], "frozen-prompt:task_planner", CancellationToken.None);

        Assert.NotNull(client.LastInstructions);
        Assert.Contains("frozen-prompt:task_planner", client.LastInstructions, StringComparison.Ordinal);
        Assert.Contains("Frozen specialist roster", client.LastInstructions, StringComparison.Ordinal);
        Assert.Contains("\"slug\":\"worker.code\"", client.LastInstructions, StringComparison.Ordinal);
        Assert.Contains("\"tool.code\"", client.LastInstructions, StringComparison.Ordinal);
        Assert.Contains("\"write_file\"", client.LastInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanningAgent_ThrowsWhenChatRouteUnavailable()
    {
        var resolver = new FakeChatResolver(false, "Provider API key is not stored.");
        var planner = new PlanningAgent(new FakeFactory(resolver, new StubChatClient("unused")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => planner.PlanAsync(Context("用户目标"), [Planner()], CancellationToken.None));
        Assert.Equal("Provider API key is not stored.", ex.Message);
    }

    [Fact]
    public async Task PlanningAgent_ExtractsTaskArrayFromReasoningWrappedOutput()
    {
        // A reasoning model emits a thinking block (with a stray bracket), prose, and a
        // fenced JSON array. The planner must still recover the real task array instead
        // of degrading to the tool-less fallback task.
        var client = new StubChatClient(
            "让我先想想。\n\n<think>\n用户问这是什么项目，我需要 [分析] 目录结构。\n</think>\n\n好的，计划如下：\n```json\n[{\"title\":\"分析项目结构\",\"success_criteria\":[\"列出关键目录\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]\n```");
        var planner = new PlanningAgent(new FakeFactory(new FakeChatResolver(true), client));

        var tasks = await planner.PlanAsync(Context("这是什么项目"), [Planner()], CancellationToken.None);

        var task = Assert.Single(tasks);
        Assert.Equal("分析项目结构", task.Title);
        Assert.Equal(new[] { "列出关键目录" }, task.SuccessCriteria);
    }

    // ──────────────────────────────────────────────────────────
    // Planner output leniency (2026-09-17 plan_parse_failed cluster)
    // ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("success_criteria")]
    [InlineData("dependencies")]
    [InlineData("required_capabilities")]
    [InlineData("required_tools")]
    public async Task PlanningAgent_RepairsScalarArrayFieldsInsteadOfFailing(string field)
    {
        // Live failure shape (runs feb146e4/05a89fc3, qwen3.8-27b): the JSON is
        // well-formed but an array-typed field carries a single scalar string.
        // System.Text.Json does not coerce scalar → array, so the whole plan used
        // to die as plan_parse_failed even though the intent was fully readable.
        var scalarValue = field == "dependencies" ? "" : "列出关键目录";
        var shape = $$"""
            [{"task_key":"t1","title":"任务A","description":"读取 README",
              "success_criteria":{{(field == "success_criteria" ? $"\"{scalarValue}\"" : "[\"列出关键目录\"]")}},
              "dependencies":{{(field == "dependencies" ? $"\"{scalarValue}\"" : "[]")}},
              "required_capabilities":{{(field == "required_capabilities" ? $"\"{scalarValue}\"" : "[]")}},
              "required_tools":{{(field == "required_tools" ? $"\"{scalarValue}\"" : "[]")}},
              "priority":1,"risk":"low"}]
            """;
        var client = new StubChatClient(shape);
        var planner = new PlanningAgent(new FakeFactory(new FakeChatResolver(true), client));

        var tasks = await planner.PlanAsync(Context("介绍项目"), [Planner()], CancellationToken.None);

        var task = Assert.Single(tasks);
        Assert.True(planner.LastPlanWasParsed, shape);
        Assert.Equal("任务A", task.Title);
        var repaired = field switch
        {
            "success_criteria" => task.SuccessCriteria,
            "dependencies" => task.Dependencies,
            "required_capabilities" => task.RequiredCapabilities,
            _ => task.RequiredTools
        };
        var expected = field == "dependencies" ? Array.Empty<string>() : new[] { "列出关键目录" };
        Assert.Equal(expected, repaired);
    }

    [Fact]
    public async Task PlanningAgent_DropsNullAndNonStringArrayEntries()
    {
        // Array form with junk entries: nulls, empties, and nested junk are dropped
        // instead of throwing the whole plan away.
        var client = new StubChatClient("""[{"task_key":"t1","title":"任务A","description":"","success_criteria":["判据",null,"","完成",["嵌套"]],"dependencies":[null,""],"required_capabilities":[42],"priority":1,"risk":"low"}]""");
        var planner = new PlanningAgent(new FakeFactory(new FakeChatResolver(true), client));

        var tasks = await planner.PlanAsync(Context("目标"), [Planner()], CancellationToken.None);

        var task = Assert.Single(tasks);
        Assert.True(planner.LastPlanWasParsed);
        Assert.Equal(new[] { "判据", "完成" }, task.SuccessCriteria);
        Assert.Empty(task.Dependencies);
        Assert.Empty(task.RequiredCapabilities);
    }

    [Fact]
    public async Task PlanningAgent_CapturesParseErrorDetailForDiagnostics()
    {
        // An unrecoverable shape must carry the JSON-level reason, not just
        // "not a task array": the retry hint and plan_diagnostic consume it.
        var client = new StubChatClient("""[{"task_key":"t1","title":"任务A","description":"","success_criteria":42,"dependencies":[],"required_capabilities":[],"required_tools":[],"priority":1,"risk":"low"}]""");
        var planner = new PlanningAgent(new FakeFactory(new FakeChatResolver(true), client));

        var tasks = await planner.PlanAsync(Context("目标"), [Planner()], CancellationToken.None);

        Assert.False(planner.LastPlanWasParsed);
        // The converter-level detail names the shape it rejected (the property name
        // itself is only available at the object layer, so the JSON path is not
        // reproduced here — the response_head still carries the full shape).
        Assert.Contains("expected a string array or a string", planner.LastParseErrorDetail, StringComparison.Ordinal);
        Assert.Single(tasks);
        Assert.True(tasks[0].IsFallback);
    }

    [Fact]
    public async Task PlanningAgent_ReportsNoArrayCandidateWhenAnswerCarriesNoJson()
    {
        var client = new StubChatClient("你好！很高兴见到你。");
        var planner = new PlanningAgent(new FakeFactory(new FakeChatResolver(true), client));

        var tasks = await planner.PlanAsync(Context("你好"), [Planner()], CancellationToken.None);

        Assert.False(planner.LastPlanWasParsed);
        Assert.Contains("no balanced JSON array candidate", planner.LastParseErrorDetail, StringComparison.Ordinal);
        Assert.Single(tasks);
    }

    [Fact]
    public async Task PlanningAgent_InstructionsPinArrayFieldTypesAndEmptyPlanContract()
    {
        // The 2026-09-17 cluster also exposed two prompt gaps: no type constraint on
        // the four array fields, and no "greeting → []" contract.
        var client = new StubChatClient("[]");
        var planner = new PlanningAgent(new FakeFactory(new FakeChatResolver(true), client));

        _ = await planner.PlanAsync(Context("目标"), [Planner()], CancellationToken.None);

        Assert.NotNull(client.LastInstructions);
        Assert.Contains("四个字段必须是字符串数组", client.LastInstructions, StringComparison.Ordinal);
        Assert.Contains("直接输出 []", client.LastInstructions, StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────────
    // Execution layer
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecutionAgent_ReturnsCompletedResultWithSummary()
    {
        var resolver = new FakeChatResolver(true);
        var client = new StubChatClient("任务A 完成");
        var executor = new ExecutionAgent(new FakeFactory(resolver, client));
        var task = new PlannedTask { Title = "任务A", SuccessCriteria = ["完成"] };
        var nodeId = Guid.NewGuid();

        var result = await executor.ExecuteAsync(Context("用户目标"), Executor(), task, nodeId, CancellationToken.None);

        Assert.Equal(nodeId, result.TaskNodeId);
        Assert.Equal("completed", result.Status);
        Assert.Equal("任务A 完成", result.Summary);
        Assert.Equal(new[] { "任务A 完成" }, result.Evidence);
    }

    [Fact]
    public async Task ExecutionAgent_ReturnsFailedResultWithoutThrowingWhenRouteUnavailable()
    {
        var resolver = new FakeChatResolver(false, "No chat model route is configured for this workspace.");
        var executor = new ExecutionAgent(new FakeFactory(resolver, null));
        var task = new PlannedTask { Title = "任务A" };

        var result = await executor.ExecuteAsync(Context("用户目标"), Executor(), task, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal("No chat model route is configured for this workspace.", result.Summary);
    }

    [Fact]
    public async Task ExecutionAgent_StripsReasoningFromCompletedSummary()
    {
        // The worker's stored summary/evidence (also echoed into multi-turn history)
        // must not carry thinking markup or orphan tags.
        var client = new StubChatClient("让我处理一下。\n\n<think>\n先读取文件再说。\n</think>\n\n任务A 已完成。");
        var executor = new ExecutionAgent(new FakeFactory(new FakeChatResolver(true), client));
        var task = new PlannedTask { Title = "任务A", SuccessCriteria = ["完成"] };

        var result = await executor.ExecuteAsync(Context("用户目标"), Executor(), task, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("completed", result.Status);
        Assert.Contains("任务A 已完成。", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("先读取文件再说", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("<think>", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────
    // Supervision layer
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SupervisionAgent_ParsesVerdictFromReasoningWrappedOutput()
    {
        // The supervisor's verdict object is wrapped in a think block and a fence;
        // it must still parse to a real decision instead of escalating as unparsable.
        var client = new StubChatClient(
            "<think>\n证据充分，可以通过。\n</think>\n\n```json\n{\"decision\":\"pass\",\"reasons\":[\"证据充分\"],\"revise_task_indexes\":[]}\n```");
        var supervisor = new SupervisionAgent(new FakeFactory(new FakeChatResolver(true), client));
        var tasks = new[] { new PlannedTask { Title = "任务A", SuccessCriteria = ["完成"] } };
        var results = new[]
        {
            new StepResult { TaskNodeId = Guid.NewGuid(), AgentId = "x", Status = "completed", Summary = "任务A 完成", Evidence = ["任务A 完成"] }
        };

        var verdict = await supervisor.ReviewAsync("用户目标", tasks, results, 0, CancellationToken.None);

        Assert.Equal(SupervisionDecision.Pass, verdict.Decision);
        Assert.Contains("证据充分", verdict.Reasons);
    }

    // ──────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────

    private static DmaeaRunContext Context(string goal) => new()
    {
        SessionId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        UserGoal = goal,
        TriggerMessageId = Guid.NewGuid(),
        RunId = Guid.NewGuid()
    };

    private static AgentDefinition Planner() => new() { Id = Guid.NewGuid(), Name = "planner", Layer = "planning", AgentType = "planner", Enabled = true };
    private static AgentDefinition Executor() => new() { Id = Guid.NewGuid(), Name = "executor", Layer = "execution", AgentType = "executor", Enabled = true };

    private sealed class FakeFactory : IAgentChatClientFactory
    {
        private readonly FakeChatResolver _resolver;
        private readonly IChatClient? _client;
        public FakeFactory(FakeChatResolver resolver, IChatClient? client)
        {
            _resolver = resolver;
            _client = client;
        }

        public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default)
            => _resolver.ResolveChatAsync(routePurpose, cancellationToken);

        public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default)
            => Task.FromResult<IChatClient>(_client ?? new StubChatClient("unused"));
    }

    private sealed class FakeChatResolver : IChatResolver
    {
        private readonly bool _available;
        private readonly string? _error;
        public FakeChatResolver(bool available, string? error = null)
        {
            _available = available;
            _error = error;
        }

        public Task<ChatResolution> ResolveChatAsync(string? routePurpose = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_available
                ? new ChatResolution { IsAvailable = true, BaseUrl = "http://localhost", Model = "fake", ApiKey = "x", ModelId = "openai/fake" }
                : new ChatResolution { IsAvailable = false, Error = _error ?? "Chat route unavailable." });
    }

    private sealed class StubChatClient : IChatClient
    {
        private readonly string _text;
        public StubChatClient(string text) => _text = text;

        public string? LastInstructions { get; private set; }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastInstructions = options?.Instructions;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => GetStreamingResponseAsyncCore(cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsyncCore(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(1, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, _text);
        }
    }

}
