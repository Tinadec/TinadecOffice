using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Companion;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The live companion feed through the real HTTP host. The journal is the activity table, so these
/// tests write activities the same way the product does and then read the stream: replay from a
/// cursor, a followed event, tenant isolation and a clean disconnect.
/// </summary>
public sealed class CompanionEventTests
{
    private sealed class EventFactory : IsolatedApiFactory
    {
        private readonly string _root = ApiTestStorage.CreateRoot("companion-events");
        public MutableIdentity Identity { get; } = new();

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services => services.AddSingleton<ITenantContextAccessor>(Identity));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class MutableIdentity : ITenantContextAccessor
    {
        public TenantContext Current { get; set; } = new(
            Guid.Parse("c7d1e4f6-a251-4a4e-9a01-1a0f4b77d001"),
            Guid.Parse("c7d1e4f6-a251-4a4e-9a01-1a0f4b77d002"),
            Guid.Parse("c7d1e4f6-a251-4a4e-9a01-1a0f4b77d003"), "owner", true);
    }

    private static async Task<long> WriteActivityAsync(EventFactory factory, string kind, string summary, Guid? tenantId = null)
    {
        var tenant = tenantId ?? factory.Identity.Current.TenantId;
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<CompanionDbContext>>().CreateDbContextAsync();
        db.Activities.Add(new CompanionActivityRecord
        {
            Id = Guid.NewGuid(), TenantId = tenant, Kind = kind, Summary = summary, CreatedAt = DateTimeOffset.UtcNow
        });
        await CompanionActivities.SaveAsync(db, tenant, CancellationToken.None);
        return await db.Activities.AsNoTracking()
            .Where(x => x.TenantId == tenant && x.Summary == summary)
            .Select(x => x.Seq)
            .SingleAsync();
    }

    private static async Task<string> ReadFrameAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) throw new EndOfStreamException("The SSE response ended before a complete frame was received.");
            if (line.Length == 0) return string.Join("\n", lines);
            lines.Add(line);
        }
    }

    private static async Task<(HttpResponseMessage Response, StreamReader Reader)> SubscribeAsync(
        HttpClient client, string query, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/companion-events{query}");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return (response, new StreamReader(stream));
    }

    [Fact]
    public async Task FeedReplaysFromTheCursor_AndThenFollows()
    {
        using var factory = new EventFactory();
        using var client = factory.CreateClient();
        var first = await WriteActivityAsync(factory, "message_sent", "first-activity");
        var second = await WriteActivityAsync(factory, "run_completed", "second-activity");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (response, reader) = await SubscribeAsync(client, $"?after_seq={first}", cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").Single());

        // The resumed cursor is published before any event so an idle reconnect still learns it.
        var heartbeat = await ReadFrameAsync(reader, cts.Token);
        Assert.Contains("event: heartbeat", heartbeat);
        Assert.Contains($"\"after_seq\":{first}", heartbeat);

        // Replay honors the cursor: only the second row, with its own seq as the SSE id.
        var replayed = await ReadFrameAsync(reader, cts.Token);
        Assert.Contains($"id: {second}", replayed);
        Assert.Contains("event: companion.run", replayed);
        Assert.DoesNotContain("first-activity", replayed);

        // Follow: a row written after the subscription arrived is delivered without reconnecting.
        var third = await WriteActivityAsync(factory, "settings_changed", "settings-changed");
        string followed;
        do followed = await ReadFrameAsync(reader, cts.Token);
        while (!followed.Contains("event: companion.settings"));
        Assert.Contains($"id: {third}", followed);
        cts.Cancel();
    }

    [Fact]
    public async Task LastEventIdHeader_ResumesWhereQueryCursorCannot()
    {
        using var factory = new EventFactory();
        using var client = factory.CreateClient();
        var first = await WriteActivityAsync(factory, "message_sent", "header-cursor-one");
        await WriteActivityAsync(factory, "message_sent", "header-cursor-two");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/companion-events");
        request.Headers.TryAddWithoutValidation("Last-Event-ID", first.ToString());
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        Assert.Contains("event: heartbeat", await ReadFrameAsync(reader, cts.Token));
        var replayed = await ReadFrameAsync(reader, cts.Token);
        Assert.Contains("event: companion.activity", replayed);
        Assert.Contains("header-cursor-two", replayed);
        cts.Cancel();
    }

    [Fact]
    public async Task AnotherTenantsActivity_NeverAppears()
    {
        using var factory = new EventFactory();
        using var client = factory.CreateClient();
        await WriteActivityAsync(factory, "message_sent", "own-tenant-activity");
        await WriteActivityAsync(factory, "message_sent", "foreign-tenant-activity", Guid.NewGuid());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (response, reader) = await SubscribeAsync(client, "?after_seq=0", cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Contains("event: heartbeat", await ReadFrameAsync(reader, cts.Token));
        var frame = await ReadFrameAsync(reader, cts.Token);
        Assert.Contains("own-tenant-activity", frame);
        Assert.DoesNotContain("foreign-tenant-activity", frame);
        // A batch of events is always closed by a heartbeat, so this frame is that heartbeat.
        Assert.Contains("event: heartbeat", await ReadFrameAsync(reader, cts.Token));

        // A leaked foreign row would be the very next frame. Nothing else is due (the heartbeat is
        // 15s away), so a short read that times out is the proof that the feed is tenant-scoped.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        idle.CancelAfter(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadFrameAsync(reader, idle.Token));
        cts.Cancel();
    }

    [Fact]
    public async Task SubscriberDisconnectEndsTheStreamCleanly()
    {
        using var factory = new EventFactory();
        using var client = factory.CreateClient();
        await WriteActivityAsync(factory, "message_sent", "before-disconnect");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (response, reader) = await SubscribeAsync(client, "?after_seq=0", cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("event: heartbeat", await ReadFrameAsync(reader, cts.Token));
        Assert.Contains("before-disconnect", await ReadFrameAsync(reader, cts.Token));

        cts.Cancel();
        reader.Dispose();
        response.Dispose();

        // The host stays healthy after the subscriber leaves; the feed is stateless per request.
        using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (again, againReader) = await SubscribeAsync(client, "?after_seq=0", probe.Token);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Contains("event: heartbeat", await ReadFrameAsync(againReader, probe.Token));
        probe.Cancel();
        again.Dispose();
    }

    [Fact]
    public async Task SettingsChange_IsRecordedAsItsOwnFeedEvent()
    {
        using var factory = new EventFactory();
        using var client = factory.CreateClient();
        using (var created = await client.PostAsJsonAsync("/api/v1/companions", new { name = "设置伙伴" }))
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        using var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/companion-settings")
        {
            Content = JsonContent.Create(new { autonomy_paused = true })
        };
        patch.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        using var patched = await client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (response, reader) = await SubscribeAsync(client, "?after_seq=0", cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("event: heartbeat", await ReadFrameAsync(reader, cts.Token));

        string frame;
        do frame = await ReadFrameAsync(reader, cts.Token);
        while (!frame.Contains("event: companion.settings"));
        Assert.Contains("\"autonomy_paused\":true", frame);
        cts.Cancel();
    }
}
