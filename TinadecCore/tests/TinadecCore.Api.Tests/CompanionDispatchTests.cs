using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Companion;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The companion's governed management surface: four verbs, all of them tenant-scoped, none of
/// them a code tool. The dispatch path is exercised through the real tool loop and the real
/// admission coordinator, so the assertions cover the durable facts the desktop supervisor board
/// reads — the user message, the run row and the <c>companion_dispatched</c> activity.
/// </summary>
public sealed class CompanionDispatchTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-companion-dispatch-tests", Guid.NewGuid().ToString("N"));
    private DispatchFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new DispatchFactory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private DispatchFactory Factory => _factory!;

    // ── the tool loop ──

    [Fact]
    public async Task CompanionDispatch_WritesTheUserMessage_StartsARun_AndRecordsTheActivity()
    {
        using var client = Factory.CreateClient();
        var (_, conversationId) = await CreateCompanionAsync(client);
        var targetSession = await CreateWorkSessionAsync(client, "修复登录页");
        const string task = "修复登录页的密码重置流程，并补一条回归测试。";

        Factory.Chat.EnqueueCompanion(
            [new FunctionCallContent("call-dispatch", CompanionToolCatalog.Dispatch, new Dictionary<string, object?>
            {
                ["session_id"] = targetSession.ToString(),
                ["task"] = task
            })],
            [new TextContent("已派活。")]);

        var companionRunId = await PostCompanionMessageAsync(client, conversationId, "把登录页的修复派给那个会话");
        Assert.Equal(1, await Factory.Services.GetRequiredService<CompanionRuntime>().RunPassAsync());

        // The companion's own turn settled with the closing text, not with the tool result.
        await using (var db = await Db<CompanionDbContext>(Factory).CreateDbContextAsync())
        {
            var run = await db.Runs.AsNoTracking().SingleAsync(x => x.Id == companionRunId);
            Assert.Equal("completed", run.Status);
            var reply = await db.Messages.AsNoTracking().SingleAsync(x => x.Id == run.ReplyMessageId!.Value);
            Assert.Equal("已派活。", reply.Body);
        }

        // The target session gained exactly one user message carrying the dispatched task.
        var messages = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{targetSession}/messages");
        var userMessages = messages!.Where(message => message.GetProperty("role").GetString() == "user").ToList();
        var dispatched = Assert.Single(userMessages);
        Assert.Equal(task, dispatched.GetProperty("content").GetString());

        // ... and one root run was started for it, inside this tenant.
        var scope = Factory.Tenant;
        await using (var db = await Db<LifecycleDbContext>(Factory).CreateDbContextAsync())
        {
            var rootRuns = await db.Runs.AsNoTracking()
                .Where(x => x.SessionId == targetSession && x.ParentRunId == null)
                .ToListAsync();
            var workRun = Assert.Single(rootRuns);
            Assert.Equal(scope.TenantId, workRun.TenantId);
            Assert.Equal(scope.WorkspaceId, workRun.WorkspaceId);
        }

        // The supervisor board learns about the dispatch from one activity with the contract payload.
        await using (var db = await Db<CompanionDbContext>(Factory).CreateDbContextAsync())
        {
            var activity = await db.Activities.AsNoTracking()
                .SingleAsync(x => x.Kind == CompanionRuntime.DispatchActivityKind);
            var payload = JsonDocument.Parse(activity.PayloadJson!).RootElement;
            Assert.Equal(targetSession, payload.GetProperty("session_id").GetGuid());
            Assert.NotEqual(Guid.Empty, payload.GetProperty("run_id").GetGuid());
            Assert.Equal("修复登录页", payload.GetProperty("session_title").GetString());
            Assert.Equal(task, payload.GetProperty("task_preview").GetString());
            Assert.Equal(conversationId, activity.ConversationId);
        }
    }

    /// <summary>
    /// The environment probes answer from real state, and they are how a companion decides whether
    /// the same work is already under way — so the transcript and the workspace list must be real
    /// rows, not synthesized placeholders.
    /// </summary>
    [Fact]
    public async Task SessionDetail_ReportsTheTranscript_AndWorkspacesListCountsSessions()
    {
        using var client = Factory.CreateClient();
        var project = await CreateProjectAsync(client, "统计项目");
        var targetSession = await CreateWorkSessionAsync(client, "数据整理", project.GetProperty("id").GetGuid());
        var gateway = Factory.Services.GetRequiredService<ICompanionWorkGateway>();

        await client.PostAsJsonAsync($"/api/v1/sessions/{targetSession}/messages", new { content = "先把上周的导出整理一下。" });

        var detail = await gateway.ReadSessionAsync(targetSession, 5);
        Assert.True(detail.IsSuccess);
        var messages = detail.Value!.RecentMessages;
        Assert.Contains(messages, message => message.Content.Contains("上周的导出", StringComparison.Ordinal));
        // The slice is capped by what the caller asked for, never the whole conversation.
        Assert.True(messages.Count <= 5);

        var workspaces = await gateway.ListWorkspacesAsync();
        Assert.True(workspaces.IsSuccess);
        var listed = Assert.Single(workspaces.Value!, item => item.ProjectId == project.GetProperty("id").GetGuid());
        Assert.Equal(1, listed.ActiveSessions);

        // A session outside this tenant is refused exactly like a missing one.
        var foreign = await gateway.ReadSessionAsync(Guid.NewGuid(), 5);
        Assert.False(foreign.IsSuccess);
        Assert.Equal("session_not_found", foreign.Error);
    }

    /// <summary>
    /// The companion effect queue is at-least-once, so a turn replayed after a failed model call
    /// must not append a second task message. The gateway honours the caller's key, and the
    /// coordinator's own append is idempotent on it.
    /// </summary>
    [Fact]
    public async Task Dispatch_WithTheSameKeyTwice_AppendsOneMessageAndOneRun()
    {
        using var client = Factory.CreateClient();
        await CreateCompanionAsync(client);
        var targetSession = await CreateWorkSessionAsync(client, "重复派活");
        var gateway = Factory.Services.GetRequiredService<ICompanionWorkGateway>();
        const string task = "整理本周的数据统计。";
        const string key = "companion-dispatch-retry-probe";

        var first = await gateway.DispatchAsync(targetSession, task, key);
        var second = await gateway.DispatchAsync(targetSession, task, key);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value!.RunId, second.Value!.RunId);

        var messages = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{targetSession}/messages");
        var userMessages = messages!.Where(message => message.GetProperty("role").GetString() == "user").ToList();
        Assert.Single(userMessages);

        await using var db = await Db<LifecycleDbContext>(Factory).CreateDbContextAsync();
        Assert.Single(await db.Runs.AsNoTracking()
            .Where(x => x.SessionId == targetSession && x.ParentRunId == null)
            .ToListAsync());
    }

    [Fact]
    public async Task UnknownTool_IsRefusedStructurally_AndTheTurnStillCompletes()
    {
        using var client = Factory.CreateClient();
        var (_, conversationId) = await CreateCompanionAsync(client);
        Factory.Chat.EnqueueCompanion(
            [new FunctionCallContent("call-code", "read_file", new Dictionary<string, object?> { ["filepath"] = "src/app.ts" })],
            [new TextContent("我只有管理工具。")]);

        var companionRunId = await PostCompanionMessageAsync(client, conversationId, "读一下代码");
        Assert.Equal(1, await Factory.Services.GetRequiredService<CompanionRuntime>().RunPassAsync());

        await using var db = await Db<CompanionDbContext>(Factory).CreateDbContextAsync();
        var run = await db.Runs.AsNoTracking().SingleAsync(x => x.Id == companionRunId);
        Assert.Equal("completed", run.Status);

        // The model received a structured refusal in the tool message, and the turn went on.
        var calls = Factory.Chat.Snapshot();
        var secondCall = calls[^1];
        var toolMessage = secondCall[^1];
        Assert.Equal(ChatRole.Tool, toolMessage.Role);
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(toolMessage.Contents));
        Assert.Contains("unknown_tool", result.Result?.ToString());

        Assert.DoesNotContain(await db.Activities.AsNoTracking().ToListAsync(),
            activity => activity.Kind == CompanionRuntime.DispatchActivityKind);
    }

    [Fact]
    public void CompanionToolSurface_IsExactlyTheSixManagementTools()
    {
        var ids = CompanionToolCatalog.Tools.Select(tool => tool.Id).ToArray();
        Assert.Equal(
            new[]
            {
                "companion_list_sessions", "companion_read_run", "companion_dispatch",
                // Read-only environment probes: the companion asks for them when it needs them.
                "companion_list_workspaces", "companion_session_detail",
                "companion_create_workspace"
            },
            ids);

        // The hard boundary: no code tool can ever appear on this surface, however it is spelled.
        string[] forbidden =
        [
            "read_file", "write_file", "shell", "command_run", "git_status", "git_commit", "git_diff",
            "git_push", "mcp_invoke", "mcp_search", "browser_navigate", "web_fetch"
        ];
        Assert.All(ids, id => Assert.StartsWith("companion_", id));
        Assert.All(ids, id => Assert.DoesNotContain(id, forbidden));
        Assert.All(CompanionToolCatalog.Tools, tool =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.Equal(JsonValueKind.Object, tool.InputSchema.ValueKind);
        });
    }

    // ── tenancy ──

    [Fact]
    public async Task ListSessions_ReturnsOnlyCurrentTenantSessions_MostRecentlyActiveFirst()
    {
        using var client = Factory.CreateClient();
        var first = await CreateWorkSessionAsync(client, "First session");
        var second = await CreateWorkSessionAsync(client, "Second session");
        var foreign = Guid.NewGuid();

        await using (var db = await Db<MemoryDbContext>(Factory).CreateDbContextAsync())
        {
            // A session in another tenant that must never be listed.
            db.Sessions.Add(new SessionRecord
            {
                Id = foreign,
                TenantId = Guid.NewGuid(),
                WorkspaceId = Guid.NewGuid(),
                Title = "Foreign session",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            // Make the ordering deterministic: activity is what moves a session to the top.
            var row = await db.Sessions.SingleAsync(x => x.Id == first);
            row.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(5);
            await db.SaveChangesAsync();
        }

        var gateway = Factory.Services.GetRequiredService<ICompanionWorkGateway>();
        var listed = await gateway.ListSessionsAsync();
        Assert.True(listed.IsSuccess, listed.Message);
        Assert.Equal(new[] { first, second }, listed.Value!.Select(session => session.Id).ToArray());
        Assert.DoesNotContain(listed.Value, session => session.Id == foreign);
        Assert.All(listed.Value, session => Assert.Null(session.LatestRun));

        // The latest root run is projected; a newer child run is not the session's latest run.
        var rootRun = Guid.NewGuid();
        var scope = Factory.Tenant;
        await using (var db = await Db<LifecycleDbContext>(Factory).CreateDbContextAsync())
        {
            db.Runs.Add(new RunRecord
            {
                Id = rootRun,
                TenantId = scope.TenantId,
                WorkspaceId = scope.WorkspaceId,
                SessionId = first,
                TriggerMessageId = Guid.NewGuid(),
                Status = "executing",
                Summary = "working",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                InitiatedByPrincipalId = scope.PrincipalId
            });
            db.Runs.Add(new RunRecord
            {
                Id = Guid.NewGuid(),
                TenantId = scope.TenantId,
                WorkspaceId = scope.WorkspaceId,
                SessionId = first,
                ParentRunId = rootRun,
                TriggerMessageId = Guid.NewGuid(),
                Status = "executing",
                Summary = "child",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1),
                InitiatedByPrincipalId = scope.PrincipalId
            });
            await db.SaveChangesAsync();
        }

        var again = await gateway.ListSessionsAsync();
        var withRun = again.Value!.Single(session => session.Id == first);
        Assert.NotNull(withRun.LatestRun);
        Assert.Equal(rootRun, withRun.LatestRun!.Id);
        Assert.Equal("executing", withRun.LatestRun.Status);
        Assert.Equal("working", withRun.LatestRun.Summary);

        var read = await gateway.ReadRunAsync(rootRun);
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(first, read.Value!.SessionId);
        Assert.Equal("executing", read.Value.Status);
        Assert.Equal("working", read.Value.Summary);
    }

    [Fact]
    public async Task CreateWorkspace_CreatesTheDirectory_AndReusesAnExistingRoot()
    {
        using var client = Factory.CreateClient();
        var gateway = Factory.Services.GetRequiredService<ICompanionWorkGateway>();
        var path = Path.Combine(_root, "new-workspace");

        var created = await gateway.CreateWorkspaceAsync("New workspace", path);
        Assert.True(created.IsSuccess, created.Message);
        Assert.True(Directory.Exists(path));
        Assert.Equal("New workspace", created.Value!.Name);
        Assert.Equal(Path.TrimEndingDirectorySeparator(path), created.Value.Path);

        // The same root is reused, and the registered project's name wins over the new request's.
        var again = await gateway.CreateWorkspaceAsync("Another name", path);
        Assert.True(again.IsSuccess, again.Message);
        Assert.Equal(created.Value.ProjectId, again.Value!.ProjectId);
        Assert.Equal("New workspace", again.Value.Name);

        var relative = await gateway.CreateWorkspaceAsync("Bad", "relative/path");
        Assert.False(relative.IsSuccess);
        Assert.Equal("invalid_arguments", relative.Error);
    }

    [Fact]
    public async Task ForeignTenantSessionAndRun_AreRefusedExactlyLikeMissingOnes()
    {
        using var client = Factory.CreateClient();
        var gateway = Factory.Services.GetRequiredService<ICompanionWorkGateway>();
        var foreignSession = Guid.NewGuid();
        var foreignRun = Guid.NewGuid();

        await using (var db = await Db<MemoryDbContext>(Factory).CreateDbContextAsync())
        {
            db.Sessions.Add(new SessionRecord
            {
                Id = foreignSession,
                TenantId = Guid.NewGuid(),
                WorkspaceId = Guid.NewGuid(),
                Title = "Foreign session",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        await using (var db = await Db<LifecycleDbContext>(Factory).CreateDbContextAsync())
        {
            db.Runs.Add(new RunRecord
            {
                Id = foreignRun,
                TenantId = Guid.NewGuid(),
                WorkspaceId = Guid.NewGuid(),
                SessionId = foreignSession,
                TriggerMessageId = Guid.NewGuid(),
                Status = "executing",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                InitiatedByPrincipalId = Guid.NewGuid()
            });
            await db.SaveChangesAsync();
        }

        var foreignDispatch = await gateway.DispatchAsync(foreignSession, "do the work", "test-key-foreign");
        var missingDispatch = await gateway.DispatchAsync(Guid.NewGuid(), "do the work", "test-key-missing");
        Assert.False(foreignDispatch.IsSuccess);
        Assert.Equal("session_not_found", foreignDispatch.Error);
        Assert.Equal(missingDispatch.Error, foreignDispatch.Error);

        var foreignRead = await gateway.ReadRunAsync(foreignRun);
        var missingRead = await gateway.ReadRunAsync(Guid.NewGuid());
        Assert.False(foreignRead.IsSuccess);
        Assert.Equal("run_not_found", foreignRead.Error);
        Assert.Equal(missingRead.Error, foreignRead.Error);

        // A refused dispatch must not leave a message or a second run behind.
        await using (var db = await Db<MemoryDbContext>(Factory).CreateDbContextAsync())
        {
            Assert.False(await db.Messages.AnyAsync(x => x.SessionId == foreignSession));
        }
        await using (var db = await Db<LifecycleDbContext>(Factory).CreateDbContextAsync())
        {
            Assert.Single(await db.Runs.Where(x => x.SessionId == foreignSession).ToListAsync());
        }
    }

    // ── helpers ──

    private static IDbContextFactory<T> Db<T>(DispatchFactory factory) where T : DbContext =>
        factory.Services.GetRequiredService<IDbContextFactory<T>>();

    private static async Task<(Guid CompanionId, Guid ConversationId)> CreateCompanionAsync(HttpClient client, string name = "A")
    {
        using var created = await client.PostAsJsonAsync("/api/v1/companions", new { name });
        Assert.Equal(System.Net.HttpStatusCode.OK, created.StatusCode);
        var companionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var conversation = await client.PostAsJsonAsync($"/api/v1/companions/{companionId}/conversations", new { kind = "private" });
        Assert.Equal(System.Net.HttpStatusCode.OK, conversation.StatusCode);
        return (companionId, (await conversation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    private static async Task<Guid> PostCompanionMessageAsync(HttpClient client, Guid conversationId, string body)
    {
        using var response = await client.PostAsJsonAsync($"/api/v1/companion-conversations/{conversationId}/messages", new { body });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return Assert.Single(payload.GetProperty("runs").EnumerateArray()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateWorkSessionAsync(HttpClient client, string title, Guid? projectId = null)
    {
        var project = projectId ?? (await CreateProjectAsync(client, title + " project")).GetProperty("id").GetGuid();
        using var sessionResponse = await client.PostAsJsonAsync("/api/v1/sessions", new
        {
            project_id = project,
            title
        });
        Assert.Equal(System.Net.HttpStatusCode.Created, sessionResponse.StatusCode);
        var session = await sessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        return session.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> CreateProjectAsync(HttpClient client, string name)
    {
        using var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new
        {
            name,
            path = Path.Combine(Path.GetTempPath(), "tinadec-companion-dispatch-project-" + Guid.NewGuid().ToString("N"))
        });
        Assert.Equal(System.Net.HttpStatusCode.Created, projectResponse.StatusCode);
        return await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
    }

    private sealed class DispatchFactory(string root) : IsolatedApiFactory
    {
        public ScriptedChat Chat { get; } = new();

        public TenantContext Tenant => Services.GetRequiredService<ITenantContextAccessor>().Current;

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                // Tests drive the companion drain one pass at a time.
                ["TinadecCompanion:RunDrainEnabled"] = "false",
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IAgentChatClientFactory>(new ScriptedChatFactory(Chat));
                // A dispatched run must not reach a real TinadecTools child process in this test.
                services.AddSingleton<IToolManifestSnapshotResolver, EmptyToolManifestSnapshotResolver>();
            });
        }
    }

    private sealed class ScriptedChatFactory(ScriptedChat chat) : IAgentChatClientFactory
    {
        public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResolution
            {
                IsAvailable = true,
                BaseUrl = "http://localhost",
                Model = "scripted",
                ApiKey = "scripted",
                ModelId = "scripted/companion"
            });

        public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default) =>
            Task.FromResult<IChatClient>(chat);
    }

    /// <summary>
    /// Queued replies for the companion turn; every other caller (a dispatched work run, an
    /// approval gate) gets a plain answer so the two conversations never share a script.
    /// </summary>
    private sealed class ScriptedChat : IChatClient
    {
        private readonly Queue<AIContent[]> _companionResponses = new();
        private readonly List<IReadOnlyList<ChatMessage>> _calls = [];
        private readonly object _gate = new();

        public void EnqueueCompanion(params AIContent[][] responses)
        {
            lock (_gate)
                foreach (var response in responses)
                    _companionResponses.Enqueue(response);
        }

        public IReadOnlyList<IReadOnlyList<ChatMessage>> Snapshot()
        {
            lock (_gate) return _calls.ToList();
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            lock (_gate)
            {
                _calls.Add(list);
                if (options?.Instructions?.Contains("你是伙伴域中的伙伴", StringComparison.Ordinal) == true)
                {
                    var next = _companionResponses.Count > 0
                        ? _companionResponses.Dequeue()
                        : [new TextContent("好的。")];
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, next)));
                }
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "工作会话已收到。")));
            }
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "工作会话已收到。");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class EmptyToolManifestSnapshotResolver : IToolManifestSnapshotResolver
    {
        private static readonly ToolManifestSnapshot Snapshot = new(
            2,
            ToolManifestHasher.Compute(Array.Empty<FrozenToolManifestEntry>()),
            []);

        public Task<ToolManifestSnapshot> ResolveAsync(
            ToolManifestSnapshotRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
    }
}
