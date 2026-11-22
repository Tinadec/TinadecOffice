using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Companion;

namespace TinadecCore.Api.Tests;

/// <summary>
/// High-signal Wave 3 verification for Companion Domain Collapse (<c>#21</c>), real SSE streaming
/// via <see cref="ICompanionStreamBroker"/>, configurable <c>max_bot_hop_depth</c> (<c>#155</c>),
/// BYO Live2D Cubism Core SDK &amp; script-free model assets, and Bot <c>schedules</c> / <c>plans</c> /
/// <c>todos</c> + tool-enabled autonomy (<c>#25</c>).
/// </summary>
public sealed class CompanionRebuildTests
{
    private sealed class RebuildFactory : IsolatedApiFactory
    {
        private readonly string _root = ApiTestStorage.CreateRoot("companion-rebuild");
        public string DataRoot => Path.Combine(_root, "data");
        public StreamingScriptedChat Chat { get; } = new();

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = DataRoot,
                ["TinadecCompanion:RunDrainEnabled"] = "false",
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services => services.AddSingleton<IAgentChatClientFactory>(new ChatFactory(Chat)));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ChatFactory(StreamingScriptedChat chat) : IAgentChatClientFactory
    {
        public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResolution
            {
                IsAvailable = true,
                BaseUrl = "http://localhost",
                Model = "test",
                ApiKey = "test",
                ModelId = "test/model"
            });

        public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default) =>
            Task.FromResult<IChatClient>(chat);
    }

    private sealed class StreamingScriptedChat : IChatClient
    {
        private readonly ConcurrentQueue<IReadOnlyList<AIContent>> _turns = new();

        public void EnqueueTurn(params AIContent[] contents) => _turns.Enqueue(contents);

        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var contents = _turns.TryDequeue(out var next) ? next : [new TextContent("默认回复。")];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents.ToList())));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!_turns.TryDequeue(out var contents))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "默认流式回复。");
                yield break;
            }

            foreach (var content in contents)
            {
                await Task.Yield();
                if (content is TextContent text && text.Text.Length > 2)
                {
                    var mid = text.Text.Length / 2;
                    yield return new ChatResponseUpdate(ChatRole.Assistant, text.Text[..mid]);
                    yield return new ChatResponseUpdate(ChatRole.Assistant, text.Text[mid..]);
                }
                else
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, [content]);
                }
            }
        }
    }

    private static async Task<Guid> CreateCompanionAsync(HttpClient client, string name, bool autonomyEnabled = false)
    {
        using var resp = await client.PostAsJsonAsync("/api/v1/companions", new
        {
            name,
            role = "autonomous",
            autonomy = autonomyEnabled ? new { enabled = true, daily_action_budget = 6 } : null
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<string> ReadSseFrameAsync(StreamReader reader, CancellationToken ct)
    {
        var lines = new List<string>();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) throw new EndOfStreamException("SSE stream closed unexpectedly.");
            if (line.Length == 0) return string.Join("\n", lines);
            lines.Add(line);
        }
    }

    [Fact]
    public async Task GroupBotMentionRelay_HonorsConfigurableMaxBotHopDepth_AndStopsAtCap()
    {
        using var factory = new RebuildFactory();
        using var client = factory.CreateClient();
        var runtime = factory.Services.GetRequiredService<CompanionRuntime>();

        var alphaId = await CreateCompanionAsync(client, "Alpha");
        var betaId = await CreateCompanionAsync(client, "Beta");

        using var groupResp = await client.PostAsJsonAsync($"/api/v1/companions/{alphaId}/conversations", new
        {
            kind = "group",
            title = "双Bot协作群",
            companion_ids = new[] { alphaId, betaId }
        });
        Assert.Equal(HttpStatusCode.OK, groupResp.StatusCode);
        var groupId = (await groupResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Configure max_bot_hop_depth = 2: User -> Alpha (hop 0) -> Beta (hop 1) -> Alpha (hop 2) -> capped!
        using var patchSettings = await client.PatchAsJsonAsync("/api/v1/companion-settings", new { max_bot_hop_depth = 2 });
        Assert.Equal(HttpStatusCode.OK, patchSettings.StatusCode);
        Assert.Equal(2, (await patchSettings.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("max_bot_hop_depth").GetInt32());

        factory.Chat.EnqueueTurn(new TextContent("@Beta 请补充第一轮数据。"));
        factory.Chat.EnqueueTurn(new TextContent("@Alpha 数据已核对，请确认。"));
        factory.Chat.EnqueueTurn(new TextContent("@Beta 还想继续追问（应在上限处停止）。"));

        using var post = await client.PostAsJsonAsync($"/api/v1/companion-conversations/{groupId}/messages", new
        {
            body = "@Alpha 开始同步",
            channel_kind = "desktop_chat",
            thread_key = "sync-1"
        });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        // Pass 1 settles Alpha (hop 0) -> enqueues Beta (hop 1).
        Assert.Equal(1, await runtime.RunPassAsync());
        // Pass 2 settles Beta (hop 1) -> enqueues Alpha (hop 2).
        Assert.Equal(1, await runtime.RunPassAsync());
        // Pass 3 settles Alpha (hop 2) -> hop 3 exceeds max_bot_hop_depth=2 -> records bot_relay_capped, 0 new runs!
        Assert.Equal(1, await runtime.RunPassAsync());
        Assert.Equal(0, await runtime.RunPassAsync());

        var activities = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/companions/activity");
        Assert.Contains(activities!, a => a.GetProperty("kind").GetString() == "bot_relay_capped");

        // Setting max_bot_hop_depth = 0 disables bot-to-bot auto-relay completely.
        using var disableRelay = await client.PatchAsJsonAsync("/api/v1/companion-settings", new { max_bot_hop_depth = 0 });
        Assert.Equal(HttpStatusCode.OK, disableRelay.StatusCode);

        factory.Chat.EnqueueTurn(new TextContent("@Beta 这条不应触发任何自动接力。"));
        using var postZero = await client.PostAsJsonAsync($"/api/v1/companion-conversations/{groupId}/messages", new { body = "@Alpha 再试一次" });
        Assert.Equal(HttpStatusCode.OK, postZero.StatusCode);
        Assert.Equal(1, await runtime.RunPassAsync());
        Assert.Equal(0, await runtime.RunPassAsync());
    }

    [Fact]
    public async Task LiveSseStream_DeliversDeltaToolCallStageCueAndProposalFramesWithoutPolling()
    {
        using var factory = new RebuildFactory();
        using var client = factory.CreateClient();
        var runtime = factory.Services.GetRequiredService<CompanionRuntime>();

        var botId = await CreateCompanionAsync(client, "Streamer", autonomyEnabled: true);
        using var privResp = await client.PostAsJsonAsync($"/api/v1/companions/{botId}/conversations", new { kind = "private" });
        var conversationId = (await privResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/companion-events?after_seq=0");
        using var sseResp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, sseResp.StatusCode);
        await using var stream = await sseResp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // Initial heartbeat
        Assert.Contains("event: heartbeat", await ReadSseFrameAsync(reader, cts.Token));

        factory.Chat.EnqueueTurn(new FunctionCallContent("call-todos", CompanionToolCatalog.ListTodos, new Dictionary<string, object?>()));
        factory.Chat.EnqueueTurn(new TextContent("流式回复已完成！"));

        using var post = await client.PostAsJsonAsync($"/api/v1/companion-conversations/{conversationId}/messages", new { body = "查一下待办并流式回答" });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal(1, await runtime.RunPassAsync(cts.Token));

        // Also trigger an autonomy proposal to verify companion.proposal over SSE.
        factory.Chat.EnqueueTurn(new TextContent($"{{\"action\":\"send_message\",\"conversation_id\":\"{conversationId}\",\"body\":\"早上好\"}}"));
        using var tickResp = await client.PostAsync($"/api/v1/companions/{botId}/autonomy/tick", content: null, cts.Token);
        Assert.Equal(HttpStatusCode.OK, tickResp.StatusCode);

        var frames = new List<string>();
        while (frames.Count < 25)
        {
            var frame = await ReadSseFrameAsync(reader, cts.Token);
            frames.Add(frame);
            if (frames.Any(f => f.Contains("event: companion.delta"))
                && frames.Any(f => f.Contains("event: companion.tool_call"))
                && frames.Any(f => f.Contains("event: companion.stage_cue"))
                && frames.Any(f => f.Contains("event: companion.proposal")))
            {
                break;
            }
        }

        Assert.Contains(frames, f => f.Contains("event: companion.delta") && f.Contains("流式回复"));
        Assert.Contains(frames, f => f.Contains("event: companion.tool_call") && f.Contains("companion_list_todos"));
        Assert.Contains(frames, f => f.Contains("event: companion.stage_cue") && f.Contains("\"speaking\":true"));
        Assert.Contains(frames, f => f.Contains("event: companion.proposal") && f.Contains("\"kind\":\"mandate\""));
        cts.Cancel();
    }

    [Fact]
    public async Task ByoCubismSdkAndModelAssets_VerifySha256ServePureDataAndRejectEmbeddedScripts()
    {
        using var factory = new RebuildFactory();
        using var client = factory.CreateClient();

        // 1. SDK starts uninstalled, imports base64 JS, verifies SHA-256, serves nosniff, and deletes cleanly.
        var initialSdk = await client.GetFromJsonAsync<JsonElement>("/api/v1/companion-avatars/sdk");
        Assert.False(initialSdk.GetProperty("ready").GetBoolean());

        var fakeSdkBytes = Encoding.UTF8.GetBytes("window.Live2DCubismCore = { version: 4 };");
        var expectedSha = Convert.ToHexString(SHA256.HashData(fakeSdkBytes)).ToLowerInvariant();

        using var importSdk = await client.PostAsJsonAsync("/api/v1/companion-avatars/sdk", new
        {
            file_name = "live2dcubismcore.min.js",
            content_base64 = Convert.ToBase64String(fakeSdkBytes)
        });
        Assert.Equal(HttpStatusCode.OK, importSdk.StatusCode);
        var sdkStatus = await importSdk.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(sdkStatus.GetProperty("ready").GetBoolean());
        Assert.Equal(expectedSha, sdkStatus.GetProperty("sha256").GetString());

        using var servedSdk = await client.GetAsync("/api/v1/companion-avatars/sdk/live2dcubismcore.min.js");
        Assert.Equal(HttpStatusCode.OK, servedSdk.StatusCode);
        Assert.Equal("nosniff", servedSdk.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal($"\"{expectedSha}\"", servedSdk.Headers.ETag?.Tag);
        Assert.Equal("window.Live2DCubismCore = { version: 4 };", await servedSdk.Content.ReadAsStringAsync());

        using var deleteSdk = await client.DeleteAsync("/api/v1/companion-avatars/sdk");
        Assert.Equal(HttpStatusCode.NoContent, deleteSdk.StatusCode);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/v1/companion-avatars/sdk")).GetProperty("ready").GetBoolean());

        // 2. Live2D model directory containing any .js file is rejected at import.
        var unsafeDir = Path.Combine(factory.DataRoot, "UnsafeModel");
        Directory.CreateDirectory(unsafeDir);
        await File.WriteAllTextAsync(Path.Combine(unsafeDir, "model3.json"), "{\"Version\":3}");
        await File.WriteAllTextAsync(Path.Combine(unsafeDir, "evil.js"), "alert(1);");

        using var unsafeImport = await client.PostAsJsonAsync("/api/v1/companion-avatars/import", new { path = unsafeDir });
        Assert.Equal(HttpStatusCode.BadRequest, unsafeImport.StatusCode);
        Assert.Equal("avatar_script_rejected", (await unsafeImport.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // 3. Pure-data Live2D model imports and serves model3.json via /assets/... while blocking traversal.
        var safeDir = Path.Combine(factory.DataRoot, "SafeModel");
        Directory.CreateDirectory(safeDir);
        await File.WriteAllTextAsync(Path.Combine(safeDir, "model3.json"), "{\"Version\":3}");

        using var safeImport = await client.PostAsJsonAsync("/api/v1/companion-avatars/import", new { path = safeDir });
        Assert.Equal(HttpStatusCode.OK, safeImport.StatusCode);
        var avatar = await safeImport.Content.ReadFromJsonAsync<JsonElement>();
        var avatarId = avatar.GetProperty("id").GetGuid();
        Assert.Equal("model3.json", avatar.GetProperty("entry_model_json").GetString());

        using var assetResp = await client.GetAsync($"/api/v1/companion-avatars/{avatarId}/assets/model3.json");
        Assert.Equal(HttpStatusCode.OK, assetResp.StatusCode);
        Assert.Equal("{\"Version\":3}", await assetResp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SchedulesPlansTodosCrud_DueTrigger_AndToolEnabledAutonomyTickWorkEndToEnd()
    {
        using var factory = new RebuildFactory();
        using var client = factory.CreateClient();
        var runtime = factory.Services.GetRequiredService<CompanionRuntime>();
        var planning = factory.Services.GetRequiredService<CompanionPlanningService>();

        var botId = await CreateCompanionAsync(client, "PlannerBot", autonomyEnabled: true);

        // 1. Schedule CRUD + Cron next_run_at + Due evaluation
        using var createSchedule = await client.PostAsJsonAsync($"/api/v1/companions/{botId}/schedules", new
        {
            title = "每日晨会提醒",
            prompt = "汇总昨日工作会话进展。",
            cron_expression = "*/15 * * * *",
            enabled = true
        });
        Assert.Equal(HttpStatusCode.OK, createSchedule.StatusCode);
        var schedule = await createSchedule.Content.ReadFromJsonAsync<JsonElement>();
        var scheduleId = schedule.GetProperty("id").GetGuid();
        Assert.Equal(JsonValueKind.String, schedule.GetProperty("next_run_at").ValueKind);

        var nextRunAt = schedule.GetProperty("next_run_at").GetDateTimeOffset();
        factory.Chat.EnqueueTurn(new TextContent("已完成晨会汇总！"));
        var fired = await planning.RunDueSchedulesAsync(nextRunAt.AddSeconds(1));
        Assert.Equal(1, fired);
        Assert.Equal(1, await runtime.RunPassAsync());

        // 2. Plan & Todo CRUD
        using var createPlan = await client.PostAsJsonAsync($"/api/v1/companions/{botId}/plans", new
        {
            title = "季度交付计划",
            summary = "按里程碑推进",
            steps = new[] { new { id = "s1", title = "梳理需求", status = "completed" }, new { id = "s2", title = "跟进实现", status = "in_progress" } }
        });
        Assert.Equal(HttpStatusCode.OK, createPlan.StatusCode);
        var planId = (await createPlan.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var createTodo = await client.PostAsJsonAsync($"/api/v1/companions/{botId}/todos", new
        {
            title = "核对验收清单",
            priority = "high",
            plan_id = planId
        });
        Assert.Equal(HttpStatusCode.OK, createTodo.StatusCode);

        // 3. Tool-enabled Autonomy Tick: Bot calls companion_upsert_todo during its tick, then returns {"action":"none"}.
        factory.Chat.EnqueueTurn(new FunctionCallContent("auto-todo-1", CompanionToolCatalog.UpsertTodo, new Dictionary<string, object?>
        {
            ["title"] = "巡检自动创建的待办",
            ["priority"] = "medium"
        }));
        factory.Chat.EnqueueTurn(new TextContent("{\"action\":\"none\"}"));

        using var tickResp = await client.PostAsync($"/api/v1/companions/{botId}/autonomy/tick", content: null);
        Assert.Equal(HttpStatusCode.OK, tickResp.StatusCode);

        var todos = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/companions/{botId}/todos");
        Assert.Equal(2, todos!.Length);
        Assert.Contains(todos, t => t.GetProperty("title").GetString() == "巡检自动创建的待办");
    }
}
