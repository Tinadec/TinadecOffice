using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Companion;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Lifecycle;

namespace TinadecCore.Api.Tests;

/// <summary>
/// High-signal Task #37 + #38 + Phase 11.5 (#4-#5) verification for the Companion Command Hub:
/// 1) <c>work_session</c> &amp; <c>browser</c> connector grants (<c>"*"</c> wildcard vs per-project scoping),
///    <see cref="ICompanionWorkGateway"/> verbs (<c>ListModesAsync</c>, <c>CreateSessionAsync</c>,
///    <c>SteerRunAsync</c>, <c>CancelRunAsync</c>, <c>SideQuestionAsync</c>, <c>ListApprovalsAsync</c>,
///    <c>CreateWorkspaceAsync</c>), and <c>GET /api/v1/companions/{id}/work-sessions</c> + <c>/browser-profiles</c>.
/// 2) Commander restraint prompt + unified <see cref="IContextMemoryService"/> (<c>scope = "companion"</c>)
///    memory tools (<c>companion_memory_remember</c>, <c>companion_memory_search</c>, <c>companion_project_note</c>).
/// 3) Event-driven <see cref="ICompanionSessionBackflow"/> <c>companion.work_session</c> SSE frames &amp;
///    selective wakeups on <c>awaiting_approval</c>, <c>browser_handoff</c>, and <c>completed</c>.
/// </summary>
public sealed class CompanionCommandHubWave1Tests : IDisposable
{
    private readonly string _root = ApiTestStorage.CreateRoot("companion-hub");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task WorkSessionAndBrowserGrants_EnforceWildcardVsProjectScope_AndSupportAllGatewayCommandVerbs()
    {
        using var factory = new HubFactory(Path.Combine(_root, "grants"));
        using var client = factory.CreateClient();
        var gateway = factory.Services.GetRequiredService<ICompanionWorkGateway>();

        var mainBot = await CreateCompanionAsync(client, "Commander");
        var scopedBot = await CreateCompanionAsync(client, "ScopedSpecialist");

        // Set mainBot as default/main companion -> auto-seeds "*" work_session grant lazily.
        await SetMainCompanionAsync(client, mainBot);

        var mainConnections = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/companions/{mainBot}/connections");
        var wildcardBinding = Assert.Single(mainConnections!, c => c.GetProperty("kind").GetString() == "work_session");
        Assert.Equal("*", wildcardBinding.GetProperty("endpoint").GetString());
        Assert.Equal("connected", wildcardBinding.GetProperty("runtime_status").GetString());

        // Bind a #38 browser connector on mainBot and verify /browser-profiles endpoint.
        using var addBrowser = await client.PostAsJsonAsync($"/api/v1/companions/{mainBot}/connections", new
        {
            kind = "browser",
            name = "Built-in Electron Browser",
            driver = "builtin_electron",
            profile_id = "persist:tinadec-agent-browser",
            authorization = "authorized",
            enabled = true
        });
        addBrowser.EnsureSuccessStatusCode();
        var createdBrowser = await addBrowser.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("builtin_electron", createdBrowser.GetProperty("driver").GetString());
        Assert.Equal("connected", createdBrowser.GetProperty("runtime_status").GetString());

        var profiles = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/companions/browser-profiles");
        Assert.NotNull(profiles);
        Assert.Contains(profiles!, p => p.GetProperty("driver").GetString() == "builtin_electron");

        // Create two projects: ProjA and ProjB. Grant scopedBot ONLY ProjA.
        var projAPath = Path.Combine(_root, "proj-a");
        var projBPath = Path.Combine(_root, "proj-b");
        Directory.CreateDirectory(projAPath);
        Directory.CreateDirectory(projBPath);
        var projA = (await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "ProjA", path = projAPath })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var projB = (await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "ProjB", path = projBPath })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var addProjAGrant = await client.PostAsJsonAsync($"/api/v1/companions/{scopedBot}/connections", new
        {
            kind = "work_session",
            name = "ProjA Only",
            endpoint = projA.ToString(),
            authorization = "authorized",
            enabled = true
        });
        addProjAGrant.EnsureSuccessStatusCode();

        // Modes list succeeds
        var modesRes = await gateway.ListModesAsync(scopedBot);
        Assert.True(modesRes.IsSuccess, modesRes.Message);
        Assert.NotEmpty(modesRes.Value!);

        // scopedBot can create & command sessions in ProjA (including user-created manual sessions),
        // but is refused on ProjB and refused on CreateWorkspaceAsync (which requires "*").
        var createdSessionA = await gateway.CreateSessionAsync(scopedBot, projA, "Scoped ProjA Task", modesRes.Value![0].ModeVersionId);
        Assert.True(createdSessionA.IsSuccess, createdSessionA.Message);
        var sessionAId = createdSessionA.Value!.Id;

        var manualSessionBResp = await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = projB, title = "Manual ProjB Session" });
        var sessionBId = (await manualSessionBResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.False((await gateway.CreateSessionAsync(scopedBot, projB, "Unauthorized ProjB", null)).IsSuccess);
        Assert.False((await gateway.ReadSessionAsync(scopedBot, sessionBId, 5)).IsSuccess);
        Assert.False((await gateway.CreateWorkspaceAsync(scopedBot, "Unauthorized Root", Path.Combine(_root, "proj-c"))).IsSuccess);
        Assert.True((await gateway.CreateWorkspaceAsync(mainBot, "Authorized Wildcard Root", Path.Combine(_root, "proj-c"))).IsSuccess);

        // SideQuestionAsync, SteerRunAsync (follow-up on idle session), CancelRunAsync, and ListApprovalsAsync on authorized ProjA:
        var sideAns = await gateway.SideQuestionAsync(scopedBot, sessionAId, null, "What is the current status?");
        Assert.True(sideAns.IsSuccess, sideAns.Message);
        Assert.Equal(sessionAId, sideAns.Value!.SessionId);

        var approvalsRes = await gateway.ListApprovalsAsync(scopedBot, sessionAId);
        Assert.True(approvalsRes.IsSuccess, approvalsRes.Message);

        var dispatched = await gateway.DispatchAsync(scopedBot, sessionAId, "Start initial task", "hub-disp-1");
        Assert.True(dispatched.IsSuccess, dispatched.Message);
        var runId = dispatched.Value!.RunId;

        var steered = await gateway.SteerRunAsync(scopedBot, sessionAId, runId, "Focus on unit tests first", "hub-steer-1");
        Assert.True(steered.IsSuccess, steered.Message);

        var cancelled = await gateway.CancelRunAsync(scopedBot, sessionAId, runId, "Pause for review");
        Assert.True(cancelled.IsSuccess, cancelled.Message);

        // GET /api/v1/companions/{id}/work-sessions scopes strictly to authorized projects:
        var scopedSessionsHttp = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/companions/{scopedBot}/work-sessions");
        Assert.Contains(scopedSessionsHttp!, s => s.GetProperty("id").GetGuid() == sessionAId);
        Assert.DoesNotContain(scopedSessionsHttp!, s => s.GetProperty("id").GetGuid() == sessionBId);

        var mainSessionsHttp = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/companions/{mainBot}/work-sessions");
        Assert.Contains(mainSessionsHttp!, s => s.GetProperty("id").GetGuid() == sessionAId);
        Assert.Contains(mainSessionsHttp!, s => s.GetProperty("id").GetGuid() == sessionBId);
    }

    [Fact]
    public async Task CommanderPromptRestraint_AndUnifiedCompanionMemoryTools_EnforceL2WritePolicy()
    {
        using var factory = new HubFactory(Path.Combine(_root, "memory"));
        using var client = factory.CreateClient();
        var runtime = factory.Services.GetRequiredService<CompanionRuntime>();

        var projDir = Path.Combine(_root, "proj-mem-workspace");
        Directory.CreateDirectory(projDir);
        using var projResp = await client.PostAsJsonAsync("/api/v1/projects", new { name = "MemProj", path = projDir });
        projResp.EnsureSuccessStatusCode();
        var projId = (await projResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var snakeJson = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        // Seed L3 principal & L2 project memory over HTTP so they share the exact HTTP tenant context.
        (await client.PostAsJsonAsync("/api/v1/memories/items", new UpsertMemoryItemRequest { Scope = "principal", Kind = "preference", Content = "USER-PREF-DARK-THEME" }, snakeJson)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/memories/items", new UpsertMemoryItemRequest { Scope = "project", Kind = "fact", ProjectId = projId, WorkspaceRoot = projDir, Content = "PROJ-FACT-STRICT-TYPES" }, snakeJson)).EnsureSuccessStatusCode();

            var botId = await CreateCompanionAsync(client, "ChiefOfStaff");
            await SetMainCompanionAsync(client, botId);
            var convId = (await (await client.PostAsJsonAsync($"/api/v1/companions/{botId}/conversations", new { kind = "private" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

            // Turn 1: companion_memory_remember (add) + companion_memory_search + companion_project_note (refused while companion_can_write_project_memory == false)
            factory.Chat.EnqueueCompanionTurn(
                new FunctionCallContent("m-rem", CompanionToolCatalog.MemoryRemember, new Dictionary<string, object?>
                {
                    ["action"] = "add",
                    ["kind"] = "bond",
                    ["content"] = "PRIVATE-COMPANION-BOND-42"
                }),
                new FunctionCallContent("m-search", CompanionToolCatalog.MemorySearch, new Dictionary<string, object?>
                {
                    ["query"] = "STRICT-TYPES",
                    ["scope"] = "all",
                    ["project_id"] = projId.ToString()
                }),
                new FunctionCallContent("m-note-blocked", CompanionToolCatalog.ProjectNote, new Dictionary<string, object?>
                {
                    ["project_id"] = projId.ToString(),
                    ["content"] = "Should be refused while read-only"
                }));
            factory.Chat.EnqueueCompanionTurn(new TextContent("记忆已更新并确认项目只读边界。"));

            using var msg1 = await client.PostAsJsonAsync($"/api/v1/companion-conversations/{convId}/messages", new { body = "记录伙伴记忆并检查项目记忆" });
            Assert.Equal(HttpStatusCode.OK, msg1.StatusCode);
            Assert.Equal(1, await runtime.RunPassAsync());

            // Verify Commander prompt contains Respectful Restraint + L3 principal profile + new tools
            var firstCall = factory.Chat.CompanionCalls.First();
            Assert.Contains("尊重与克制原则", firstCall.Instructions, StringComparison.Ordinal);
            Assert.Contains("USER-PREF-DARK-THEME", firstCall.Instructions, StringComparison.Ordinal);
            Assert.Contains("companion_steer_run", firstCall.ToolNames);
            Assert.Contains("companion_memory_remember", firstCall.ToolNames);

            // Verify tool results: private memory stored in unified IContextMemoryService(scope="companion"), search hit L2, and project_note returned project_memory_read_only
            var allToolOutputs = string.Join("\n", factory.Chat.CompanionToolOutputs);
            Assert.Contains("PRIVATE-COMPANION-BOND-42", allToolOutputs, StringComparison.Ordinal);
            Assert.Contains("PROJ-FACT-STRICT-TYPES", allToolOutputs, StringComparison.Ordinal);
            Assert.Contains("project_memory_read_only", allToolOutputs, StringComparison.Ordinal);

            var companionItems = await client.GetFromJsonAsync<MemoryItemListResultDto>($"/api/v1/memories/items?scope=companion&companion_id={botId}", snakeJson);
            Assert.Contains(companionItems!.Items, x => x.Content.Contains("PRIVATE-COMPANION-BOND-42", StringComparison.Ordinal));

            // Enable companion_can_write_project_memory = true -> companion_project_note now succeeds with provenance!
            using var enableWrite = await client.PutAsJsonAsync("/api/v1/context-memory/policy", new UpdateContextMemoryPolicyRequest
            {
                CompanionCanWriteProjectMemory = true
            }, snakeJson);
            enableWrite.EnsureSuccessStatusCode();

            factory.Chat.EnqueueCompanionTurn(
                new FunctionCallContent("m-note-ok", CompanionToolCatalog.ProjectNote, new Dictionary<string, object?>
                {
                    ["project_id"] = projId.ToString(),
                    ["kind"] = "decision",
                    ["content"] = "COMPANION-PROVENANCE-NOTE-99"
                }));
            factory.Chat.EnqueueCompanionTurn(new TextContent("已写入项目笔记。"));

            using var msg2 = await client.PostAsJsonAsync($"/api/v1/companion-conversations/{convId}/messages", new { body = "现在写入项目笔记" });
            Assert.Equal(HttpStatusCode.OK, msg2.StatusCode);
            Assert.Equal(1, await runtime.RunPassAsync());

            var projItems = await client.GetFromJsonAsync<MemoryItemListResultDto>($"/api/v1/memories/items?scope=project&project_id={projId}", snakeJson);
            Assert.Contains(projItems!.Items, x => x.Content.Contains("COMPANION-PROVENANCE-NOTE-99", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompanionSessionBackflow_StreamsWorkSessionSse_AndWakesOnlyCommandingCompanion()
    {
        using var factory = new HubFactory(Path.Combine(_root, "backflow"));
        using var client = factory.CreateClient();
        var backflow = factory.Services.GetRequiredService<ICompanionSessionBackflow>();
        var tenant = factory.Services.GetRequiredService<ITenantContextAccessor>().Current;

        var botId = await CreateCompanionAsync(client, "SupervisorBot");
        using var setMain = await client.PatchAsJsonAsync("/api/v1/companion-settings", new { default_companion_id = botId });
        var convId = (await (await client.PostAsJsonAsync($"/api/v1/companions/{botId}/conversations", new { kind = "private" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var sseReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/companion-events?after_seq=0");
        using var sseResp = await client.SendAsync(sseReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, sseResp.StatusCode);
        await using var stream = await sseResp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);
        Assert.Contains("event: heartbeat", await ReadSseFrameAsync(reader, cts.Token));

        // 1. Commanded session transitions (awaiting_approval + browser_handoff) broadcast SSE AND enqueue wake runs on botId.
        var commandedSessionId = Guid.NewGuid();
        var commandedRunId = Guid.NewGuid();
        backflow.TrackCommandedSession(tenant.TenantId, botId, convId, commandedSessionId, commandedRunId);

        await backflow.NotifySessionTransitionAsync(new CompanionSessionBackflowEvent(
            tenant.TenantId,
            tenant.WorkspaceId,
            ProjectId: null,
            SessionId: commandedSessionId,
            SessionTitle: "Auth Refactor",
            RunId: commandedRunId,
            Status: "awaiting_approval",
            ModeName: "Team",
            Summary: "Waiting on shell approval",
            PlanSummary: "1/2 steps",
            PendingApprovals: [new CompanionPendingApprovalItem(Guid.NewGuid(), "shell", "bun test", "high", DateTimeOffset.UtcNow)]), cts.Token);

        await backflow.NotifySessionTransitionAsync(new CompanionSessionBackflowEvent(
            tenant.TenantId,
            tenant.WorkspaceId,
            ProjectId: null,
            SessionId: commandedSessionId,
            SessionTitle: "Auth Refactor",
            RunId: commandedRunId,
            Status: "browser_handoff",
            ModeName: "Team",
            Summary: "2FA login required on staging",
            PlanSummary: "1/2 steps"), cts.Token);

        // 2. Uncommanded manual user session broadcasts SSE for dock visibility, WITHOUT waking Companion Bot (respectful restraint!).
        var manualSessionId = Guid.NewGuid();
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<CompanionDbContext>>();
        int runsBeforeManual;
        await using (var db = await dbFactory.CreateDbContextAsync(cts.Token))
            runsBeforeManual = await db.Runs.CountAsync(x => x.CompanionId == botId, cts.Token);

        await backflow.NotifySessionTransitionAsync(new CompanionSessionBackflowEvent(
            tenant.TenantId,
            tenant.WorkspaceId,
            ProjectId: null,
            SessionId: manualSessionId,
            SessionTitle: "Manual User Scratch",
            RunId: Guid.NewGuid(),
            Status: "completed",
            ModeName: "Solo",
            Summary: "Manual session finished",
            PlanSummary: null), cts.Token);

        await using (var db = await dbFactory.CreateDbContextAsync(cts.Token))
        {
            var runsAfterManual = await db.Runs.CountAsync(x => x.CompanionId == botId, cts.Token);
            Assert.True(runsBeforeManual >= 1, "Commanded session transitions must wake the commanding Companion.");
            Assert.Equal(runsBeforeManual, runsAfterManual);
        }

        var frames = new List<string>();
        while (frames.Count < 12)
        {
            frames.Add(await ReadSseFrameAsync(reader, cts.Token));
            if (frames.Any(f => f.Contains("event: companion.work_session") && f.Contains("awaiting_approval"))
                && frames.Any(f => f.Contains("event: companion.work_session") && f.Contains("browser_handoff")))
            {
                break;
            }
        }

        Assert.Contains(frames, f => f.Contains("event: companion.work_session") && f.Contains("\"status\":\"awaiting_approval\"") && f.Contains("bun test"));
        Assert.Contains(frames, f => f.Contains("event: companion.work_session") && f.Contains("\"status\":\"browser_handoff\"") && f.Contains("2FA login required"));
        cts.Cancel();
    }

    private static async Task<Guid> CreateCompanionAsync(HttpClient client, string name)
    {
        using var resp = await client.PostAsJsonAsync("/api/v1/companions", new { name });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task SetMainCompanionAsync(HttpClient client, Guid companionId)
    {
        var current = await client.GetFromJsonAsync<JsonElement>("/api/v1/companion-settings");
        var revision = current.GetProperty("revision").GetInt64();
        using var req = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/companion-settings")
        {
            Content = JsonContent.Create(new { main_companion_id = companionId })
        };
        req.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        using var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<string> ReadSseFrameAsync(StreamReader reader, CancellationToken ct)
    {
        var lines = new List<string>();
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0)
            {
                if (lines.Count > 0) return string.Join('\n', lines);
                continue;
            }
            lines.Add(line);
        }
        return string.Join('\n', lines);
    }

    private sealed class HubFactory(string root, bool managedStorage = false, string? hostToken = null) : IsolatedApiFactory
    {
        public ScriptedHubChat Chat { get; } = new();
        protected override bool UsesManagedStorage => managedStorage;
        protected override string? ManagedUserRoot => managedStorage ? root : null;

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            if (!string.IsNullOrWhiteSpace(hostToken))
                client.DefaultRequestHeaders.Add("X-Tinadec-Host-Control", hostToken);
        }

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(root);
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["TinadecCompanion:RunDrainEnabled"] = "false",
                    ["Logging:LogLevel:Default"] = "Warning"
                };
                if (managedStorage)
                {
                    settings["TinadecStorage:Enabled"] = "true";
                    settings["TinadecStorage:UserRoot"] = root;
                }
                else
                {
                    settings["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db");
                    settings["TinadecPersistence:DataRoot"] = Path.Combine(root, "data");
                }
                cfg.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IAgentChatClientFactory>(new HubChatFactory(Chat));
                services.AddSingleton<IToolManifestSnapshotResolver, EmptyManifest>();
            });
        }

        private sealed class HubChatFactory(ScriptedHubChat chat) : IAgentChatClientFactory
        {
            public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken ct = default) =>
                Task.FromResult(new ChatResolution { IsAvailable = true, BaseUrl = "http://localhost", Model = "hub", ApiKey = "hub", ModelId = "hub/model" });
            public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken ct = default) =>
                Task.FromResult<IChatClient>(chat);
        }

        private sealed class EmptyManifest : IToolManifestSnapshotResolver
        {
            private static readonly ToolManifestSnapshot Snap = new(2, ToolManifestHasher.Compute(Array.Empty<FrozenToolManifestEntry>()), []);
            public Task<ToolManifestSnapshot> ResolveAsync(ToolManifestSnapshotRequest request, CancellationToken ct = default) => Task.FromResult(Snap);
        }
    }

    private sealed record CapturedCompanionCall(string Instructions, string ToolNames);

    private sealed class ScriptedHubChat : IChatClient
    {
        private readonly ConcurrentQueue<AIContent[]> _turns = new();
        public ConcurrentQueue<CapturedCompanionCall> CompanionCalls { get; } = new();
        public ConcurrentQueue<string> CompanionToolOutputs { get; } = new();

        public void EnqueueCompanionTurn(params AIContent[] contents) => _turns.Enqueue(contents);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var list = messages.ToList();
            if (options?.Instructions?.Contains("你是伙伴域中的", StringComparison.Ordinal) == true)
            {
                CompanionCalls.Enqueue(new CapturedCompanionCall(
                    options.Instructions,
                    options.Tools is { Count: > 0 } t ? string.Join(",", t.Select(x => x.Name)) : string.Empty));
                foreach (var res in list.SelectMany(m => m.Contents).OfType<FunctionResultContent>())
                    CompanionToolOutputs.Enqueue(res.Result?.ToString() ?? string.Empty);

                var next = _turns.TryDequeue(out var queued) ? queued : [new TextContent("好的。")];
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, next)));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Work run reply.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var resp = await GetResponseAsync(messages, options, ct);
            yield return new ChatResponseUpdate(ChatRole.Assistant, resp.Messages.SelectMany(m => m.Contents).ToList());
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
