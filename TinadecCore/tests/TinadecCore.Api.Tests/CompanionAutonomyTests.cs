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
/// Companion autonomy through the real HTTP host, a real SQLite database and a scripted model.
/// The model is only an input: every id, membership, pause switch, duplicate and budget rule is
/// checked by the server, so these tests assert both the decision result and the rows it left.
/// </summary>
public sealed class CompanionAutonomyTests
{
    private sealed class AutonomyFactory : IsolatedApiFactory
    {
        private readonly string _root = ApiTestStorage.CreateRoot("companion-autonomy");

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
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed record ChatCall(string Instructions, string Prompt, ChatToolMode? ToolMode, string? ToolNames);

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
        public string Reply { get; set; } = """{"action":"none"}""";
        public ConcurrentQueue<ChatCall> Calls { get; } = new();

        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(new ChatCall(
                options?.Instructions ?? string.Empty,
                string.Join("\n", messages.Select(x => x.Text)),
                options?.ToolMode,
                options?.Tools is { Count: > 0 } tools ? string.Join(",", tools.Select(x => x.Name)) : null));
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

    private static async Task<JsonElement> CreateCompanionAsync(
        HttpClient client, string name, object? autonomy = null, string? persona = null)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/companions", new { name, persona_document = persona, autonomy });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string url, object body)
    {
        using var response = await client.PostAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> PrivateConversationAsync(HttpClient client, Guid companionId)
    {
        var body = await PostJsonAsync(client, $"/api/v1/companions/{companionId}/conversations", new { kind = "private" });
        Assert.Equal("private", body.GetProperty("kind").GetString());
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> TickAsync(HttpClient client, Guid companionId)
    {
        using var response = await client.PostAsync($"/api/v1/companions/{companionId}/autonomy/tick", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement[]> ActivityAsync(HttpClient client)
    {
        var rows = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/companions/activity?limit=50");
        Assert.NotNull(rows);
        return rows!;
    }

    private static string SendMessageDecision(Guid conversationId, string body) =>
        JsonSerializer.Serialize(new { action = "send_message", conversation_id = conversationId, body });

    private static string CreateGroupDecision(string title, params Guid[] companionIds) =>
        JsonSerializer.Serialize(new { action = "create_group", title, companion_ids = companionIds });

    private static string InviteDecision(Guid conversationId, Guid companionId) =>
        JsonSerializer.Serialize(new { action = "invite", conversation_id = conversationId, companion_id = companionId, share_history = false });

    private static List<Guid> CompanionMemberIds(JsonElement conversation) =>
        conversation.GetProperty("members").EnumerateArray()
            .Where(member => member.GetProperty("member_kind").GetString() == "companion")
            .Select(member => member.GetProperty("companion_id").GetGuid())
            .ToList();

    private static async Task<int> CountAsync(AutonomyFactory factory, Func<CompanionDbContext, Task<int>> count)
    {
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<CompanionDbContext>>()
            .CreateDbContextAsync();
        return await count(db);
    }

    // ── tests ──

    [Fact]
    public async Task DefaultDisabled_TickDoesNothingAndWritesNothing()
    {
        using var factory = new AutonomyFactory();
        using var client = factory.CreateClient();
        var companionId = (await CreateCompanionAsync(client, "A")).GetProperty("id").GetGuid();

        var tick = await TickAsync(client, companionId);

        Assert.False(tick.GetProperty("acted").GetBoolean());
        Assert.Equal("none", tick.GetProperty("action").GetString());
        Assert.Equal("autonomy_disabled", tick.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, tick.GetProperty("activity").ValueKind);
        Assert.Empty(await ActivityAsync(client));
        Assert.Equal(0, await CountAsync(factory, db => db.Conversations.CountAsync()));
        Assert.Equal(0, await CountAsync(factory, db => db.Messages.CountAsync()));
    }

    [Fact]
    public async Task DailyBudget_FirstActionSucceeds_SecondIsRefused()
    {
        using var factory = new AutonomyFactory();
        using var client = factory.CreateClient();
        var companionId = (await CreateCompanionAsync(
            client, "A", new { enabled = true, daily_action_budget = 1 })).GetProperty("id").GetGuid();
        var conversationId = await PrivateConversationAsync(client, companionId);
        factory.Chat.Reply = SendMessageDecision(conversationId, "我先整理一下今天的安排。");

        var first = await TickAsync(client, companionId);

        Assert.True(first.GetProperty("acted").GetBoolean());
        Assert.Equal("send_message", first.GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("reason").ValueKind);
        Assert.Equal("autonomy_message_sent", first.GetProperty("activity").GetProperty("kind").GetString());
        Assert.Contains(await ActivityAsync(client), row => row.GetProperty("kind").GetString() == "autonomy_message_sent");
        Assert.Single((await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/companion-conversations/{conversationId}/messages"))!);

        var second = await TickAsync(client, companionId);

        Assert.False(second.GetProperty("acted").GetBoolean());
        Assert.Equal("none", second.GetProperty("action").GetString());
        Assert.Equal("daily_budget_exhausted", second.GetProperty("reason").GetString());
        Assert.Equal("autonomy_skipped", second.GetProperty("activity").GetProperty("kind").GetString());
        Assert.Equal("daily_budget_exhausted",
            second.GetProperty("activity").GetProperty("payload").GetProperty("reason").GetString());

        // The exhausted budget is visible in the feed, and it did not consume a second action.
        var activity = await ActivityAsync(client);
        Assert.Contains(activity, row => row.GetProperty("kind").GetString() == "autonomy_skipped"
            && row.GetProperty("payload").GetProperty("reason").GetString() == "daily_budget_exhausted"
            && row.GetProperty("summary").GetString()!.Contains("额度已用完", StringComparison.Ordinal));
        Assert.Single(activity, row => row.GetProperty("kind").GetString() == "autonomy_message_sent");
        Assert.Equal(1, await CountAsync(factory, db => db.Messages.CountAsync()));
    }

    [Fact]
    public async Task PausedCompanionOrGloballyPausedTenant_DoesNothing()
    {
        using var factory = new AutonomyFactory();
        using var client = factory.CreateClient();
        var companionId = (await CreateCompanionAsync(
            client, "A", new { enabled = true, paused = true })).GetProperty("id").GetGuid();
        var conversationId = await PrivateConversationAsync(client, companionId);
        factory.Chat.Reply = SendMessageDecision(conversationId, "暂停中不应发出。");

        var paused = await TickAsync(client, companionId);
        Assert.False(paused.GetProperty("acted").GetBoolean());
        Assert.Equal("autonomy_paused", paused.GetProperty("reason").GetString());
        Assert.Empty(await ActivityAsync(client));

        using var resume = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/companions/{companionId}")
        {
            Content = JsonContent.Create(new { autonomy = new { paused = false } })
        };
        resume.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var resumed = await client.SendAsync(resume);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync("/api/v1/companion-settings",
            new { autonomy_paused = true })).StatusCode);

        var globallyPaused = await TickAsync(client, companionId);
        Assert.False(globallyPaused.GetProperty("acted").GetBoolean());
        Assert.Equal("autonomy_paused", globallyPaused.GetProperty("reason").GetString());
        Assert.Empty(await ActivityAsync(client));
        Assert.Empty(factory.Chat.Calls);
    }

    [Fact]
    public async Task InvalidJson_IsSkippedWithAReason_AndLeavesNoGroupOrMessage()
    {
        using var factory = new AutonomyFactory();
        using var client = factory.CreateClient();
        var companionId = (await CreateCompanionAsync(client, "A", new { enabled = true })).GetProperty("id").GetGuid();
        factory.Chat.Reply = "抱歉，我这次不做决定。";

        var tick = await TickAsync(client, companionId);

        Assert.False(tick.GetProperty("acted").GetBoolean());
        Assert.Equal("none", tick.GetProperty("action").GetString());
        Assert.Equal("invalid_decision", tick.GetProperty("reason").GetString());
        var skip = tick.GetProperty("activity");
        Assert.Equal("autonomy_skipped", skip.GetProperty("kind").GetString());
        Assert.Equal("invalid_decision", skip.GetProperty("payload").GetProperty("reason").GetString());
        Assert.Single(await ActivityAsync(client));
        Assert.Equal(0, await CountAsync(factory, db => db.Conversations.CountAsync()));
        Assert.Equal(0, await CountAsync(factory, db => db.Messages.CountAsync()));
    }

    [Fact]
    public async Task CreateGroup_CreatesOneGroup_AndTheSameDecisionIsADuplicate()
    {
        using var factory = new AutonomyFactory();
        using var client = factory.CreateClient();
        var acting = (await CreateCompanionAsync(client, "A", new { enabled = true })).GetProperty("id").GetGuid();
        var peer = (await CreateCompanionAsync(client, "B")).GetProperty("id").GetGuid();
        factory.Chat.Reply = CreateGroupDecision("自主讨论", peer);

        var first = await TickAsync(client, acting);

        Assert.True(first.GetProperty("acted").GetBoolean());
        Assert.Equal("create_group", first.GetProperty("action").GetString());
        var conversation = first.GetProperty("conversation");
        Assert.Equal("group", conversation.GetProperty("kind").GetString());
        Assert.Equal("自主讨论", conversation.GetProperty("title").GetString());
        // The companion opened this chat, and the member list cannot say so: the user is a member too.
        Assert.Equal("companion", conversation.GetProperty("origin").GetString());
        Assert.Equal(3, conversation.GetProperty("members").GetArrayLength());
        var created = conversation.GetProperty("id").GetGuid();
        Assert.Equal("autonomy_group_created", first.GetProperty("activity").GetProperty("kind").GetString());
        Assert.Single(await ActivityAsync(client), row => row.GetProperty("kind").GetString() == "autonomy_group_created");

        // The model call is a proposal only: it gets no tools and must not be the authority on ids.
        var call = Assert.Single(factory.Chat.Calls);
        Assert.Equal(ChatToolMode.None, call.ToolMode);
        Assert.Null(call.ToolNames);

        var second = await TickAsync(client, acting);

        Assert.False(second.GetProperty("acted").GetBoolean());
        Assert.Equal("create_group", second.GetProperty("action").GetString());
        Assert.Equal("duplicate_group", second.GetProperty("reason").GetString());
        Assert.Equal(created, second.GetProperty("activity").GetProperty("conversation_id").GetGuid());
        Assert.Equal(1, await CountAsync(factory, db => db.Conversations.CountAsync(x => x.Kind == "group")));
        Assert.Single(await ActivityAsync(client), row => row.GetProperty("kind").GetString() == "autonomy_group_created");
    }

    [Fact]
    public async Task Invite_AddsTheTargetOnce_AndTheSameDecisionIsADuplicate()
    {
        using var factory = new AutonomyFactory();
        using var client = factory.CreateClient();
        var acting = (await CreateCompanionAsync(
            client, "A", new { enabled = true, daily_action_budget = 6 })).GetProperty("id").GetGuid();
        var peer = (await CreateCompanionAsync(client, "B")).GetProperty("id").GetGuid();
        var invitee = (await CreateCompanionAsync(client, "C")).GetProperty("id").GetGuid();

        var group = await PostJsonAsync(client, $"/api/v1/companions/{acting}/conversations",
            new { kind = "group", companion_ids = new[] { peer } });
        var groupId = group.GetProperty("id").GetGuid();
        Assert.Equal(3, group.GetProperty("members").GetArrayLength());
        factory.Chat.Reply = InviteDecision(groupId, invitee);

        var first = await TickAsync(client, acting);

        Assert.True(first.GetProperty("acted").GetBoolean());
        Assert.Equal("invite", first.GetProperty("action").GetString());
        Assert.Equal("autonomy_member_invited", first.GetProperty("activity").GetProperty("kind").GetString());
        Assert.Equal(groupId, first.GetProperty("conversation").GetProperty("id").GetGuid());

        var afterInvite = await client.GetFromJsonAsync<JsonElement>($"/api/v1/companion-conversations/{groupId}");
        Assert.Equal(4, afterInvite.GetProperty("members").GetArrayLength());
        Assert.Contains(invitee, CompanionMemberIds(afterInvite));
        Assert.Single(await ActivityAsync(client), row => row.GetProperty("kind").GetString() == "autonomy_member_invited");

        var second = await TickAsync(client, acting);

        Assert.False(second.GetProperty("acted").GetBoolean());
        Assert.Equal("invite", second.GetProperty("action").GetString());
        Assert.Equal("duplicate_member", second.GetProperty("reason").GetString());

        var afterDuplicate = await client.GetFromJsonAsync<JsonElement>($"/api/v1/companion-conversations/{groupId}");
        Assert.Equal(4, afterDuplicate.GetProperty("members").GetArrayLength());
        Assert.Single(CompanionMemberIds(afterDuplicate), id => id == invitee);
        Assert.Single(await ActivityAsync(client), row => row.GetProperty("kind").GetString() == "autonomy_member_invited");
    }

    [Fact]
    public async Task SendMessage_IsIdempotent_AndCreatesNoRun()
    {
        using var factory = new AutonomyFactory();
        using var client = factory.CreateClient();
        var companionId = (await CreateCompanionAsync(
            client, "A", new { enabled = true, daily_action_budget = 6 })).GetProperty("id").GetGuid();
        var conversationId = await PrivateConversationAsync(client, companionId);
        factory.Chat.Reply = SendMessageDecision(conversationId, "同一句话只应出现一次。");

        var first = await TickAsync(client, companionId);
        Assert.True(first.GetProperty("acted").GetBoolean());
        Assert.Equal("autonomy_message_sent", first.GetProperty("activity").GetProperty("kind").GetString());

        var second = await TickAsync(client, companionId);
        Assert.False(second.GetProperty("acted").GetBoolean());
        Assert.Equal("send_message", second.GetProperty("action").GetString());
        Assert.Equal("duplicate_message", second.GetProperty("reason").GetString());

        Assert.Single((await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/companion-conversations/{conversationId}/messages"))!);
        Assert.Equal(0, await CountAsync(factory, db => db.Runs.CountAsync()));
        Assert.Single(await ActivityAsync(client), row => row.GetProperty("kind").GetString() == "autonomy_message_sent");
    }

    [Fact]
    public async Task UnavailableModel_RefusesWithoutWritingAnActivity()
    {
        using var factory = new AutonomyFactory();
        using var client = factory.CreateClient();
        factory.Chat.Available = false;
        var companionId = (await CreateCompanionAsync(client, "A", new { enabled = true })).GetProperty("id").GetGuid();

        var tick = await TickAsync(client, companionId);

        Assert.False(tick.GetProperty("acted").GetBoolean());
        Assert.Equal("model_unavailable", tick.GetProperty("reason").GetString());
        Assert.Empty(await ActivityAsync(client));
        Assert.Equal(0, await CountAsync(factory, db => db.Conversations.CountAsync()));
        Assert.Empty(factory.Chat.Calls);
    }

    [Fact]
    public async Task WorkerDueList_AppliesTheIntervalAndTheBudget()
    {
        using var factory = new AutonomyFactory();
        using var client = factory.CreateClient();
        var companionId = (await CreateCompanionAsync(
            client, "A", new { enabled = true, interval_minutes = 60, daily_action_budget = 6 })).GetProperty("id").GetGuid();
        var service = factory.Services.GetRequiredService<CompanionAutonomyService>();

        // A fresh companion has not waited one interval yet.
        Assert.DoesNotContain(companionId, await service.DueCompanionIdsAsync());

        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<CompanionDbContext>>()
            .CreateDbContextAsync())
        {
            var row = await db.Companions.SingleAsync(x => x.Id == companionId);
            // The interval starts at max(last action, updated_at); moving updated_at back makes it due.
            row.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
            await db.SaveChangesAsync();
        }
        Assert.Contains(companionId, await service.DueCompanionIdsAsync());

        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<CompanionDbContext>>()
            .CreateDbContextAsync())
        {
            var row = await db.Companions.SingleAsync(x => x.Id == companionId);
            row.AutonomyDailyBudget = 0;
            await db.SaveChangesAsync();
        }
        Assert.DoesNotContain(companionId, await service.DueCompanionIdsAsync());
    }

    [Fact]
    public async Task CompanionRoutesStayInTheUserScope_EvenWithAProjectStorageHeader()
    {
        var root = ApiTestStorage.CreateRoot("companion-autonomy-scope");
        var token = Guid.NewGuid().ToString("N");
        var previous = Environment.GetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN");
        Environment.SetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN", token);
        try
        {
            using var factory = new ManagedAutonomyFactory(root, token);
            using var client = factory.CreateClient();
            var companionId = (await CreateCompanionAsync(client, "归属伙伴")).GetProperty("id").GetGuid();

            var projectPath = Path.Combine(root, "project");
            Directory.CreateDirectory(projectPath);
            using var open = await client.PostAsJsonAsync("/api/v1/storage/scopes/open", new { project_path = projectPath });
            open.EnsureSuccessStatusCode();
            var storageId = (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("storage_id").GetString()!;

            // A project header must not move companions into the project's store: the same companion
            // is still readable, from the user scope it was created in.
            using var projected = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/companions/{companionId}");
            projected.Headers.Add("X-Tinadec-Storage-Id", storageId);
            using var projectedResponse = await client.SendAsync(projected);
            Assert.Equal(HttpStatusCode.OK, projectedResponse.StatusCode);
            Assert.Equal(companionId,
                (await projectedResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());

            // Control: the middleware still honors the header for work-domain routes, so the
            // companion result above is the companion rule, not a globally ignored header.
            using var unknownSessions = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sessions");
            unknownSessions.Headers.Add("X-Tinadec-Storage-Id", "no-such-project");
            using var unknownSessionsResponse = await client.SendAsync(unknownSessions);
            Assert.Equal(HttpStatusCode.NotFound, unknownSessionsResponse.StatusCode);

            using var unknownCompanion = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/companions/{companionId}");
            unknownCompanion.Headers.Add("X-Tinadec-Storage-Id", "no-such-project");
            using var unknownCompanionResponse = await client.SendAsync(unknownCompanion);
            Assert.Equal(HttpStatusCode.OK, unknownCompanionResponse.StatusCode);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Environment.SetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN", previous);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>Managed storage is required to exercise the scope middleware at all.</summary>
    private sealed class ManagedAutonomyFactory(string root, string token) : IsolatedApiFactory
    {
        protected override bool UsesManagedStorage => true;
        protected override string? ManagedUserRoot => Path.Combine(root, "user");

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.Add("X-Tinadec-Host-Control", token);
        }

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecStorage:Enabled"] = "true",
                ["TinadecStorage:UserRoot"] = Path.Combine(root, "user"),
                ["TinadecTools:DefaultWorkspaceRoot"] = Path.Combine(root, "workspace"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }
}
