using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Runtime;

namespace TinadecCore.Api.Tests;

public sealed class LifecycleManagementApiTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-core-lifecycle-tests", Guid.NewGuid().ToString("N"));
    private LifecycleFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new LifecycleFactory(_root);
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
    public async Task ProjectAndSessionLifecycle_RenameArchiveTrashRestoreWithListFilters()
    {
        var client = _factory!.CreateClient();
        var project = await CreateProjectAsync(client, "lifecycle-workspace");
        var projectId = project.GetProperty("id").GetGuid();
        Assert.Equal("active", project.GetProperty("lifecycle_status").GetString());
        var session = await CreateSessionAsync(client, projectId, "Lifecycle session");
        var sessionId = session.GetProperty("id").GetGuid();
        Assert.Equal("active", session.GetProperty("lifecycle_status").GetString());

        // Rename project and session.
        var renamed = await (await client.PatchAsJsonAsync($"/api/v1/projects/{projectId}", new { name = "Renamed project" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Renamed project", renamed.GetProperty("name").GetString());
        var renamedSession = await (await client.PatchAsJsonAsync($"/api/v1/sessions/{sessionId}", new { title = "Renamed session" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Renamed session", renamedSession.GetProperty("title").GetString());
        var blankName = await client.PatchAsJsonAsync($"/api/v1/projects/{projectId}", new { name = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, blankName.StatusCode);

        // Invalid transitions fail closed while the session is still active.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/sessions/{sessionId}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/v1/sessions/{sessionId}")).StatusCode);

        // Archive hides the session from the default list and shows it under the archived filter.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/sessions/{sessionId}/archive", null)).StatusCode);
        var activeSessions = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions?project_id={projectId}");
        Assert.Empty(activeSessions!);
        var archivedSessions = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions?project_id={projectId}&lifecycle_status=archived");
        var archived = Assert.Single(archivedSessions!);
        Assert.Equal(sessionId, archived.GetProperty("id").GetGuid());
        Assert.Equal("archived", archived.GetProperty("lifecycle_status").GetString());

        // Purging is still reserved for trashed sessions.
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/v1/sessions/{sessionId}")).StatusCode);

        // Restore brings the session back to the active list.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/sessions/{sessionId}/restore", null)).StatusCode);
        Assert.Single((await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions?project_id={projectId}"))!);

        // Trash moves the session to the trash list with a trashed_at timestamp.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/sessions/{sessionId}/trash", null)).StatusCode);
        var trashedSessions = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions?lifecycle_status=trashed");
        var trashed = Assert.Single(trashedSessions!);
        Assert.Equal(sessionId, trashed.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.String, trashed.GetProperty("trashed_at").ValueKind);

        // The same state machine applies to projects.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/projects/{projectId}/archive", null)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>("/api/v1/projects"))!);
        var archivedProjects = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/projects?lifecycle_status=archived");
        Assert.Equal(projectId, Assert.Single(archivedProjects!).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/projects/{projectId}/restore", null)).StatusCode);

        // Unknown lifecycle status values are rejected.
        var invalid = await client.GetAsync("/api/v1/projects?lifecycle_status=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("INVALID_LIFECYCLE_STATUS", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task LifecycleOperations_AreRejectedWhileARunIsActive()
    {
        var client = _factory!.CreateClient();
        var project = await CreateProjectAsync(client, "active-run-workspace");
        var projectId = project.GetProperty("id").GetGuid();
        var session = await CreateSessionAsync(client, projectId, "Active run session");
        var sessionId = session.GetProperty("id").GetGuid();
        var message = await (await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "Trigger" })).Content.ReadFromJsonAsync<JsonElement>();
        var run = await _factory.Services.GetRequiredService<StorageLifecycleService>().StartRunAsync(sessionId, message.GetProperty("id").GetGuid());

        var archiveResponse = await client.PostAsync($"/api/v1/sessions/{sessionId}/archive", null);
        Assert.Equal(HttpStatusCode.Conflict, archiveResponse.StatusCode);
        var archiveError = await archiveResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("active_run_conflict", archiveError.GetProperty("code").GetString());
        Assert.Equal(run.Id, archiveError.GetProperty("run_id").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/sessions/{sessionId}/trash", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/projects/{projectId}/archive", null)).StatusCode);

        await _factory.Services.GetRequiredService<StorageLifecycleService>().CompleteRunAsync(run.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/sessions/{sessionId}/trash", null)).StatusCode);
    }

    [Fact]
    public async Task RunCompletion_IsIdempotentAndKeepsFirstCompletedAt()
    {
        var client = _factory!.CreateClient();
        var project = await CreateProjectAsync(client, "terminal-idempotency-workspace");
        var session = await CreateSessionAsync(client, project.GetProperty("id").GetGuid(), "Terminal idempotency session");
        var sessionId = session.GetProperty("id").GetGuid();
        var message = await (await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "Terminal trigger" })).Content.ReadFromJsonAsync<JsonElement>();
        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var runs = _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();
        var run = await lifecycle.StartRunAsync(sessionId, message.GetProperty("id").GetGuid());

        await lifecycle.CompleteRunAsync(run.Id);
        DateTimeOffset firstCompletedAt;
        await using (var db = await runs.CreateDbContextAsync())
        {
            var record = await db.Runs.AsNoTracking().SingleAsync(x => x.Id == run.Id);
            Assert.Equal("completed", record.Status);
            firstCompletedAt = record.CompletedAt!.Value;
        }

        await Task.Delay(10);
        await lifecycle.CompleteRunAsync(run.Id);
        await using (var db = await runs.CreateDbContextAsync())
        {
            var record = await db.Runs.AsNoTracking().SingleAsync(x => x.Id == run.Id);
            Assert.Equal("completed", record.Status);
            Assert.Equal(firstCompletedAt, record.CompletedAt!.Value);
        }
    }

    [Fact]
    public async Task RunStatusMachine_RepeatedTerminalStatusKeepsOriginalCompletedAt()
    {
        var client = _factory!.CreateClient();
        var project = await CreateProjectAsync(client, "terminal-rewrite-workspace");
        var session = await CreateSessionAsync(client, project.GetProperty("id").GetGuid(), "Terminal rewrite session");
        var sessionId = session.GetProperty("id").GetGuid();
        var message = await (await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "Terminal rewrite trigger" })).Content.ReadFromJsonAsync<JsonElement>();
        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var runs = _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();
        var run = await lifecycle.StartRunAsync(sessionId, message.GetProperty("id").GetGuid());

        await lifecycle.SetRunStatusAsync(run.Id, "executing");
        await lifecycle.SetRunStatusAsync(run.Id, "failed", "first failure");
        DateTimeOffset firstCompletedAt;
        await using (var db = await runs.CreateDbContextAsync())
        {
            var record = await db.Runs.AsNoTracking().SingleAsync(x => x.Id == run.Id);
            firstCompletedAt = record.CompletedAt!.Value;
        }

        await Task.Delay(10);
        await lifecycle.SetRunStatusAsync(run.Id, "failed", "second failure");
        await using (var db = await runs.CreateDbContextAsync())
        {
            var record = await db.Runs.AsNoTracking().SingleAsync(x => x.Id == run.Id);
            Assert.Equal("failed", record.Status);
            Assert.Equal(firstCompletedAt, record.CompletedAt!.Value);
            // Summary follows the latest transition by design; only the terminal
            // timestamp is frozen.
            Assert.Equal("second failure", record.Summary);
        }
    }

    [Fact]
    public async Task AppendEvent_WithIdempotencyKey_PersistsExactlyOnce()
    {
        var client = _factory!.CreateClient();
        var project = await CreateProjectAsync(client, "event-idempotency-workspace");
        var session = await CreateSessionAsync(client, project.GetProperty("id").GetGuid(), "Event idempotency session");
        var sessionId = session.GetProperty("id").GetGuid();
        var message = await (await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "Event idempotency trigger" })).Content.ReadFromJsonAsync<JsonElement>();
        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var runs = _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();
        var run = await lifecycle.StartRunAsync(sessionId, message.GetProperty("id").GetGuid());

        // A keyed terminal event replays the first record instead of appending twice.
        var first = await lifecycle.AppendEventAsync(run.Id, "run.failed", new { reason = "one" }, "first", "warning", idempotencyKey: $"run:{run.Id}:event:run.failed");
        var replay = await lifecycle.AppendEventAsync(run.Id, "run.failed", new { reason = "two" }, "second", "warning", idempotencyKey: $"run:{run.Id}:event:run.failed");
        Assert.Equal(first.Sequence, replay.Sequence);

        // Unkeyed events keep the legacy append-always behavior.
        await lifecycle.AppendEventAsync(run.Id, "run.failed", new { reason = "three" }, "third", "warning");
        await lifecycle.AppendEventAsync(run.Id, "run.failed", new { reason = "four" }, "fourth", "warning");

        await using var db = await runs.CreateDbContextAsync();
        var total = await db.EventIndex.AsNoTracking().CountAsync(x => x.RunId == run.Id);
        var keyed = await db.EventIndex.AsNoTracking().CountAsync(x => x.RunId == run.Id && x.IdempotencyKey == $"run:{run.Id}:event:run.failed");
        Assert.Equal(3, total);
        Assert.Equal(1, keyed);
    }

    [Fact]
    public async Task RecoveryCoordinator_FailsOrphans_ProtectsAwaiting_AndIsIdempotent()
    {
        var client = _factory!.CreateClient();
        var project = await CreateProjectAsync(client, "recovery-coordinator-workspace");
        var projectId = project.GetProperty("id").GetGuid();
        var session = await CreateSessionAsync(client, projectId, "Recovery session");
        var sessionId = session.GetProperty("id").GetGuid();
        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var coordinator = _factory.Services.GetRequiredService<RecoveryCoordinator>();
        var runs = _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();

        // A run parked on a human decision is protected, not orphaned.
        var awaitingMessage = await (await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "awaiting trigger" })).Content.ReadFromJsonAsync<JsonElement>();
        var awaitingRun = await lifecycle.StartRunAsync(sessionId, awaitingMessage.GetProperty("id").GetGuid());
        await lifecycle.SetRunStatusAsync(awaitingRun.Id, "understanding");
        await lifecycle.SetRunStatusAsync(awaitingRun.Id, "awaiting_user");

        // A fresh non-terminal run is an orphan for the startup pass.
        var orphanMessage = await (await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "orphan trigger" })).Content.ReadFromJsonAsync<JsonElement>();
        var orphanRun = await lifecycle.StartRunAsync(sessionId, orphanMessage.GetProperty("id").GetGuid());

        await coordinator.RunStartupPassesAsync();

        await using (var db = await runs.CreateDbContextAsync())
        {
            var orphan = await db.Runs.AsNoTracking().SingleAsync(x => x.Id == orphanRun.Id);
            Assert.Equal("failed", orphan.Status);
            var protectedRun = await db.Runs.AsNoTracking().SingleAsync(x => x.Id == awaitingRun.Id);
            Assert.Equal("awaiting_user", protectedRun.Status);
            var recoveredEvents = await db.EventIndex.AsNoTracking().CountAsync(x => x.RunId == orphanRun.Id && x.EventType == "run.recovered");
            Assert.Equal(1, recoveredEvents);
        }

        // A second pass must not duplicate the audit event or flip the protected run.
        await coordinator.RunStartupPassesAsync();
        await using (var db = await runs.CreateDbContextAsync())
        {
            var orphan = await db.Runs.AsNoTracking().SingleAsync(x => x.Id == orphanRun.Id);
            Assert.Equal("failed", orphan.Status);
            var protectedRun = await db.Runs.AsNoTracking().SingleAsync(x => x.Id == awaitingRun.Id);
            Assert.Equal("awaiting_user", protectedRun.Status);
            var recoveredEvents = await db.EventIndex.AsNoTracking().CountAsync(x => x.RunId == orphanRun.Id && x.EventType == "run.recovered");
            Assert.Equal(1, recoveredEvents);
        }
    }

    [Fact]
    public async Task PurgeSession_DeletesRelationalRowsAndRunFilesButKeepsContentBlobs()
    {
        var client = _factory!.CreateClient();
        var project = await CreateProjectAsync(client, "purge-session-workspace");
        var session = await CreateSessionAsync(client, project.GetProperty("id").GetGuid(), "Purge session");
        var sessionId = session.GetProperty("id").GetGuid();
        var message = await (await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "Purge trigger" })).Content.ReadFromJsonAsync<JsonElement>();
        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var run = await lifecycle.StartRunAsync(sessionId, message.GetProperty("id").GetGuid());
        await lifecycle.AppendEventAsync(run.Id, "run.started", new { source = "purge-test" }, "Run started");
        await lifecycle.CompleteRunAsync(run.Id);
        var eventFile = Path.Combine(_root, "data", "events", run.Id + ".events.jsonl");
        var taskFile = Path.Combine(_root, "data", "tasks", run.Id + ".tasks.json");
        Assert.True(File.Exists(eventFile));

        // Only trashed sessions can be purged.
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/v1/sessions/{sessionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/sessions/{sessionId}/trash", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/sessions/{sessionId}")).StatusCode);

        await using var memory = await _factory.Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>().CreateDbContextAsync();
        Assert.Equal(0, await memory.Sessions.CountAsync(x => x.Id == sessionId));
        Assert.Equal(0, await memory.Messages.CountAsync(x => x.SessionId == sessionId));
        Assert.Equal(0, await memory.Turns.CountAsync(x => x.SessionId == sessionId));
        Assert.Equal(0, await memory.ContextSnapshots.CountAsync(x => x.SessionId == sessionId));
        Assert.Equal(0, await memory.ContextPatches.CountAsync(x => x.SessionId == sessionId));
        await using var lifecycleDb = await _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync();
        Assert.Equal(0, await lifecycleDb.Runs.CountAsync(x => x.SessionId == sessionId));
        Assert.Equal(0, await lifecycleDb.EventIndex.CountAsync(x => x.SessionId == sessionId));

        Assert.False(File.Exists(eventFile));
        Assert.False(File.Exists(taskFile));
        // Message bodies live in the shared content-addressed store and are never deleted.
        var contentRoot = Path.Combine(_root, "data", "content");
        Assert.NotEmpty(Directory.EnumerateFiles(contentRoot, "*", SearchOption.AllDirectories));

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/sessions/{sessionId}")).StatusCode);
    }

    [Fact]
    public async Task PurgeProject_DeletesEverySessionRegardlessOfLifecycleStatus()
    {
        var client = _factory!.CreateClient();
        var project = await CreateProjectAsync(client, "purge-project-workspace");
        var projectId = project.GetProperty("id").GetGuid();
        var kept = await CreateSessionAsync(client, projectId, "Kept session");
        var doomed = await CreateSessionAsync(client, projectId, "Archived session");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/sessions/{doomed.GetProperty("id").GetGuid()}/archive", null)).StatusCode);
        await client.PostAsJsonAsync($"/api/v1/sessions/{kept.GetProperty("id").GetGuid()}/messages", new { content = "Cascade check" });

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/projects/{projectId}/trash", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/projects/{projectId}")).StatusCode);

        await using var memory = await _factory.Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>().CreateDbContextAsync();
        Assert.Equal(0, await memory.Projects.CountAsync(x => x.Id == projectId));
        Assert.Equal(0, await memory.Sessions.CountAsync(x => x.ProjectId == projectId));
        Assert.Equal(0, await memory.Messages.CountAsync(x => x.SessionId == kept.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Session_CanBeCreatedWithoutProject_AndMigratedToNewProject()
    {
        var client = _factory!.CreateClient();
        // 1. Create session without project
        var response = await client.PostAsJsonAsync("/api/v1/sessions", new { title = "Free conversation" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var freeSession = await response.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = freeSession.GetProperty("id").GetGuid();
        // Core serializes with WhenWritingNull: a projectless session either omits project_id or sends null.
        Assert.True(!freeSession.TryGetProperty("project_id", out var freeProjectId) || freeProjectId.ValueKind == JsonValueKind.Null);

        // 2. Migrate to new project with name and path
        var newWorkspacePath = Path.Combine(_root, "migrated-workspace");
        var migrateResponse = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/migrate", new
        {
            project_name = "Migrated Workspace",
            project_path = newWorkspacePath
        });
        Assert.Equal(HttpStatusCode.OK, migrateResponse.StatusCode);
        var migratedSession = await migrateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.String, migratedSession.GetProperty("project_id").ValueKind);
        var targetProjectId = migratedSession.GetProperty("project_id").GetGuid();
        Assert.True(Directory.Exists(newWorkspacePath));

        // 3. Check memory store
        await using var memory = await _factory.Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>().CreateDbContextAsync();
        var sessionInDb = await memory.Sessions.FindAsync(sessionId);
        Assert.NotNull(sessionInDb);
        Assert.Equal(targetProjectId, sessionInDb.ProjectId);
    }

    private async Task<JsonElement> CreateProjectAsync(HttpClient client, string workspaceName)
    {
        var response = await client.PostAsJsonAsync("/api/v1/projects", new { name = workspaceName, path = Path.Combine(_root, workspaceName) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> CreateSessionAsync(HttpClient client, Guid projectId, string title)
    {
        var response = await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = projectId, title });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private sealed class LifecycleFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public LifecycleFactory(string root) => _root = root;

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
}
