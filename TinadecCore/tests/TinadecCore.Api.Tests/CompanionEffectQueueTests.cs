using System.Collections.Concurrent;
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
/// The durable effect queue behind companion runs: a row is written with the run, claimed under a
/// lease, retried with backoff, and healed when a run lost its row. The background loop is disabled
/// in the fixture so each test drives one pass itself instead of racing a timer.
/// </summary>
public sealed class CompanionEffectQueueTests
{
    private sealed class QueueFactory : IsolatedApiFactory
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-companion-effect-tests", Guid.NewGuid().ToString("N"));

        public QueueFactory(string? maxAttempts = null)
        {
            Directory.CreateDirectory(_root);
            MaxAttempts = maxAttempts;
        }

        public string? MaxAttempts { get; }

        public ScriptedChat Chat { get; } = new();

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            var settings = new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["TinadecCompanion:RunDrainEnabled"] = "false",
                ["Logging:LogLevel:Default"] = "Warning"
            };
            if (MaxAttempts is not null) settings["TinadecCompanion:MaxAttempts"] = MaxAttempts;
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureServices(services => services.AddSingleton<IAgentChatClientFactory>(new ScriptedChatFactory(Chat)));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ScriptedChatFactory(ScriptedChat chat) : IAgentChatClientFactory
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

    private sealed class ScriptedChat : IChatClient
    {
        public string Reply { get; set; } = "收到。";
        public string? Failure { get; set; }
        public int Calls;

        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (Failure is not null) throw new InvalidOperationException(Failure);
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

    private static IDbContextFactory<CompanionDbContext> Db(QueueFactory factory) =>
        factory.Services.GetRequiredService<IDbContextFactory<CompanionDbContext>>();

    private static CompanionRuntime Runtime(QueueFactory factory) =>
        factory.Services.GetRequiredService<CompanionRuntime>();

    private static async Task<(Guid CompanionId, Guid ConversationId)> SetupAsync(HttpClient client, string name = "A")
    {
        using var created = await client.PostAsJsonAsync("/api/v1/companions", new { name });
        Assert.Equal(System.Net.HttpStatusCode.OK, created.StatusCode);
        var companionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var conversation = await client.PostAsJsonAsync($"/api/v1/companions/{companionId}/conversations", new { kind = "private" });
        Assert.Equal(System.Net.HttpStatusCode.OK, conversation.StatusCode);
        return (companionId, (await conversation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    private static async Task<Guid> PostMessageAsync(HttpClient client, Guid conversationId, string body)
    {
        using var response = await client.PostAsJsonAsync($"/api/v1/companion-conversations/{conversationId}/messages", new { body });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return Assert.Single(payload.GetProperty("runs").EnumerateArray()).GetProperty("id").GetGuid();
    }

    private static async Task<CompanionRunRecord> RunAsync(QueueFactory factory, Guid runId)
    {
        await using var db = await Db(factory).CreateDbContextAsync();
        return await db.Runs.AsNoTracking().SingleAsync(x => x.Id == runId);
    }

    private static async Task<CompanionEffectRecord> EffectAsync(QueueFactory factory, Guid runId)
    {
        await using var db = await Db(factory).CreateDbContextAsync();
        return await db.Effects.AsNoTracking().SingleAsync(x => x.RunId == runId);
    }

    // ── write → claim → complete ──

    [Fact]
    public async Task MessageWritesAnEffect_ThatOnePassClaimsAndCompletes()
    {
        using var factory = new QueueFactory();
        using var client = factory.CreateClient();
        var (_, conversationId) = await SetupAsync(client);

        var runId = await PostMessageAsync(client, conversationId, "在吗");

        var queued = await EffectAsync(factory, runId);
        Assert.Equal("pending", queued.Status);
        Assert.Equal(CompanionRuntime.RunReplyKind, queued.Kind);
        Assert.Equal(0, queued.Attempts);

        // The drain is off, so the row is still waiting: nothing else moved it.
        Assert.Equal("queued", (await RunAsync(factory, runId)).Status);

        var processed = await Runtime(factory).RunPassAsync();
        Assert.Equal(1, processed);

        var effect = await EffectAsync(factory, runId);
        Assert.Equal("done", effect.Status);
        Assert.Equal(1, effect.Attempts);
        Assert.Null(effect.ClaimToken);

        var run = await RunAsync(factory, runId);
        Assert.Equal("completed", run.Status);
        Assert.NotNull(run.ReplyMessageId);
    }

    // ── lease expiry ──

    [Fact]
    public async Task ExpiredLeaseIsReclaimed_AndTheRunIsExecutedByTheNextPass()
    {
        using var factory = new QueueFactory();
        using var client = factory.CreateClient();
        var (_, conversationId) = await SetupAsync(client);
        var runId = await PostMessageAsync(client, conversationId, "在吗");

        await using (var db = await Db(factory).CreateDbContextAsync())
        {
            var row = await db.Effects.SingleAsync(x => x.RunId == runId);
            // A host that died mid-run leaves exactly this shape behind.
            row.Status = "running";
            row.ClaimToken = "dead-host";
            row.ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
            row.ClaimedAtUnixMs = row.ClaimedAt.Value.ToUnixTimeMilliseconds();
            row.Attempts = 1;
            await db.SaveChangesAsync();
        }

        var processed = await Runtime(factory).RunPassAsync();

        Assert.Equal(1, processed);
        var effect = await EffectAsync(factory, runId);
        Assert.Equal("done", effect.Status);
        Assert.Null(effect.ClaimToken);
        Assert.Equal("completed", (await RunAsync(factory, runId)).Status);
    }

    [Fact]
    public async Task LiveLeaseIsLeftAlone_SoTwoHostsCannotAnswerTheSameMessage()
    {
        using var factory = new QueueFactory();
        using var client = factory.CreateClient();
        var (_, conversationId) = await SetupAsync(client);
        var runId = await PostMessageAsync(client, conversationId, "在吗");

        await using (var db = await Db(factory).CreateDbContextAsync())
        {
            var effect = await db.Effects.SingleAsync(x => x.RunId == runId);
            effect.Status = "running";
            effect.ClaimToken = "other-host";
            effect.ClaimedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
            effect.ClaimedAtUnixMs = effect.ClaimedAt.Value.ToUnixTimeMilliseconds();
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await Runtime(factory).RunPassAsync());
        Assert.Equal(0, factory.Chat.Calls);
        Assert.Equal("running", (await EffectAsync(factory, runId)).Status);
    }

    // ── retry ceiling ──

    [Fact]
    public async Task ModelFailureRetriesWithBackoff_UntilTheAttemptCeilingFailsTheRun()
    {
        using var factory = new QueueFactory(maxAttempts: "2");
        using var client = factory.CreateClient();
        var (_, conversationId) = await SetupAsync(client);
        var runId = await PostMessageAsync(client, conversationId, "在吗");
        factory.Chat.Failure = "provider exploded";

        Assert.Equal(0, await Runtime(factory).RunPassAsync());

        var first = await EffectAsync(factory, runId);
        Assert.Equal("pending", first.Status);
        Assert.Equal(1, first.Attempts);
        Assert.Contains("provider exploded", first.LastError);
        // Backoff must push the row out, otherwise the next pass would spin on a dead provider.
        Assert.True(first.AvailableAt > DateTimeOffset.UtcNow);
        Assert.Equal("running", (await RunAsync(factory, runId)).Status);

        // The row is not due yet: a pass now must not spend the second attempt.
        Assert.Equal(0, await Runtime(factory).RunPassAsync());
        Assert.Equal(1, (await EffectAsync(factory, runId)).Attempts);

        await using (var db = await Db(factory).CreateDbContextAsync())
        {
            var effect = await db.Effects.SingleAsync(x => x.RunId == runId);
            effect.AvailableAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            effect.AvailableAtUnixMs = effect.AvailableAt.ToUnixTimeMilliseconds();
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await Runtime(factory).RunPassAsync());

        var settled = await EffectAsync(factory, runId);
        Assert.Equal("failed", settled.Status);
        Assert.Equal(2, settled.Attempts);
        Assert.Contains("provider exploded", settled.LastError);

        var run = await RunAsync(factory, runId);
        Assert.Equal("failed", run.Status);
        Assert.Equal("companion_run_failed", run.ErrorCode);
    }

    [Fact]
    public async Task MissingCompanionIsNotRetried_ItSettlesFailedImmediately()
    {
        using var factory = new QueueFactory(maxAttempts: "5");
        using var client = factory.CreateClient();
        var (companionId, conversationId) = await SetupAsync(client);
        var runId = await PostMessageAsync(client, conversationId, "在吗");

        await using (var db = await Db(factory).CreateDbContextAsync())
        {
            var companion = await db.Companions.SingleAsync(x => x.Id == companionId);
            companion.Status = "archived";
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await Runtime(factory).RunPassAsync());

        var effect = await EffectAsync(factory, runId);
        Assert.Equal("failed", effect.Status);
        Assert.Equal(1, effect.Attempts);
        var run = await RunAsync(factory, runId);
        Assert.Equal("failed", run.Status);
        Assert.Equal("companion_not_found", run.ErrorCode);
    }

    // ── restart self-heal ──

    [Fact]
    public async Task QueuedRunWithoutAnEffect_IsHealedAndExecuted()
    {
        using var factory = new QueueFactory();
        using var client = factory.CreateClient();
        var (_, conversationId) = await SetupAsync(client);
        var runId = await PostMessageAsync(client, conversationId, "在吗");

        // A run written before the queue existed (or a crash between the two writes) has no effect.
        await using (var db = await Db(factory).CreateDbContextAsync())
        {
            var effect = await db.Effects.SingleAsync(x => x.RunId == runId);
            db.Effects.Remove(effect);
            await db.SaveChangesAsync();
        }
        Assert.Empty(await Effects(factory));
        Assert.Equal("queued", (await RunAsync(factory, runId)).Status);

        var healedCount = await Runtime(factory).SelfHealAsync(CancellationToken.None);
        Assert.Equal(1, healedCount);
        Assert.NotEmpty(await Effects(factory));

        var processed = await Runtime(factory).RunPassAsync();

        Assert.Equal(1, processed);
        var healed = await EffectAsync(factory, runId);
        Assert.Equal("done", healed.Status);
        Assert.Equal("completed", (await RunAsync(factory, runId)).Status);
    }

    [Fact]
    public async Task SelfHealLeavesRunsThatAreNotQueuedAlone()
    {
        using var factory = new QueueFactory();
        using var client = factory.CreateClient();
        var (_, conversationId) = await SetupAsync(client);
        var runId = await PostMessageAsync(client, conversationId, "在吗");

        await using (var db = await Db(factory).CreateDbContextAsync())
        {
            var run = await db.Runs.SingleAsync(x => x.Id == runId);
            run.Status = "completed";
            run.FinishedAt = DateTimeOffset.UtcNow;
            var effect = await db.Effects.SingleAsync(x => x.RunId == runId);
            db.Effects.Remove(effect);
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await Runtime(factory).RunPassAsync());
        Assert.Empty(await Effects(factory));
        Assert.Equal(0, factory.Chat.Calls);
    }

    private static async Task<List<CompanionEffectRecord>> Effects(QueueFactory factory)
    {
        await using var db = await Db(factory).CreateDbContextAsync();
        return await db.Effects.AsNoTracking().ToListAsync();
    }
}
