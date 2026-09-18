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
    public async Task RunStatusMachine_ConcurrentTerminalClaims_KeepTheDatabaseWinner()
    {
        var client = _factory!.CreateClient();
        var project = await CreateProjectAsync(client, "terminal-cas-workspace");
        var session = await CreateSessionAsync(client, project.GetProperty("id").GetGuid(), "Terminal CAS session");
        var sessionId = session.GetProperty("id").GetGuid();
        var message = await (await client.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/messages",
            new { content = "Terminal CAS trigger" })).Content.ReadFromJsonAsync<JsonElement>();
        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var runs = _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();
        var run = await lifecycle.StartRunAsync(sessionId, message.GetProperty("id").GetGuid());
        await lifecycle.SetRunStatusAsync(run.Id, "executing");

        static async Task<(string Target, bool Succeeded, Exception? Error)> ClaimAsync(
            string target,
            Func<Task> claim)
        {
            try
            {
                await claim();
                return (target, true, null);
            }
            catch (Exception ex)
            {
                return (target, false, ex);
            }
        }

        var claims = await Task.WhenAll(
            ClaimAsync("failed", () => lifecycle.SetRunStatusAsync(run.Id, "failed", "failure won")),
            ClaimAsync("cancelled", () => lifecycle.SetRunStatusAsync(run.Id, "cancelled", "cancel won")));

        var winner = Assert.Single(claims, item => item.Succeeded);
        var loser = Assert.Single(claims, item => !item.Succeeded);
        Assert.IsType<InvalidOperationException>(loser.Error);

        await using var db = await runs.CreateDbContextAsync();
        var stored = await db.Runs.AsNoTracking().SingleAsync(x => x.Id == run.Id);
        Assert.Equal(winner.Target, stored.Status);
        Assert.NotNull(stored.CompletedAt);
        Assert.Equal(winner.Target == "failed" ? "failure won" : "cancel won", stored.Summary);
    }

    [Fact]
    public async Task RunCompletionClaim_AtomicallyFencesCancellation()
    {
        var client = _factory!.CreateClient();
        var project = await CreateProjectAsync(client, "completion-claim-workspace");
        var session = await CreateSessionAsync(client, project.GetProperty("id").GetGuid(), "Completion claim session");
        var sessionId = session.GetProperty("id").GetGuid();
        var message = await (await client.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/messages",
            new { content = "Completion claim trigger" })).Content.ReadFromJsonAsync<JsonElement>();
        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var runs = _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();
        var run = await lifecycle.StartRunAsync(sessionId, message.GetProperty("id").GetGuid());
        await lifecycle.SetRunStatusAsync(run.Id, "executing");

        async Task<(bool Succeeded, Exception? Error)> CancelAsync()
        {
            try
            {
                await lifecycle.SetRunStatusAsync(run.Id, "cancelled", "cancel attempted");
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex);
            }
        }

        var completionTask = lifecycle.TryClaimRunCompletionAsync(run.Id, "executing", null, null);
        var cancelTask = CancelAsync();
        await Task.WhenAll(completionTask, cancelTask);
        var completionWon = await completionTask;
        var cancel = await cancelTask;
        Assert.NotEqual(completionWon, cancel.Succeeded);

        if (completionWon)
        {
            Assert.IsType<InvalidOperationException>(cancel.Error);
            await lifecycle.CompleteRunAsync(run.Id);
        }

        await using var db = await runs.CreateDbContextAsync();
        var stored = await db.Runs.AsNoTracking().SingleAsync(x => x.Id == run.Id);
        Assert.Equal(completionWon ? "completed" : "cancelled", stored.Status);
        Assert.NotNull(stored.CompletedAt);
    }

    [Fact]
    public async Task RetryCheckpointPrecondition_RejectsACommittedCancellation()
    {
        var client = _factory!.CreateClient();
        var project = await CreateProjectAsync(client, "retry-precondition-workspace");
        var session = await CreateSessionAsync(client, project.GetProperty("id").GetGuid(), "Retry precondition session");
        var sessionId = session.GetProperty("id").GetGuid();
        var message = await (await client.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/messages",
            new { content = "Retry precondition trigger" })).Content.ReadFromJsonAsync<JsonElement>();
        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var run = await lifecycle.StartRunAsync(sessionId, message.GetProperty("id").GetGuid());
        await lifecycle.SetRunStatusAsync(run.Id, "executing");
        await lifecycle.SetRunStatusAsync(run.Id, "cancelled", "user cancelled");

        var error = await Assert.ThrowsAsync<RunCheckpointConflictException>(() =>
            lifecycle.SaveRunCheckpointAsync(run.Id, new RunCheckpointWrite(
                0,
                "executing",
                "{\"retry\":1}",
                IdempotencyKey: $"run:{run.Id}:retry-precondition",
                ExpectedRunStatus: "executing",
                RequireCompletionUnclaimed: true)));
        Assert.Equal(0, error.ExpectedRevision);
        Assert.Equal(0, error.ActualRevision);
        Assert.Null(await lifecycle.GetCurrentRunCheckpointAsync(run.Id));
    }

    [Fact]
    public async Task RecoveryCoordinator_RepairsCancelledRunWithoutTerminalStream()
    {
        var client = _factory!.CreateClient();
        var project = await CreateProjectAsync(client, "cancel-repair-workspace");
        var session = await CreateSessionAsync(client, project.GetProperty("id").GetGuid(), "Cancel repair session");
        var sessionId = session.GetProperty("id").GetGuid();
        var conversations = _factory.Services.GetRequiredService<IConversationStore>();
        var user = await conversations.AppendMessageAsync(sessionId, "user", "cancel repair");
        var revision = await conversations.GetContextRevisionAsync(sessionId);
        var turn = await conversations.CreateTurnAsync(sessionId, user.Id, "new_task", revision);
        var lifecycle = _factory.Services.GetRequiredService<ILifecycleManager>();
        var started = await lifecycle.StartOrGetRunAsync(new RunStartRequest(
            sessionId.ToString(),
            user.Id.ToString(),
            turn.Id.ToString(),
            revision));
        var runId = Guid.Parse(started.RunId);
        await conversations.AttachRunAsync(turn.Id, runId);
        await lifecycle.SetRunStatusAsync(started.RunId, "cancelled", "simulated host crash after status commit");
        Assert.Empty(await lifecycle.ReplayRunStreamAsync(started.RunId, turn.Id, 0));

        var coordinator = _factory.Services.GetRequiredService<RecoveryCoordinator>();
        await coordinator.RunStartupPassesAsync();

        var stream = await lifecycle.ReplayRunStreamAsync(started.RunId, turn.Id, 0);
        var done = Assert.Single(stream, item => item.Kind == "done");
        Assert.Equal("cancelled", done.FinishReason);
        var repairedTurn = await conversations.FindTurnByUserMessageAsync(user.Id);
        Assert.NotNull(repairedTurn);
        Assert.Equal("cancelled", repairedTurn.Status);
    }

    [Fact]
    public async Task RecoveryCoordinator_RepairsFailedRunWithItsOriginalErrorCategory()
    {
        var client = _factory!.CreateClient();
        var project = await CreateProjectAsync(client, "failed-repair-workspace");
        var session = await CreateSessionAsync(client, project.GetProperty("id").GetGuid(), "Failed repair session");
        var sessionId = session.GetProperty("id").GetGuid();
        var conversations = _factory.Services.GetRequiredService<IConversationStore>();
        var user = await conversations.AppendMessageAsync(sessionId, "user", "failed repair");
        var revision = await conversations.GetContextRevisionAsync(sessionId);
        var turn = await conversations.CreateTurnAsync(sessionId, user.Id, "new_task", revision);
        var lifecycle = _factory.Services.GetRequiredService<ILifecycleManager>();
        var storage = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var started = await lifecycle.StartOrGetRunAsync(new RunStartRequest(
            sessionId.ToString(),
            user.Id.ToString(),
            turn.Id.ToString(),
            revision));
        var runId = Guid.Parse(started.RunId);
        await conversations.AttachRunAsync(turn.Id, runId);
        var lease = await storage.TryAcquireRunLeaseAsync(runId, "failed-repair-owner", TimeSpan.FromMinutes(1));
        Assert.True(lease.Acquired);
        await storage.SetRunFailedUnderLeaseAsync(
            runId,
            "The model provider remained unavailable.",
            "model",
            lease.OwnerId,
            lease.RecoveryCount);
        Assert.Empty(await lifecycle.ReplayRunStreamAsync(started.RunId, turn.Id, 0));

        var coordinator = _factory.Services.GetRequiredService<RecoveryCoordinator>();
        await coordinator.RunStartupPassesAsync();

        var stream = await lifecycle.ReplayRunStreamAsync(started.RunId, turn.Id, 0);
        var error = Assert.Single(stream, item => item.Kind == "error");
        Assert.Equal("model", error.ErrorCategory);
        Assert.Equal("The model provider remained unavailable.", error.SafeErrorMessage);
        var repaired = await lifecycle.GetRunStateAsync(started.RunId);
        Assert.Equal("model", repaired.TerminalErrorCategory);
        var repairedTurn = await conversations.FindTurnByUserMessageAsync(user.Id);
        Assert.NotNull(repairedTurn);
        Assert.Equal("failed", repairedTurn.Status);
    }

    [Fact]
    public async Task LeaseEpochPreconditions_RejectThePreviousOwnerAfterRecovery()
    {
        var client = _factory!.CreateClient();
        var project = await CreateProjectAsync(client, "lease-epoch-workspace");
        var session = await CreateSessionAsync(client, project.GetProperty("id").GetGuid(), "Lease epoch session");
        var sessionId = session.GetProperty("id").GetGuid();
        var message = await (await client.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/messages",
            new { content = "Lease epoch trigger" })).Content.ReadFromJsonAsync<JsonElement>();
        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var run = await lifecycle.StartRunAsync(sessionId, message.GetProperty("id").GetGuid());
        await lifecycle.SetRunStatusAsync(run.Id, "executing");
        var first = await lifecycle.TryAcquireRunLeaseAsync(run.Id, "owner-a", TimeSpan.FromMinutes(1));
        Assert.True(first.Acquired);
        await lifecycle.ReleaseRunLeaseAsync(run.Id, first.OwnerId);
        var second = await lifecycle.TryAcquireRunLeaseAsync(run.Id, "owner-b", TimeSpan.FromMinutes(1));
        Assert.True(second.Acquired);
        Assert.True(second.RecoveryCount > first.RecoveryCount);

        var checkpointConflict = await Assert.ThrowsAsync<RunCheckpointConflictException>(() =>
            lifecycle.SaveRunCheckpointAsync(run.Id, new RunCheckpointWrite(
                0,
                "executing",
                "{\"owner\":\"a\"}",
                IdempotencyKey: $"run:{run.Id}:stale-owner",
                ExpectedRunStatus: "executing",
                RequireCompletionUnclaimed: true,
                ExpectedLeaseOwner: first.OwnerId,
                ExpectedRecoveryCount: first.RecoveryCount)));
        Assert.Equal(0, checkpointConflict.ActualRevision);
        Assert.False(await lifecycle.TryClaimRunCompletionAsync(
            run.Id,
            "executing",
            first.OwnerId,
            first.RecoveryCount));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.SetRunStatusUnderLeaseAsync(
            run.Id,
            "reviewing",
            null,
            first.OwnerId,
            first.RecoveryCount));

        var checkpoint = await lifecycle.SaveRunCheckpointAsync(run.Id, new RunCheckpointWrite(
            0,
            "executing",
            "{\"owner\":\"b\"}",
            IdempotencyKey: $"run:{run.Id}:current-owner",
            ExpectedRunStatus: "executing",
            RequireCompletionUnclaimed: true,
            ExpectedLeaseOwner: second.OwnerId,
            ExpectedRecoveryCount: second.RecoveryCount));
        Assert.Equal(1, checkpoint.Revision);
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
