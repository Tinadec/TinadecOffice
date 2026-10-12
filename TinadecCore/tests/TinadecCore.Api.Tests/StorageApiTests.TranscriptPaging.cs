using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

public sealed partial class StorageApiTests
{
    [Fact]
    public async Task MessagePage_TenThousandRowsReadsOnlyLatestHundredBodies()
    {
        using var client = _factory!.CreateClient();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { title = "Large history" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = session.GetProperty("id").GetGuid();
        await client.PostAsJsonAsync($"/api/v1/sessions/{id}/messages", new { content = "Stored body" });
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>().CreateDbContextAsync();
        var seed = await db.Messages.SingleAsync(x => x.SessionId == id);
        for (var sequence = 2; sequence <= 10_000; sequence++)
        {
            db.Messages.Add(new MessageRecord { Id = Guid.NewGuid(), TenantId = seed.TenantId, WorkspaceId = seed.WorkspaceId,
                SessionId = id, Sequence = sequence, Role = "user", ContentReference = sequence <= 9_900 ? "missing-old-body" : seed.ContentReference,
                ContentHash = seed.ContentHash, ContentLength = seed.ContentLength, CreatedAt = seed.CreatedAt });
        }
        seed.ContentReference = "missing-old-body";
        await db.SaveChangesAsync();
        var latestMessageId = await db.Messages.Where(x => x.SessionId == id && x.Sequence == 10_000).Select(x => x.Id).SingleAsync();
        foreach (var messageId in new[] { seed.Id, latestMessageId }) db.MessageAttachments.Add(new MessageAttachmentRecord
        {
            Id = Guid.NewGuid(), TenantId = seed.TenantId, WorkspaceId = seed.WorkspaceId, SessionId = id, MessageId = messageId,
            FileName = messageId == latestMessageId ? "latest.txt" : "old.txt", ContentReference = "unread-attachment",
            ContentHash = seed.ContentHash, CreatedAt = seed.CreatedAt,
        });
        await db.SaveChangesAsync();
        var pageAttachments = await _factory.Services.GetRequiredService<IMessageAttachmentStore>().ListForMessagesAsync(id, [latestMessageId]);
        Assert.Equal("latest.txt", Assert.Single(pageAttachments).FileName);
        var response = await client.GetAsync($"/api/v1/sessions/{id}/messages/page");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = page.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(100, items.Length);
        Assert.Equal(9_901, items[0].GetProperty("sequence").GetInt64());
        Assert.Equal(10_000, items[^1].GetProperty("sequence").GetInt64());
        Assert.All(items, item => Assert.Equal("Stored body", item.GetProperty("content").GetString()));
        Assert.Equal("latest.txt", items[^1].GetProperty("attachments")[0].GetProperty("file_name").GetString());
        Assert.Single(items.SelectMany(x => x.GetProperty("attachments").EnumerateArray()));
        Assert.True(page.GetProperty("has_more").GetBoolean());
        Assert.Equal(9_901, page.GetProperty("next_before_sequence").GetInt64());
        Assert.Equal(0, page.GetProperty("revert_epoch").GetInt64());
    }

    [Fact]
    public async Task MessagePage_KeysetAppendAndRevertAreIndependent()
    {
        using var client = _factory!.CreateClient();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { title = "Paging" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = session.GetProperty("id").GetGuid();
        var store = _factory.Services.GetRequiredService<ProjectSessionStore>();
        var messages = new List<StoredMessage>();
        for (var i = 1; i <= 105; i++) messages.Add(await store.AddMessageAsync(id, $"message {i}"));
        var first = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{id}/messages/page");
        Assert.Equal(6, first.GetProperty("items")[0].GetProperty("sequence").GetInt64());
        await store.AddMessageAsync(id, "append after snapshot");
        var older = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{id}/messages/page?before_sequence=6&expected_revert_epoch=0");
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, older.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("sequence").GetInt64()));
        Assert.False(older.GetProperty("has_more").GetBoolean());
        await store.RevertHistoryAsync(id, messages[99].Id);
        using var conflict = await client.GetAsync($"/api/v1/sessions/{id}/messages/page?before_sequence=6&expected_revert_epoch=0");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var problem = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("history_changed", problem.GetProperty("code").GetString());
        Assert.Equal(7, problem.GetProperty("revert_epoch").GetInt64());
        var current = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{id}/messages/page");
        Assert.Equal(99, current.GetProperty("items").GetArrayLength());
        Assert.Equal(7, current.GetProperty("revert_epoch").GetInt64());
    }

    [Fact]
    public async Task Activity_OnlyRequestedAndActiveRunsAreReplayedAndTimelineIsBounded()
    {
        using var client = _factory!.CreateClient();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { title = "Activities" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = session.GetProperty("id").GetGuid();
        var store = _factory.Services.GetRequiredService<ProjectSessionStore>();
        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var runs = new List<RunRecord>();
        for (var i = 0; i < 3; i++)
        {
            var message = await store.AddMessageAsync(id, $"run {i}");
            var run = await lifecycle.StartRunAsync(id, message.Id);
            runs.Add(run);
            await lifecycle.AppendEventAsync(run.Id, "run.started", new { value = i }, "Run started");
            for (var tool = 0; tool < 4; tool++) await lifecycle.AppendEventAsync(run.Id, "tool.execution.completed", new { execution_id = Guid.NewGuid(), tool_id = "read_file" }, "Tool completed");
        }
        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync())
        {
            await db.Runs.Where(x => x.Id == runs[0].Id || x.Id == runs[1].Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "completed"));
        }
        var activity = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{id}/activity?run_ids={runs[1].Id}");
        var returnedRuns = activity.GetProperty("runs").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(2, returnedRuns.Length);
        Assert.Contains(runs[1].Id, returnedRuns);
        Assert.Contains(runs[2].Id, returnedRuns);
        Assert.DoesNotContain(runs[0].Id, returnedRuns);
        Assert.All(activity.GetProperty("events").EnumerateArray(), x => Assert.Contains(x.GetProperty("run_id").GetGuid(), returnedRuns));
        var tools = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{id}/tool-executions?run_id={runs[1].Id}&limit=2");
        Assert.Equal(2, tools!.Length);
        Assert.All(tools, x => Assert.Equal(runs[1].Id, x.GetProperty("run_id").GetGuid()));
    }

    [Fact]
    public async Task RevertedHistory_SessionSnapshotsExcludeOldRunsButRunAuditRemainsReadable()
    {
        using var client = _factory!.CreateClient();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { title = "Reverted activity" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = session.GetProperty("id").GetGuid();
        var store = _factory.Services.GetRequiredService<ProjectSessionStore>();
        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var messages = new List<StoredMessage>();
        var runs = new List<RunRecord>();
        foreach (var status in new[] { "completed", "failed", "cancelled" })
        {
            var message = await store.AddMessageAsync(id, status);
            messages.Add(message);
            var run = await lifecycle.StartRunAsync(id, message.Id);
            runs.Add(run);
            await lifecycle.AppendEventAsync(run.Id, "tool.execution.completed", new { execution_id = Guid.NewGuid(), tool_id = "read_file" }, status);
            if (status == "completed") await lifecycle.SetRunStatusAsync(run.Id, "executing");
            await lifecycle.SetRunStatusAsync(run.Id, status);
        }
        var before = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{id}/orchestration");
        Assert.Equal(runs[2].Id, before.GetProperty("run").GetProperty("id").GetGuid());

        using var reverted = await client.PostAsync($"/api/v1/sessions/{id}/messages/{messages[1].Id}/revert", null);
        Assert.Equal(HttpStatusCode.OK, reverted.StatusCode);
        var snapshot = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{id}/orchestration");
        Assert.Equal(runs[0].Id, snapshot.GetProperty("run").GetProperty("id").GetGuid());
        var requested = string.Join(',', runs.Select(x => x.Id));
        var activity = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{id}/activity?run_ids={requested}");
        Assert.Equal(runs[0].Id, Assert.Single(activity.GetProperty("runs").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.All(activity.GetProperty("events").EnumerateArray(), x => Assert.Equal(runs[0].Id, x.GetProperty("run_id").GetGuid()));
        Assert.All(activity.GetProperty("tool_executions").EnumerateArray(), x => Assert.Equal(runs[0].Id, x.GetProperty("run_id").GetGuid()));
        var audit = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runs[2].Id}/orchestration");
        Assert.Equal(runs[2].Id, audit.GetProperty("run").GetProperty("id").GetGuid());

        using var allReverted = await client.PostAsync($"/api/v1/sessions/{id}/messages/{messages[0].Id}/revert", null);
        Assert.Equal(HttpStatusCode.OK, allReverted.StatusCode);
        var empty = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{id}/orchestration");
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("run").ValueKind);
        var emptyActivity = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{id}/activity?run_ids={requested}");
        Assert.Empty(emptyActivity.GetProperty("runs").EnumerateArray());
        Assert.Empty(emptyActivity.GetProperty("events").EnumerateArray());

        // Public revert refuses an active run. A persisted active fact still belongs
        // to the explicitly visible live range even when its trigger was reverted.
        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync())
            await db.Runs.Where(x => x.Id == runs[2].Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "planning"));
        var live = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{id}/activity");
        Assert.Equal(runs[2].Id, Assert.Single(live.GetProperty("runs").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task EventsLiveOnly_ReadyBeforeSnapshotThenNewRunAndHistoryControl()
    {
        using var client = _factory!.CreateClient();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { title = "Live only" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = session.GetProperty("id").GetGuid();
        var store = _factory.Services.GetRequiredService<ProjectSessionStore>();
        var first = await store.AddMessageAsync(id, "before subscribe");
        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var run = await lifecycle.StartRunAsync(id, first.Id);
        await lifecycle.AppendEventAsync(run.Id, "run.started", new { }, "Run started");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/events?session_id={id}&replay=false"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);
        var ready = await ReadSseFrameAsync(reader, cts.Token);
        Assert.Contains("event: stream.ready", ready);
        Assert.DoesNotContain("id:", ready);
        await lifecycle.AppendEventAsync(run.Id, "task.assigned", new { }, "Task assigned");
        var next = await ReadSseFrameAsync(reader, cts.Token);
        Assert.Contains("event: task.assigned", next);
        Assert.Contains("id: 2", next);
        var second = await store.AddMessageAsync(id, "new run");
        var secondRun = await lifecycle.StartRunAsync(id, second.Id);
        await lifecycle.AppendEventAsync(secondRun.Id, "run.started", new { }, "New run started");
        Assert.Contains("id: 1", await ReadSseFrameAsync(reader, cts.Token));
        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync())
            await db.Runs.Where(x => x.SessionId == id).ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "completed"));
        using var otherWindow = _factory.CreateClient();
        using var reverted = await otherWindow.PostAsync($"/api/v1/sessions/{id}/messages/{second.Id}/revert", null);
        Assert.Equal(HttpStatusCode.OK, reverted.StatusCode);
        var changed = await ReadSseFrameAsync(reader, cts.Token);
        Assert.Contains("event: history.changed", changed);
        Assert.Contains("\"revert_epoch\":1", changed);
        Assert.DoesNotContain("id:", changed);
        cts.Cancel();
    }

    [Fact]
    public async Task MessagePage_ConcurrentRevertRetriesFirstPageOnlyOnce()
    {
        Func<Task>? onRead = null;
        var raceRoot = Path.Combine(_root, "read-race");
        Directory.CreateDirectory(raceRoot);
        using var factory = new StorageFactory(raceRoot, services =>
            services.AddSingleton(sp => new ProjectSessionStore(sp.GetRequiredService<IDbContextFactory<MemoryDbContext>>(),
                sp.GetRequiredService<StoragePaths>(), new ReadHookContentStore(sp.GetRequiredService<IContentStore>(), () => onRead?.Invoke() ?? Task.CompletedTask),
                sp.GetRequiredService<ITenantContextAccessor>())));
        using var client = factory.CreateClient();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { title = "Read race" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = session.GetProperty("id").GetGuid();
        var store = factory.Services.GetRequiredService<ProjectSessionStore>();
        await store.AddMessageAsync(id, "one");
        await store.AddMessageAsync(id, "two");
        var third = await store.AddMessageAsync(id, "three");
        var reads = 0;
        onRead = async () => { if (++reads == 1) await store.RevertHistoryAsync(id, third.Id); };
        using var recovered = await client.GetAsync($"/api/v1/sessions/{id}/messages/page");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(2, (await recovered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength());
        Assert.Equal(5, reads); // three raced bodies, then two from the only retry

        onRead = null;
        var nextThird = await store.AddMessageAsync(id, "new three");
        var fourth = await store.AddMessageAsync(id, "four");
        reads = 0;
        onRead = async () =>
        {
            reads++;
            if (reads == 1) await store.RevertHistoryAsync(id, fourth.Id);
            if (reads == 5) await store.RevertHistoryAsync(id, nextThird.Id);
        };
        using var repeated = await client.GetAsync($"/api/v1/sessions/{id}/messages/page");
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        Assert.Equal("history_changed", (await repeated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(7, reads); // four bodies, then three; no third attempt
    }

    private sealed class ReadHookContentStore(IContentStore inner, Func<Task> onRead) : IContentStore
    {
        public Task<ContentReference> PutAsync(ContentWriteRequest request, CancellationToken ct = default) => inner.PutAsync(request, ct);
        public async Task<Stream> OpenReadAsync(ContentReference reference, CancellationToken ct = default)
        {
            if (reference.MediaType == "text/plain; charset=utf-8") await onRead();
            return await inner.OpenReadAsync(reference, ct);
        }
        public Task<bool> ExistsAsync(ContentReference reference, CancellationToken ct = default) => inner.ExistsAsync(reference, ct);
        public Task DeleteAsync(ContentReference reference, CancellationToken ct = default) => inner.DeleteAsync(reference, ct);
    }
}
