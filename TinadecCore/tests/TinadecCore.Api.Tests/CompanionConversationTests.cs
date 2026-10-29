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

namespace TinadecCore.Api.Tests;

/// <summary>
/// Companion conversations run through the real HTTP host and a real SQLite database, with a
/// scripted chat client standing in for the model. The background runtime is the production loop,
/// so a run is observed by polling its status, exactly as a client would.
/// </summary>
public sealed class CompanionConversationTests
{
    private sealed class ConversationFactory : IsolatedApiFactory
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-companion-conversation-tests", Guid.NewGuid().ToString("N"));

        public ConversationFactory() => Directory.CreateDirectory(_root);

        public ScriptedChat Chat { get; } = new();

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services => services.AddSingleton<IAgentChatClientFactory>(new ScriptedChatFactory(Chat)));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed record ChatCall(string Instructions, string Prompt, string? ToolNames, ChatToolMode? ToolMode);

    private sealed class ScriptedChatFactory(ScriptedChat chat) : IAgentChatClientFactory
    {
        public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default) =>
            Task.FromResult(chat.Available
                ? new ChatResolution { IsAvailable = true, BaseUrl = "http://localhost", Model = "test", ApiKey = "test", ModelId = "test/model" }
                : new ChatResolution { IsAvailable = false, Error = "No companion chat route is configured." });

        public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default) =>
            Task.FromResult<IChatClient>(chat);
    }

    private sealed class ScriptedChat : IChatClient
    {
        public bool Available { get; set; } = true;
        public string Reply { get; set; } = "收到，我在。";
        public ConcurrentQueue<ChatCall> Calls { get; } = new();

        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(new ChatCall(
                options?.Instructions ?? string.Empty,
                string.Join("\n", messages.Select(x => x.Text)),
                options?.Tools is { Count: > 0 } tools ? string.Join(",", tools.Select(x => x.Name)) : null,
                options?.ToolMode));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }

    // ── helpers ──

    private static async Task<JsonElement> CreateCompanionAsync(HttpClient client, string name, string? persona = null)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/companions", new { name, persona_document = persona });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> CreateMemoryAsync(HttpClient client, Guid companionId, string content)
    {
        using var response = await client.PostAsJsonAsync($"/api/v1/companions/{companionId}/memories", new { content });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string url, object body, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    private static async Task<JsonElement> PrivateConversationAsync(HttpClient client, Guid companionId)
    {
        var body = await PostJsonAsync(client, $"/api/v1/companions/{companionId}/conversations", new { kind = "private" });
        Assert.Equal("private", body.GetProperty("kind").GetString());
        return body;
    }

    private static async Task<JsonElement> WaitForRunAsync(HttpClient client, Guid runId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var run = await client.GetFromJsonAsync<JsonElement>($"/api/v1/companion-runs/{runId}");
            var status = run.GetProperty("status").GetString();
            if (status is "completed" or "failed") return run;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException($"companion run {runId} did not settle in time.");
    }

    private static async Task<List<Guid>> LoadedMemoriesAsync(HttpClient client, Guid runId)
    {
        var run = await client.GetFromJsonAsync<JsonElement>($"/api/v1/companion-runs/{runId}");
        return run.GetProperty("loaded_memory_ids").EnumerateArray().Select(x => x.GetGuid()).ToList();
    }

    [Fact]
    public async Task PrivateMessage_StoresMessage_CreatesRun_AndTheCompanionRepliesWithItsMemory()
    {
        using var factory = new ConversationFactory();
        using var client = factory.CreateClient();
        var companion = await CreateCompanionAsync(client, "A", "你是 A，一个严谨的伙伴。");
        var companionId = companion.GetProperty("id").GetGuid();
        var memoryId = await CreateMemoryAsync(client, companionId, "项目代号是 Aurora");

        var conversation = await PrivateConversationAsync(client, companionId);
        var conversationId = conversation.GetProperty("id").GetGuid();
        Assert.Equal(2, conversation.GetProperty("members").GetArrayLength());

        var sent = await PostJsonAsync(client, $"/api/v1/companion-conversations/{conversationId}/messages",
            new { body = "项目代号是 Aurora 吗？" });
        Assert.False(sent.GetProperty("duplicate").GetBoolean());
        var runId = Assert.Single(sent.GetProperty("runs").EnumerateArray()).GetProperty("id").GetGuid();
        Assert.Equal(0, sent.GetProperty("skipped_runs").GetArrayLength());
        Assert.Equal("queued", sent.GetProperty("runs")[0].GetProperty("status").GetString());

        var run = await WaitForRunAsync(client, runId);
        Assert.Equal("completed", run.GetProperty("status").GetString());
        Assert.Equal(1, run.GetProperty("rounds").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, run.GetProperty("reply_message_id").ValueKind);
        Assert.Contains(memoryId, await LoadedMemoriesAsync(client, runId));

        var messages = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/companion-conversations/{conversationId}/messages");
        Assert.NotNull(messages);
        Assert.Equal(2, messages!.Length);
        var reply = messages.Single(x => x.GetProperty("author_kind").GetString() == "companion");
        Assert.Equal("A", reply.GetProperty("author_name").GetString());
        Assert.Equal(companionId, reply.GetProperty("author_companion_id").GetGuid());
        Assert.Equal(0, reply.GetProperty("addressed_to").GetArrayLength());

        var call = Assert.Single(factory.Chat.Calls, x => x.Instructions.Contains("你是 A"));
        Assert.Null(call.ToolNames);
        Assert.Equal(ChatToolMode.None, call.ToolMode);
        Assert.Contains("项目代号是 Aurora", call.Instructions);
    }

    [Fact]
    public async Task Invite_CreatesLinkedGroup_WithoutHistoryUnlessAsked()
    {
        using var factory = new ConversationFactory();
        using var client = factory.CreateClient();
        factory.Chat.Available = false; // keep the transcript deterministic: no companion reply is written

        var a = (await CreateCompanionAsync(client, "A")).GetProperty("id").GetGuid();
        var b = (await CreateCompanionAsync(client, "B")).GetProperty("id").GetGuid();
        var c = (await CreateCompanionAsync(client, "C")).GetProperty("id").GetGuid();

        var priv = await PrivateConversationAsync(client, a);
        var privId = priv.GetProperty("id").GetGuid();
        await PostJsonAsync(client, $"/api/v1/companion-conversations/{privId}/messages", new { body = "只属于 A 的私聊" });

        var group = await PostJsonAsync(client, $"/api/v1/companion-conversations/{privId}/members",
            new { companion_id = b, share_history = false });
        var groupId = group.GetProperty("id").GetGuid();
        Assert.Equal("group", group.GetProperty("kind").GetString());
        Assert.Equal(privId, group.GetProperty("parent_conversation_id").GetGuid());
        Assert.Equal("A、B", group.GetProperty("title").GetString());
        Assert.Equal(3, group.GetProperty("members").GetArrayLength());
        Assert.Equal(0, group.GetProperty("message_count").GetInt32());

        var original = await client.GetFromJsonAsync<JsonElement>($"/api/v1/companion-conversations/{privId}");
        Assert.Equal(1, original.GetProperty("message_count").GetInt32());
        Assert.Equal("private", original.GetProperty("kind").GetString());

        var shared = await PostJsonAsync(client, $"/api/v1/companion-conversations/{privId}/members",
            new { companion_id = c, share_history = true });
        Assert.Equal(1, shared.GetProperty("message_count").GetInt32());
        var sharedId = shared.GetProperty("id").GetGuid();
        var copied = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/companion-conversations/{sharedId}/messages");
        var copiedMessage = Assert.Single(copied!);
        Assert.Equal("只属于 A 的私聊", copiedMessage.GetProperty("body").GetString());
        Assert.Equal("user", copiedMessage.GetProperty("author_kind").GetString());

        var activity = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/companions/activity?limit=50");
        Assert.Contains(activity!, row => row.GetProperty("kind").GetString() == "group_created");
        Assert.Contains(activity!, row => row.GetProperty("kind").GetString() == "member_invited");
    }

    [Fact]
    public async Task IdempotencyKeyReplaysTheSameMessageAndRuns()
    {
        using var factory = new ConversationFactory();
        using var client = factory.CreateClient();
        factory.Chat.Available = false;

        var companionId = (await CreateCompanionAsync(client, "A")).GetProperty("id").GetGuid();
        var conversationId = (await PrivateConversationAsync(client, companionId)).GetProperty("id").GetGuid();

        var first = await PostJsonAsync(client, $"/api/v1/companion-conversations/{conversationId}/messages",
            new { body = "只发一次" }, "key-1");
        Assert.Single(first.GetProperty("runs").EnumerateArray());

        var second = await PostJsonAsync(client, $"/api/v1/companion-conversations/{conversationId}/messages",
            new { body = "只发一次" }, "key-1");
        Assert.True(second.GetProperty("duplicate").GetBoolean());
        Assert.Equal(first.GetProperty("message").GetProperty("id").GetGuid(), second.GetProperty("message").GetProperty("id").GetGuid());
        Assert.Single(second.GetProperty("runs").EnumerateArray());

        var conversation = await client.GetFromJsonAsync<JsonElement>($"/api/v1/companion-conversations/{conversationId}");
        Assert.Equal(1, conversation.GetProperty("message_count").GetInt32());
    }

    [Fact]
    public async Task AddressedToMustBeAMember_AndAnUnaddressedGroupMessageWakesNobody()
    {
        using var factory = new ConversationFactory();
        using var client = factory.CreateClient();
        factory.Chat.Available = false;

        var a = (await CreateCompanionAsync(client, "A")).GetProperty("id").GetGuid();
        var b = (await CreateCompanionAsync(client, "B")).GetProperty("id").GetGuid();
        var outsider = (await CreateCompanionAsync(client, "D")).GetProperty("id").GetGuid();

        var group = await PostJsonAsync(client, $"/api/v1/companions/{a}/conversations",
            new { kind = "group", companion_ids = new[] { b } });
        var groupId = group.GetProperty("id").GetGuid();
        Assert.Equal(3, group.GetProperty("members").GetArrayLength());

        using var rejected = await client.PostAsJsonAsync($"/api/v1/companion-conversations/{groupId}/messages",
            new { body = "点名外人", addressed_to = new[] { outsider } });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("companion_not_member", await ErrorCodeAsync(rejected));

        var silent = await PostJsonAsync(client, $"/api/v1/companion-conversations/{groupId}/messages", new { body = "大家好" });
        Assert.Equal(0, silent.GetProperty("runs").GetArrayLength());
        Assert.Equal(0, silent.GetProperty("skipped_runs").GetArrayLength());

        var conversation = await client.GetFromJsonAsync<JsonElement>($"/api/v1/companion-conversations/{groupId}");
        Assert.Equal(1, conversation.GetProperty("message_count").GetInt32());

        // An exact @name mention wakes only that member.
        var mentioned = await PostJsonAsync(client, $"/api/v1/companion-conversations/{groupId}/messages", new { body = "@B 你在吗" });
        var run = Assert.Single(mentioned.GetProperty("runs").EnumerateArray());
        Assert.Equal(b, run.GetProperty("companion_id").GetGuid());
    }

    [Fact]
    public async Task TurnBudget_StopsFurtherWakes_ButKeepsTheMessage()
    {
        using var factory = new ConversationFactory();
        using var client = factory.CreateClient();
        factory.Chat.Available = false;

        var a = (await CreateCompanionAsync(client, "A")).GetProperty("id").GetGuid();
        var b = (await CreateCompanionAsync(client, "B")).GetProperty("id").GetGuid();
        var groupId = (await PostJsonAsync(client, $"/api/v1/companions/{a}/conversations",
            new { kind = "group", companion_ids = new[] { b } })).GetProperty("id").GetGuid();

        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<CompanionDbContext>>().CreateDbContextAsync())
        {
            var conversation = await db.Conversations.SingleAsync(x => x.Id == groupId);
            conversation.TurnBudget = 1;
            await db.SaveChangesAsync();
        }

        var first = await PostJsonAsync(client, $"/api/v1/companion-conversations/{groupId}/messages",
            new { body = "第一次", addressed_to = new[] { a } });
        Assert.Single(first.GetProperty("runs").EnumerateArray());

        var second = await PostJsonAsync(client, $"/api/v1/companion-conversations/{groupId}/messages",
            new { body = "第二次", addressed_to = new[] { a } });
        Assert.Equal(0, second.GetProperty("runs").GetArrayLength());
        var skip = Assert.Single(second.GetProperty("skipped_runs").EnumerateArray());
        Assert.Equal(a, skip.GetProperty("companion_id").GetGuid());
        Assert.Equal("turn_budget", skip.GetProperty("reason").GetString());

        var conversationAfter = await client.GetFromJsonAsync<JsonElement>($"/api/v1/companion-conversations/{groupId}");
        Assert.Equal(2, conversationAfter.GetProperty("message_count").GetInt32());
    }

    [Fact]
    public async Task PausedConversationOrCompanion_CreatesNoRun()
    {
        using var factory = new ConversationFactory();
        using var client = factory.CreateClient();
        factory.Chat.Available = false;

        var a = (await CreateCompanionAsync(client, "A")).GetProperty("id").GetGuid();
        var conversationA = (await PrivateConversationAsync(client, a)).GetProperty("id").GetGuid();
        await PostJsonAsync(client, $"/api/v1/companion-conversations/{conversationA}/pause", new { paused = true });
        var pausedSend = await PostJsonAsync(client, $"/api/v1/companion-conversations/{conversationA}/messages", new { body = "暂停中" });
        Assert.Equal(0, pausedSend.GetProperty("runs").GetArrayLength());
        Assert.Equal("conversation_paused", Assert.Single(pausedSend.GetProperty("skipped_runs").EnumerateArray()).GetProperty("reason").GetString());

        var b = (await CreateCompanionAsync(client, "B")).GetProperty("id").GetGuid();
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<CompanionDbContext>>().CreateDbContextAsync())
        {
            var row = await db.Companions.SingleAsync(x => x.Id == b);
            row.Status = "paused";
            await db.SaveChangesAsync();
        }
        var conversationB = (await PrivateConversationAsync(client, b)).GetProperty("id").GetGuid();
        var companionPaused = await PostJsonAsync(client, $"/api/v1/companion-conversations/{conversationB}/messages", new { body = "伙伴暂停" });
        Assert.Equal(0, companionPaused.GetProperty("runs").GetArrayLength());
        Assert.Equal("companion_paused", Assert.Single(companionPaused.GetProperty("skipped_runs").EnumerateArray()).GetProperty("reason").GetString());

        var conversation = await client.GetFromJsonAsync<JsonElement>($"/api/v1/companion-conversations/{conversationA}");
        Assert.True(conversation.GetProperty("autonomy_paused").GetBoolean());
    }

    [Fact]
    public async Task RunSnapshotKeepsThePersonaThatExistedWhenItStarted()
    {
        using var factory = new ConversationFactory();
        using var client = factory.CreateClient();
        var companion = await CreateCompanionAsync(client, "A", "旧人格：只谈事实。");
        var companionId = companion.GetProperty("id").GetGuid();
        var conversationId = (await PrivateConversationAsync(client, companionId)).GetProperty("id").GetGuid();

        var sent = await PostJsonAsync(client, $"/api/v1/companion-conversations/{conversationId}/messages", new { body = "你好" });
        var runId = Assert.Single(sent.GetProperty("runs").EnumerateArray()).GetProperty("id").GetGuid();
        Assert.Equal("completed", (await WaitForRunAsync(client, runId)).GetProperty("status").GetString());

        using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/companions/{companionId}")
        {
            Content = JsonContent.Create(new { persona_document = "新人格：随便聊。" })
        };
        patch.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var patched = await client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<CompanionDbContext>>().CreateDbContextAsync();
        var snapshot = await db.Runs.AsNoTracking().Where(x => x.Id == runId).Select(x => x.ConfigSnapshotJson).SingleAsync();
        using var document = JsonDocument.Parse(snapshot);
        var root = document.RootElement;
        Assert.Equal(companionId, root.GetProperty("companion_id").GetGuid());
        Assert.Equal("旧人格：只谈事实。", root.GetProperty("persona_document").GetString());
        Assert.DoesNotContain("新人格", root.GetProperty("persona_document").GetString());
        Assert.Equal(1, root.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task UnavailableModelFailsTheRunAndWritesNoCompanionText()
    {
        using var factory = new ConversationFactory();
        using var client = factory.CreateClient();
        factory.Chat.Available = false;

        var companionId = (await CreateCompanionAsync(client, "A")).GetProperty("id").GetGuid();
        var conversationId = (await PrivateConversationAsync(client, companionId)).GetProperty("id").GetGuid();
        var sent = await PostJsonAsync(client, $"/api/v1/companion-conversations/{conversationId}/messages", new { body = "在吗" });
        var runId = Assert.Single(sent.GetProperty("runs").EnumerateArray()).GetProperty("id").GetGuid();

        var run = await WaitForRunAsync(client, runId);
        Assert.Equal("failed", run.GetProperty("status").GetString());
        Assert.Equal("companion_model_unavailable", run.GetProperty("error_code").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("reply_message_id").ValueKind);

        var messages = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/companion-conversations/{conversationId}/messages");
        Assert.Single(messages!);
        Assert.Equal("user", messages![0].GetProperty("author_kind").GetString());

        var activity = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/companions/activity?limit=50");
        Assert.Contains(activity!, row => row.GetProperty("kind").GetString() == "run_failed");
    }

    [Fact]
    public async Task EachCompanionLoadsOnlyItsOwnMemory_AndSeesOnlyItsOwnConversations()
    {
        using var factory = new ConversationFactory();
        using var client = factory.CreateClient();

        var a = (await CreateCompanionAsync(client, "A")).GetProperty("id").GetGuid();
        var b = (await CreateCompanionAsync(client, "B")).GetProperty("id").GetGuid();
        var memoryA = await CreateMemoryAsync(client, a, "Aurora 是 A 的私有记忆");
        var memoryB = await CreateMemoryAsync(client, b, "Aurora 是 B 的私有记忆");

        var privateA = (await PrivateConversationAsync(client, a)).GetProperty("id").GetGuid();
        var privateB = (await PrivateConversationAsync(client, b)).GetProperty("id").GetGuid();

        var listA = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/companions/{a}/conversations");
        Assert.NotNull(listA);
        Assert.Contains(listA!, row => row.GetProperty("id").GetGuid() == privateA);
        Assert.DoesNotContain(listA!, row => row.GetProperty("id").GetGuid() == privateB);

        var groupId = (await PostJsonAsync(client, $"/api/v1/companions/{a}/conversations",
            new { kind = "group", companion_ids = new[] { b } })).GetProperty("id").GetGuid();
        var sent = await PostJsonAsync(client, $"/api/v1/companion-conversations/{groupId}/messages",
            new { body = "Aurora 是什么", addressed_to = new[] { a } });
        var runId = Assert.Single(sent.GetProperty("runs").EnumerateArray()).GetProperty("id").GetGuid();
        Assert.Equal("completed", (await WaitForRunAsync(client, runId)).GetProperty("status").GetString());

        var loaded = await LoadedMemoriesAsync(client, runId);
        Assert.Contains(memoryA, loaded);
        Assert.DoesNotContain(memoryB, loaded);

        var listAfter = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/companions/{b}/conversations");
        Assert.NotNull(listAfter);
        Assert.DoesNotContain(listAfter!, row => row.GetProperty("id").GetGuid() == privateA);
    }

    [Fact]
    public async Task ConversationIndex_ReturnsEveryCompanionsChats_WithOriginAndOpenability()
    {
        using var factory = new ConversationFactory();
        using var client = factory.CreateClient();
        factory.Chat.Available = false; // no replies: this test is about the index, not the runtime

        var a = (await CreateCompanionAsync(client, "A")).GetProperty("id").GetGuid();
        var b = (await CreateCompanionAsync(client, "B")).GetProperty("id").GetGuid();
        var privateA = (await PrivateConversationAsync(client, a)).GetProperty("id").GetGuid();
        var group = (await PostJsonAsync(client, $"/api/v1/companions/{a}/conversations",
            new { kind = "group", title = "排期群", companion_ids = new[] { b } })).GetProperty("id").GetGuid();

        var all = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/companion-conversations");
        Assert.NotNull(all);
        var ids = all!.Select(row => row.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(privateA, ids);
        Assert.Contains(group, ids);

        var groupRow = all.Single(row => row.GetProperty("id").GetGuid() == group);
        // Who opened the chat is its own field: every group carries a user member either way.
        Assert.Equal("user", groupRow.GetProperty("origin").GetString());
        Assert.True(groupRow.GetProperty("can_open").GetBoolean());
        Assert.Equal("排期群", groupRow.GetProperty("title").GetString());
        Assert.Equal(3, groupRow.GetProperty("members").GetArrayLength());
        Assert.Equal("user", all.Single(row => row.GetProperty("id").GetGuid() == privateA).GetProperty("origin").GetString());

        var groupsOnly = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/companion-conversations?kind=group");
        Assert.NotNull(groupsOnly);
        Assert.Contains(groupsOnly!, row => row.GetProperty("id").GetGuid() == group);
        Assert.DoesNotContain(groupsOnly!, row => row.GetProperty("id").GetGuid() == privateA);

        var forB = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/companion-conversations?companion_id={b}");
        Assert.NotNull(forB);
        Assert.Contains(forB!, row => row.GetProperty("id").GetGuid() == group);
        Assert.DoesNotContain(forB!, row => row.GetProperty("id").GetGuid() == privateA);

        var invalid = await client.GetAsync("/api/v1/companion-conversations?kind=private-chat");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);        Assert.Equal("invalid_request", await ErrorCodeAsync(invalid));
    }
}
