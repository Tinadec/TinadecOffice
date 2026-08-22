using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

public sealed class StorageApiTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-core-storage-tests", Guid.NewGuid().ToString("N"));
    private StorageFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new StorageFactory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ProjectSessionAndMessageContent_AreStoredWithSnakeCaseCompatibility()
    {
        var client = _factory!.CreateClient();
        var rootPath = Path.Combine(_root, "workspace");
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new { name = "Storage test", path = rootPath });
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        using var projectJson = JsonDocument.Parse(await projectResponse.Content.ReadAsStringAsync());
        Assert.True(projectJson.RootElement.TryGetProperty("path", out _));
        var projectId = projectJson.RootElement.GetProperty("id").GetGuid();

        var sessionResponse = await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = projectId, title = "Session" });
        Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
        using var sessionJson = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
        Assert.True(sessionJson.RootElement.TryGetProperty("project_id", out _));
        var sessionId = sessionJson.RootElement.GetProperty("id").GetGuid();

        var messageResponse = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "Hello history" });
        Assert.Equal(HttpStatusCode.Created, messageResponse.StatusCode);
        var messages = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/messages");
        Assert.Single(messages!);
        Assert.Equal("Hello history", messages![0].GetProperty("content").GetString());
        var contentRoot = Path.Combine(_root, "data", "content");
        Assert.True(Directory.Exists(contentRoot));
        Assert.NotEmpty(Directory.EnumerateFiles(contentRoot, "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(_root, "data", "sessions", sessionId + ".json")));

        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>().CreateDbContextAsync();
        var message = await db.Messages.SingleAsync(item => item.SessionId == sessionId);
        Assert.Equal(1, message.Sequence);
        var reference = new ContentReference(message.ContentReference, message.ContentHash, message.ContentLength, "text/plain; charset=utf-8");
        Assert.True(await _factory.Services.GetRequiredService<IContentStore>().ExistsAsync(reference));

        var duplicate = await client.PostAsJsonAsync("/api/v1/projects", new { name = "Duplicate", path = rootPath + Path.DirectorySeparatorChar });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task LegacySessionJson_ImportsOnceIntoRelationalMessagesAndContentStore()
    {
        var client = _factory!.CreateClient();
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Legacy import", path = Path.Combine(_root, "legacy-workspace") })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "Legacy session" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var legacyPath = Path.Combine(_root, "data", "sessions", sessionId + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        var legacy = new SessionHistoryFile
        {
            SessionId = sessionId,
            Revision = 1,
            Messages =
            [
                new StoredMessage
                {
                    Id = Guid.NewGuid(),
                    SessionId = sessionId,
                    Sequence = 1,
                    Role = "user",
                    Content = "Imported legacy history",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ]
        };
        await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(legacy));

        var store = _factory.Services.GetRequiredService<ProjectSessionStore>();
        await store.MigrateAsync();
        await store.MigrateAsync();

        var messages = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/messages");
        var message = Assert.Single(messages!);
        Assert.Equal("Imported legacy history", message.GetProperty("content").GetString());

        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>().CreateDbContextAsync();
        Assert.Single(await db.Messages.Where(item => item.SessionId == sessionId).ToListAsync());
    }

    [Fact]
    public async Task RunEvents_AreIndexedReplayedAndRemainBelowDataRoot()
    {
        var client = _factory!.CreateClient();
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Event test", path = Path.Combine(_root, "workspace-events") })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "Event session" })).Content.ReadFromJsonAsync<JsonElement>();
        var message = await (await client.PostAsJsonAsync($"/api/v1/sessions/{session.GetProperty("id").GetGuid()}/messages", new { content = "Trigger" })).Content.ReadFromJsonAsync<JsonElement>();

        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var run = await lifecycle.StartRunAsync(session.GetProperty("id").GetGuid(), message.GetProperty("id").GetGuid());
        await lifecycle.AppendEventAsync(run.Id, "run.started", new { source = "test" }, "Run started");
        var events = await lifecycle.ReplayEventsAsync(session.GetProperty("id").GetGuid(), 0);

        Assert.Single(events);
        Assert.Equal("run.started", events[0].EventType);
        Assert.True(File.Exists(Path.Combine(_root, "data", "tasks", run.Id + ".tasks.json")));
        Assert.True(File.Exists(Path.Combine(_root, "data", "events", run.Id + ".events.jsonl")));
    }

    [Fact]
    public async Task DebugSnapshot_PersistsTenantScopedRunTaskAndEventProjection()
    {
        var client = _factory!.CreateClient();
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new
        {
            name = "Debug snapshot test",
            path = Path.Combine(_root, "workspace-debug-snapshot")
        })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new
        {
            project_id = project.GetProperty("id").GetGuid(),
            title = "Debug snapshot session"
        })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var message = await (await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "Snapshot trigger" })).Content.ReadFromJsonAsync<JsonElement>();

        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var run = await lifecycle.StartRunAsync(sessionId, message.GetProperty("id").GetGuid());
        await lifecycle.UpdateTaskAsync(run.Id, new { task_id = Guid.NewGuid(), status = "running" });
        await lifecycle.AppendEventAsync(run.Id, "run.started", new { source = "debug-test" }, "Run started");

        var first = await client.GetFromJsonAsync<JsonElement>($"/api/v1/debug/snapshot/{sessionId}");
        Assert.Equal(sessionId, first.GetProperty("session_id").GetGuid());
        Assert.Equal("session_runtime", first.GetProperty("kind").GetString());
        Assert.Equal("lifecycle_projection", first.GetProperty("source").GetString());
        Assert.Equal(1, first.GetProperty("runs").GetArrayLength());
        Assert.Equal(run.Id, first.GetProperty("runs")[0].GetProperty("id").GetGuid());
        Assert.Single(first.GetProperty("tasks").EnumerateArray());
        Assert.Single(first.GetProperty("events").EnumerateArray());
        var revision = first.GetProperty("revision").GetInt64();

        var second = await client.GetFromJsonAsync<JsonElement>($"/api/v1/debug/snapshot/{sessionId}");
        Assert.Equal(revision, second.GetProperty("revision").GetInt64());
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync();
        Assert.Equal(1, await db.SessionMetadataSnapshots.CountAsync(x => x.SessionId == sessionId));
    }

    [Fact]
    public async Task WorkspaceSnapshot_IsIdempotentAndRestoresOnlyAfterConflictCheck()
    {
        var client = _factory!.CreateClient();
        var workspace = Path.Combine(_root, "workspace-snapshot");
        Directory.CreateDirectory(workspace);
        var file = Path.Combine(workspace, "note.txt");
        await File.WriteAllTextAsync(file, "before");
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Workspace snapshot", path = workspace }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("id").GetGuid();

        var firstResponse = await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/snapshots", new
        {
            idempotency_key = "snapshot-1",
            max_files = 100,
            max_bytes = 1024 * 1024
        });
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();
        var snapshotId = first.GetProperty("id").GetGuid();
        var secondResponse = await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/snapshots", new { idempotency_key = "snapshot-1" });
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        var second = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(snapshotId, second.GetProperty("id").GetGuid());

        await File.WriteAllTextAsync(file, "after");
        var added = Path.Combine(workspace, "created-after-snapshot.txt");
        await File.WriteAllTextAsync(added, "should be removed");
        var restoreConflict = await client.PostAsJsonAsync($"/api/v1/workspace-snapshots/{snapshotId}/restore", new
        {
            idempotency_key = "restore-1"
        });
        Assert.Equal(HttpStatusCode.Conflict, restoreConflict.StatusCode);
        Assert.Equal("after", await File.ReadAllTextAsync(file));
        Assert.True(File.Exists(added));

        var restoreAllowed = await client.PostAsJsonAsync($"/api/v1/workspace-snapshots/{snapshotId}/restore", new
        {
            idempotency_key = "restore-1-allowed",
            allow_conflicts = true
        });
        Assert.Equal(HttpStatusCode.OK, restoreAllowed.StatusCode);
        var restore = await restoreAllowed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("restored_with_conflicts", restore.GetProperty("status").GetString());
        Assert.Equal("before", await File.ReadAllTextAsync(file));
        Assert.False(File.Exists(added));

        var replay = await client.PostAsJsonAsync($"/api/v1/workspace-snapshots/{snapshotId}/restore", new
        {
            idempotency_key = "restore-1-allowed",
            allow_conflicts = false
        });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(restore.GetProperty("restored_at").GetDateTimeOffset(),
            (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("restored_at").GetDateTimeOffset());
    }

    [Fact]
    public async Task WorkspaceDefaults_UsesEtagsAndRejectsUnpublishedReferences()
    {
        var client = _factory!.CreateClient();
        var draft = await client.PutAsJsonAsync("/api/v1/workspace-defaults/draft", new
        {
            default_agent_definition_id = Guid.NewGuid(),
            default_agent_mode_id = Guid.NewGuid(),
            default_prompt_pipeline_id = Guid.NewGuid()
        });
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        var draftBody = await draft.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("draft", draftBody.GetProperty("status").GetString());
        var revision = draftBody.GetProperty("revision").GetInt64();
        Assert.Equal($"\"{revision}\"", draft.Headers.ETag?.Tag);

        var stale = new HttpRequestMessage(HttpMethod.Put, "/api/v1/workspace-defaults/draft")
        {
            Content = JsonContent.Create(new { default_agent_definition_id = Guid.NewGuid() })
        };
        stale.Headers.TryAddWithoutValidation("If-Match", $"\"{revision - 1}\"");
        using (stale)
        using (var staleResponse = await client.SendAsync(stale))
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);
        }

        using var publish = await client.PostAsync("/api/v1/workspace-defaults/publish", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
        var error = await publish.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("published", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EventsSse_ReplaysThenFollowsDurableEventsWithHeartbeatAndCursor()
    {
        var client = _factory!.CreateClient();
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "SSE test", path = Path.Combine(_root, "workspace-sse") })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "SSE session" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var message = await (await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "SSE trigger" })).Content.ReadFromJsonAsync<JsonElement>();
        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var run = await lifecycle.StartRunAsync(sessionId, message.GetProperty("id").GetGuid());
        await lifecycle.AppendEventAsync(run.Id, "run.started", new { source = "sse-test" }, "Run started");

        using var followCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/events?session_id={sessionId}&after_seq=0");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, followCts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync(followCts.Token);
        using var reader = new StreamReader(stream);
        var replayed = await ReadSseFrameAsync(reader, followCts.Token);
        Assert.Contains("id: 1", replayed);
        Assert.Contains("event: run.started", replayed);
        var replayedData = replayed.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
        using (var document = JsonDocument.Parse(replayedData))
        {
            Assert.Equal("run.started", document.RootElement.GetProperty("event_type").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("payload").GetProperty("sequence").GetInt64());
        }

        var heartbeat = await ReadSseFrameAsync(reader, followCts.Token);
        Assert.Contains("event: heartbeat", heartbeat);
        Assert.Contains("data: {\"after_seq\":1}", heartbeat);

        await lifecycle.AppendEventAsync(run.Id, "task.assigned", new { source = "sse-test" }, "Task assigned");
        var followed = await ReadSseFrameAsync(reader, followCts.Token);
        Assert.Contains("id: 2", followed);
        Assert.Contains("event: task.assigned", followed);

        // A second run restarts its local event sequence at one. The active session
        // feed must still observe its new durable event after the first run reached two.
        var secondMessage = await (await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "Second SSE trigger" })).Content.ReadFromJsonAsync<JsonElement>();
        var secondRun = await lifecycle.StartRunAsync(sessionId, secondMessage.GetProperty("id").GetGuid());
        await lifecycle.AppendEventAsync(secondRun.Id, "run.started", new { source = "sse-test" }, "Second run started");
        var secondRunEvent = await ReadSseFrameAsync(reader, followCts.Token);
        Assert.Contains("id: 1", secondRunEvent);
        Assert.Contains("event: run.started", secondRunEvent);
        var secondRunData = secondRunEvent.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
        using (var document = JsonDocument.Parse(secondRunData))
        {
            Assert.Equal(secondRun.Id, document.RootElement.GetProperty("run_id").GetGuid());
        }
        followCts.Cancel();
    }

    [Fact]
    public async Task DurableRunPrimitives_FreezeCheckpointLeaseAndReplayStream()
    {
        var client = _factory!.CreateClient();
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Durable run", path = Path.Combine(_root, "workspace-durable") })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "Durable session" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var message = await (await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "Durable trigger" })).Content.ReadFromJsonAsync<JsonElement>();
        var messageId = message.GetProperty("id").GetGuid();

        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var first = await lifecycle.StartOrGetRunAsync(sessionId, messageId, new RunStartOptions(null, 1, 7, "baseline", "space", "agent", "default", "space.full_duplex"));
        var retry = await lifecycle.StartOrGetRunAsync(sessionId, messageId, new RunStartOptions(null, 1, 7, "baseline", "space", "agent", "default", "space.full_duplex"));
        Assert.False(first.Existing);
        Assert.True(retry.Existing);
        Assert.Equal(first.Run.Id, retry.Run.Id);

        var frozen = await lifecycle.FreezeRunConfigurationAsync(first.Run.Id,
            new FrozenRunConfigurationWrite("1", "{\"profile\":\"space.full_duplex\"}", []));
        var frozenAgain = await lifecycle.GetFrozenRunConfigurationAsync(first.Run.Id);
        Assert.NotNull(frozenAgain);
        Assert.Equal(frozen.ContentHash, frozenAgain!.ContentHash);
        Assert.Equal("{\"profile\":\"space.full_duplex\"}", frozenAgain.Content);

        var checkpoint = await lifecycle.SaveRunCheckpointAsync(first.Run.Id,
            new RunCheckpointWrite(0, "planned", "{\"tasks\":[]}", IdempotencyKey: "plan:v1"));
        Assert.Equal(1, checkpoint.Revision);
        var checkpointAgain = await lifecycle.SaveRunCheckpointAsync(first.Run.Id,
            new RunCheckpointWrite(0, "planned", "{\"tasks\":[]}", IdempotencyKey: "plan:v1"));
        Assert.Equal(checkpoint.Id, checkpointAgain.Id);
        await Assert.ThrowsAsync<RunCheckpointConflictException>(() => lifecycle.SaveRunCheckpointAsync(first.Run.Id,
            new RunCheckpointWrite(0, "executing", "{\"tasks\":[1]}", IdempotencyKey: "execute:v1")));

        var firstLease = await lifecycle.TryAcquireRunLeaseAsync(first.Run.Id, "host-a", TimeSpan.FromMinutes(1));
        var competingLease = await lifecycle.TryAcquireRunLeaseAsync(first.Run.Id, "host-b", TimeSpan.FromMinutes(1));
        Assert.True(firstLease.Acquired);
        Assert.False(competingLease.Acquired);
        Assert.True(await lifecycle.HeartbeatRunLeaseAsync(first.Run.Id, "host-a", TimeSpan.FromMinutes(1)));
        await lifecycle.ReleaseRunLeaseAsync(first.Run.Id, "host-a");
        Assert.True((await lifecycle.TryAcquireRunLeaseAsync(first.Run.Id, "host-b", TimeSpan.FromMinutes(1))).Acquired);

        var turnId = Guid.NewGuid();
        var ack = await lifecycle.AppendRunStreamAsync(first.Run.Id, new DurableRunStreamAppend(turnId, "ack", IdempotencyKey: "turn:ack"));
        var delta = await lifecycle.AppendRunStreamAsync(first.Run.Id, new DurableRunStreamAppend(turnId, "delta", Delta: "partial", IdempotencyKey: "turn:delta:1"));
        var deltaAgain = await lifecycle.AppendRunStreamAsync(first.Run.Id, new DurableRunStreamAppend(turnId, "delta", Delta: "partial", IdempotencyKey: "turn:delta:1"));
        Assert.Equal(ack.Sequence + 1, delta.Sequence);
        Assert.Equal(delta.Sequence, deltaAgain.Sequence);
        var replayed = await lifecycle.ReplayRunStreamAsync(first.Run.Id, turnId, 0);
        Assert.Collection(replayed,
            item => Assert.Equal("ack", item.Kind),
            item => Assert.Equal("partial", item.Delta));

        static async Task<object> CaptureCheckpointAsync(Task<RunCheckpoint> task)
        {
            try { return await task; }
            catch (Exception exception) { return exception; }
        }

        var competingCheckpoints = await Task.WhenAll(
            CaptureCheckpointAsync(lifecycle.SaveRunCheckpointAsync(first.Run.Id,
                new RunCheckpointWrite(1, "executing", "{\"worker\":\"a\"}", IdempotencyKey: "execute:a"))),
            CaptureCheckpointAsync(lifecycle.SaveRunCheckpointAsync(first.Run.Id,
                new RunCheckpointWrite(1, "executing", "{\"worker\":\"b\"}", IdempotencyKey: "execute:b"))));
        Assert.Single(competingCheckpoints.OfType<RunCheckpoint>());
        Assert.Single(competingCheckpoints.OfType<RunCheckpointConflictException>());

        var concurrentTurnId = Guid.NewGuid();
        var concurrentChunks = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            lifecycle.AppendRunStreamAsync(first.Run.Id,
                new DurableRunStreamAppend(concurrentTurnId, "delta", Delta: index.ToString(), IdempotencyKey: $"parallel:{index}"))));
        Assert.Equal(Enumerable.Range(3, 8).Select(index => (long)index), concurrentChunks.Select(item => item.Sequence).OrderBy(sequence => sequence));

        var idempotentChunks = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            lifecycle.AppendRunStreamAsync(first.Run.Id,
                new DurableRunStreamAppend(concurrentTurnId, "done", FinishReason: "stop", IdempotencyKey: "parallel:done"))));
        Assert.Single(idempotentChunks.Select(item => item.Sequence).Distinct());
    }

    private sealed class StorageFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public StorageFactory(string root) => _root = root;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing");
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
            });
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }

    private static async Task<string> ReadSseFrameAsync(StreamReader reader, CancellationToken cancellationToken)
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
}
