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
