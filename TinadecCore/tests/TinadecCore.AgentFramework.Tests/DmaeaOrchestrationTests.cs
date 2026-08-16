using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Persistence;
using TinadecCore.Runtime;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// DmaEA runtime tests. Planning/execution agents run against a fake chat client via the
/// injected chat-client factory (no HTTP); the orchestrator integration test runs the full
/// composition with a real SQLite database and an unavailable chat route, asserting the
/// run.started/run.failed audit trail and the running (not completed) run status.
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
        var planner = new PlanningAgent(resolver, chatClientFactory: _ => client);
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
        var planner = new PlanningAgent(resolver, chatClientFactory: _ => client);

        var tasks = await planner.PlanAsync(Context("用户目标"), [Planner()], CancellationToken.None);

        var task = Assert.Single(tasks);
        Assert.Equal("用户目标", task.Title);
        Assert.Equal("Task is complete when the goal is satisfied", task.SuccessCriteria[0]);
    }

    [Fact]
    public async Task PlanningAgent_ThrowsWhenChatRouteUnavailable()
    {
        var resolver = new FakeChatResolver(false, "Provider API key is not stored.");
        var planner = new PlanningAgent(resolver, chatClientFactory: _ => new StubChatClient("unused"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => planner.PlanAsync(Context("用户目标"), [Planner()], CancellationToken.None));
        Assert.Equal("Provider API key is not stored.", ex.Message);
    }

    // ──────────────────────────────────────────────────────────
    // Execution layer
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecutionAgent_ReturnsCompletedResultWithSummary()
    {
        var resolver = new FakeChatResolver(true);
        var client = new StubChatClient("任务A 完成");
        var executor = new ExecutionAgent(resolver, chatClientFactory: _ => client);
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
        var executor = new ExecutionAgent(resolver);
        var task = new PlannedTask { Title = "任务A" };

        var result = await executor.ExecuteAsync(Context("用户目标"), Executor(), task, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal("No chat model route is configured for this workspace.", result.Summary);
    }

    // ──────────────────────────────────────────────────────────
    // Orchestrator integration
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Orchestrator_FailedRunIsAuditedAndKeepsRunningStatus()
    {
        var root = Path.Combine(Path.GetTempPath(), "tinadec-dmaea-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var services = new ServiceCollection();
            var configuration = new EmptyConfiguration();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            services.AddTinadecPersistence(configuration, root);
            services.AddTinadecCore();
            services.AddSingleton<IChatResolver>(new FakeChatResolver(false, "Provider API key is not stored."));
            var provider = services.BuildServiceProvider();

            Directory.CreateDirectory(Path.Combine(root, "data"));
            await provider.GetRequiredService<IStorageMigrationRunner>().RunAsync();
            var tenant = provider.GetRequiredService<ITenantContextAccessor>().Current;
            var store = provider.GetRequiredService<ProjectSessionStore>();
            var project = await store.CreateProjectAsync("Dmaea project", Path.Combine(root, "workspace"));
            var session = await store.CreateSessionAsync(project.Id, "Dmaea session");
            await SeedAgentsAsync(provider, tenant);

            var orchestrator = provider.GetRequiredService<IAgentOrchestrator>();
            var result = await orchestrator.OrchestrateAsync(session.Id.ToString(), "用户目标");

            Assert.False(result.Success);
            Assert.False(string.IsNullOrEmpty(result.RunId));

            var lifecycle = provider.GetRequiredService<StorageLifecycleService>();
            var events = await lifecycle.ReplayEventsAsync(session.Id, 0);
            Assert.Contains(events, e => e.EventType == "run.started");
            Assert.Contains(events, e => e.EventType == "run.failed");

            var run = await lifecycle.FindRunAsync(Guid.Parse(result.RunId));
            Assert.NotNull(run);
            Assert.Equal("running", run.Status);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
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

    private static async Task SeedAgentsAsync(IServiceProvider provider, TenantContext tenant)
    {
        var content = provider.GetRequiredService<IContentStore>();
        var now = DateTimeOffset.UtcNow;
        await using var db = await provider.GetRequiredService<IDbContextFactory<AgentControlDbContext>>().CreateDbContextAsync();
        foreach (var (name, layer, agentType) in new[] { ("planner", "planning", "planner"), ("executor", "execution", "executor") })
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["mode"] = "default",
                ["description"] = name,
                ["model_route_purpose"] = "chat",
                ["allowed_tools"] = Array.Empty<string>(),
                ["capabilities"] = Array.Empty<string>(),
                ["system_prompt"] = "prompt"
            });
            await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(body));
            var stored = await content.PutAsync(new ContentWriteRequest(tenant.TenantId, tenant.WorkspaceId, "agent-profile", "application/json", ms));
            var row = new AgentProfileRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId,
                WorkspaceId = tenant.WorkspaceId,
                Scope = "workspace",
                Name = name,
                Layer = layer,
                AgentType = agentType,
                Enabled = true,
                Revision = 0,
                CreatedByPrincipalId = tenant.PrincipalId,
                CreatedAt = now
            };
            var version = new AgentProfileVersionRecord
            {
                Id = Guid.NewGuid(),
                AgentId = row.Id,
                Version = 1,
                ContentReference = stored.Value,
                ContentHash = stored.Sha256,
                ContentLength = stored.Length,
                CreatedByPrincipalId = tenant.PrincipalId,
                CreatedAt = now
            };
            row.CurrentVersionId = version.Id;
            db.Agents.Add(row);
            db.Versions.Add(version);
        }
        await db.SaveChangesAsync();
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

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _text)));

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

    private sealed class EmptyConfiguration : IConfiguration
    {
        public string? this[string key] { get => null; set { } }
        public IEnumerable<IConfigurationSection> GetChildren() => [];
        public IChangeToken GetReloadToken() => new EmptyChangeToken();
        public IConfigurationSection GetSection(string key) => new EmptySection(key);
    }

    private sealed class EmptySection : IConfigurationSection
    {
        private readonly string _key;
        public EmptySection(string key) => _key = key;

        public string? this[string key] { get => null; set { } }
        public string Key => _key;
        public string Path => _key;
        public string? Value { get => null; set { } }
        public IEnumerable<IConfigurationSection> GetChildren() => [];
        public IChangeToken GetReloadToken() => new EmptyChangeToken();
        public IConfigurationSection GetSection(string key) => new EmptySection(_key + ":" + key);
    }

    private sealed class EmptyChangeToken : IChangeToken
    {
        public bool HasChanged => false;
        public bool ActiveChangeCallbacks => false;
        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) => new NoopDisposable();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
