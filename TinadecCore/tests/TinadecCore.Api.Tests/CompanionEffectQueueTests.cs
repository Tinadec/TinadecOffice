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
/// Consolidated lease queue on <see cref="CompanionRunRecord"/>: runs are claimed directly via
/// <c>ClaimToken</c> / <c>AvailableAtUnixMs</c>, expired leases are reclaimed, live leases are
/// fenced, and transient failures back off until <c>MaxAttempts</c>.
/// </summary>
public sealed class CompanionEffectQueueTests
{
    private sealed class QueueFactory : IsolatedApiFactory
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-companion-lease-tests", Guid.NewGuid().ToString("N"));

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

    [Fact]
    public async Task QueuedRunIsClaimedAndCompletedOnDirectRunLease()
    {
        using var factory = new QueueFactory();
        using var client = factory.CreateClient();
        var (_, conversationId) = await SetupAsync(client);

        var runId = await PostMessageAsync(client, conversationId, "在吗");
        var queued = await RunAsync(factory, runId);
        Assert.Equal("queued", queued.Status);
        Assert.Equal(0, queued.Attempts);

        var processed = await Runtime(factory).RunPassAsync();
        Assert.Equal(1, processed);

        var completed = await RunAsync(factory, runId);
        Assert.Equal("completed", completed.Status);
        Assert.Equal(1, completed.Attempts);
        Assert.Null(completed.ClaimToken);
        Assert.NotNull(completed.ReplyMessageId);
    }

    [Fact]
    public async Task ExpiredLeaseIsReclaimed_WhileLiveLeaseIsFenced()
    {
        using var factory = new QueueFactory();
        using var client = factory.CreateClient();
        var (_, conversationId) = await SetupAsync(client);
        var runId = await PostMessageAsync(client, conversationId, "在吗");

        await using (var db = await Db(factory).CreateDbContextAsync())
        {
            var row = await db.Runs.SingleAsync(x => x.Id == runId);
            row.Status = "running";
            row.ClaimToken = "live-host";
            row.ClaimedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
            row.ClaimedAtUnixMs = row.ClaimedAt.Value.ToUnixTimeMilliseconds();
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await Runtime(factory).RunPassAsync());
        Assert.Equal(0, factory.Chat.Calls);

        await using (var db = await Db(factory).CreateDbContextAsync())
        {
            var row = await db.Runs.SingleAsync(x => x.Id == runId);
            row.ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
            row.ClaimedAtUnixMs = row.ClaimedAt.Value.ToUnixTimeMilliseconds();
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await Runtime(factory).RunPassAsync());
        var settled = await RunAsync(factory, runId);
        Assert.Equal("completed", settled.Status);
        Assert.Null(settled.ClaimToken);
    }

    [Fact]
    public async Task ModelFailureBacksOff_AndSettlesFailedAtMaxAttempts()
    {
        using var factory = new QueueFactory(maxAttempts: "2");
        using var client = factory.CreateClient();
        var (_, conversationId) = await SetupAsync(client);
        var runId = await PostMessageAsync(client, conversationId, "在吗");
        factory.Chat.Failure = "provider exploded";

        Assert.Equal(0, await Runtime(factory).RunPassAsync());
        var first = await RunAsync(factory, runId);
        Assert.Equal("queued", first.Status);
        Assert.Equal(1, first.Attempts);
        Assert.Contains("provider exploded", first.LastError);

        await using (var db = await Db(factory).CreateDbContextAsync())
        {
            var row = await db.Runs.SingleAsync(x => x.Id == runId);
            row.AvailableAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            row.AvailableAtUnixMs = row.AvailableAt.ToUnixTimeMilliseconds();
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await Runtime(factory).RunPassAsync());
        var terminal = await RunAsync(factory, runId);
        Assert.Equal("failed", terminal.Status);
        Assert.Equal(2, terminal.Attempts);
    }
}
