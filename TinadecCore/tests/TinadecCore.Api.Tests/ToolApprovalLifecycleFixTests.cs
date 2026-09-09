using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Lifecycle;
using TinadecCore.Tools;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Stale-running recovery (B1): a "running" execution row the live process cannot
/// hold is a host-crash remnant. With the stale-reset signal an approval-gated row
/// converts to outcome_unknown (its approval was provably consumed atomically with
/// the running mark) and a read-only row is re-driven — instead of both parking
/// forever on the misleading already_running answer. Also covers the approval
/// expiry sweep (B7) and the fail-closed risk vocabulary (B8).
/// </summary>
public sealed class ToolApprovalLifecycleFixTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Parameters = "{\"path\":\"probe.txt\",\"content\":\"x\"}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-lifecycle-fix-tests", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new Factory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 10 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, true);
                break;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                Thread.Sleep(250);
            }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task StaleRunningApprovalGatedExecution_BecomesOutcomeUnknown_WithResetFlag()
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("stale-mutating");
        var run = await InsertRunAsync(sessionId, "executing");
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, "stale:m:0:0");
        await MarkRunningWithConsumedApprovalAsync(execution);

        var start = await ExecutionCoordinator().TryStartAsync(execution.Id, allowStaleRunningReset: true);

        Assert.Equal("outcome_unknown", start.Status);
        await using var verify = await DbFactory().CreateDbContextAsync();
        var row = await verify.ToolExecutions.AsNoTracking().SingleAsync(x => x.Id == execution.Id);
        Assert.Equal("outcome_unknown", row.Status);
        Assert.Equal("approval_consumed_without_outcome", row.ErrorCategory);
    }

    [Fact]
    public async Task StaleRunningApprovalGatedExecution_KeepsAlreadyRunningAnswer_WithoutResetFlag()
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("held-mutating");
        var run = await InsertRunAsync(sessionId, "executing");
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, "stale:h:0:0");
        await MarkRunningWithConsumedApprovalAsync(execution);

        // The default flag preserves the legacy answer for a row this process
        // may genuinely hold; it must not convert anything.
        var start = await ExecutionCoordinator().TryStartAsync(execution.Id);

        Assert.Equal("already_running", start.Status);
        await using var verify = await DbFactory().CreateDbContextAsync();
        var row = await verify.ToolExecutions.AsNoTracking().SingleAsync(x => x.Id == execution.Id);
        Assert.Equal("running", row.Status);
    }

    [Fact]
    public async Task StaleRunningReadOnlyExecution_IsResetAndDrivenAgain()
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("stale-readonly");
        var run = await InsertRunAsync(sessionId, "executing");
        var execution = await PrepareExecutionAsync(
            projectId, sessionId, run.Id, "stale:r:0:0", toolId: "read_file", risk: "low",
            mutatesWorkspace: false, requiresApproval: false);
        Assert.Null(execution.ApprovalId);
        await using (var db = await DbFactory().CreateDbContextAsync())
        {
            var staleRow = await db.ToolExecutions.SingleAsync(x => x.Id == execution.Id);
            staleRow.Status = "running";
            await db.SaveChangesAsync();
        }

        var start = await ExecutionCoordinator().TryStartAsync(execution.Id, allowStaleRunningReset: true);

        Assert.Equal("running", start.Status);
        await using var verify = await DbFactory().CreateDbContextAsync();
        var row = await verify.ToolExecutions.AsNoTracking().SingleAsync(x => x.Id == execution.Id);
        Assert.Equal("running", row.Status);
        Assert.Null(row.ErrorCategory);
    }

    [Fact]
    public async Task ExpirySweep_WakesLiveRun_ExpiresUndrivableApproval_LeavesFreshApproval()
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("sweep");
        var parkedRun = await InsertRunAsync(sessionId, "awaiting_approval");
        var parkedExecution = await PrepareExecutionAsync(projectId, sessionId, parkedRun.Id, "sweep:live:0:0");
        var terminalRun = await InsertRunAsync(sessionId, "completed");
        var orphanedExecution = await PrepareExecutionAsync(projectId, sessionId, terminalRun.Id, "sweep:dead:0:0");
        var freshRun = await InsertRunAsync(sessionId, "awaiting_approval");
        var freshExecution = await PrepareExecutionAsync(projectId, sessionId, freshRun.Id, "sweep:fresh:0:0");
        await using (var db = await DbFactory().CreateDbContextAsync())
        {
            foreach (var approvalId in new[] { parkedExecution.ApprovalId, orphanedExecution.ApprovalId })
            {
                var approval = await db.ApprovalRequests.SingleAsync(x => x.Id == approvalId);
                approval.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            }
            await db.SaveChangesAsync();
        }

        var sweep = await Approvals().SweepExpiredPendingApprovalsAsync();

        // The live run is returned for waking, and its approval is deliberately
        // NOT marked here: the wake re-enters TryStartAsync, which owns the park
        // transition (marking it now would turn the resume into a hard failure).
        Assert.Contains(parkedRun.Id, sweep.RunIdsToWake);
        Assert.DoesNotContain(terminalRun.Id, sweep.RunIdsToWake);
        Assert.Equal(1, sweep.OrphanedExpiryCount);
        await using (var verify = await DbFactory().CreateDbContextAsync())
        {
            var parkedApproval = await verify.ApprovalRequests.AsNoTracking().SingleAsync(x => x.Id == parkedExecution.ApprovalId);
            Assert.Equal("pending", parkedApproval.Status);
            var orphanedApproval = await verify.ApprovalRequests.AsNoTracking().SingleAsync(x => x.Id == orphanedExecution.ApprovalId);
            Assert.Equal("expired", orphanedApproval.Status);
            var freshApproval = await verify.ApprovalRequests.AsNoTracking().SingleAsync(x => x.Id == freshExecution.ApprovalId);
            Assert.Equal("pending", freshApproval.Status);
        }

        // The woken run then parks through the existing TryStartAsync path,
        // which is what escalates the lane to awaiting_user.
        var start = await ExecutionCoordinator().TryStartAsync(parkedExecution.Id);
        Assert.Equal("awaiting_approval", start.Status);
        Assert.True(start.ParkExpired);
        await using var final = await DbFactory().CreateDbContextAsync();
        var expiredPark = await final.ApprovalRequests.AsNoTracking().SingleAsync(x => x.Id == parkedExecution.ApprovalId);
        Assert.Equal("expired", expiredPark.Status);
    }

    [Fact]
    public async Task ExpirySweepCoordinator_ReturnsSweepWithoutEngine()
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("sweep-coordinator");
        var run = await InsertRunAsync(sessionId, "awaiting_approval");
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, "sweep:coord:0:0");
        await using (var db = await DbFactory().CreateDbContextAsync())
        {
            var approval = await db.ApprovalRequests.SingleAsync(x => x.Id == execution.ApprovalId);
            approval.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var coordinator = _factory!.Services.GetRequiredService<TinadecCore.Runtime.RecoveryCoordinator>();
        var sweep = await coordinator.SweepApprovalExpiryAsync();

        Assert.Contains(run.Id, sweep.RunIdsToWake);
    }

    [Fact]
    public async Task UnknownRisk_FailsClosed_InsteadOfCoercingToMedium()
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("risk-closed");
        var run = await InsertRunAsync(sessionId, "executing");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            PrepareExecutionAsync(projectId, sessionId, run.Id, "risk:bogus:0:0", risk: "bogus"));

        var elevated = await PrepareExecutionAsync(projectId, sessionId, run.Id, "risk:elevated:0:0", risk: "elevated");
        Assert.Equal("elevated", elevated.Risk);
    }

    private async Task MarkRunningWithConsumedApprovalAsync(ToolExecutionSnapshot execution)
    {
        await using var db = await DbFactory().CreateDbContextAsync();
        var approval = await db.ApprovalRequests.SingleAsync(x => x.Id == execution.ApprovalId);
        approval.Status = "consumed";
        approval.Decision = "approved";
        approval.ConsumedByExecutionId = execution.Id;
        approval.ConsumedAt = DateTimeOffset.UtcNow;
        var row = await db.ToolExecutions.SingleAsync(x => x.Id == execution.Id);
        row.Status = "running";
        await db.SaveChangesAsync();
    }

    private async Task<(Guid ProjectId, Guid SessionId)> CreateProjectAndSessionAsync(string name)
    {
        var client = _factory!.CreateClient();
        var projectPath = Path.Combine(_root, $"{name}-workspace");
        Directory.CreateDirectory(projectPath);
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new { name, path = projectPath }, Json);
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("id").GetGuid();
        var sessionResponse = await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = projectId, title = name }, Json);
        Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
        var session = await sessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        return (projectId, session.GetProperty("id").GetGuid());
    }

    private async Task<RunRecord> InsertRunAsync(Guid sessionId, string status)
    {
        var scope = TenantAccessor().Current;
        await using var db = await DbFactory().CreateDbContextAsync();
        var run = new RunRecord
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            SessionId = sessionId,
            InitiatedByPrincipalId = Guid.NewGuid(),
            TriggerMessageId = Guid.NewGuid(),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    private async Task<ToolExecutionSnapshot> PrepareExecutionAsync(
        Guid projectId,
        Guid sessionId,
        Guid runId,
        string toolCallKey,
        string toolId = "write_file",
        string risk = "high",
        bool mutatesWorkspace = true,
        bool requiresApproval = true)
    {
        var scope = TenantAccessor().Current;
        var request = new ToolExecutionPrepareRequest(
            scope.TenantId,
            scope.WorkspaceId,
            projectId,
            sessionId,
            runId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            toolId,
            risk,
            mutatesWorkspace,
            requiresApproval,
            Parameters,
            ToolParametersHash.Compute(Parameters),
            toolCallKey,
            "stale running test");
        var preparation = await ExecutionCoordinator().PrepareAsync(request);
        return preparation.Execution;
    }

    private IDbContextFactory<LifecycleDbContext> DbFactory() =>
        _factory!.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();

    private IToolExecutionCoordinator ExecutionCoordinator() =>
        _factory!.Services.GetRequiredService<IToolExecutionCoordinator>();

    private ToolApprovalCoordinator Approvals() =>
        _factory!.Services.GetRequiredService<ToolApprovalCoordinator>();

    private ITenantContextAccessor TenantAccessor() =>
        _factory!.Services.GetRequiredService<ITenantContextAccessor>();

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public Factory(string root) => _root = root;
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
            ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
            ["Logging:LogLevel:Default"] = "Warning"
        }));
    }
}
