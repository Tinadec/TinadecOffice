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
using TinadecCore.Persistence;
using TinadecCore.Tools;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Decision window vs execution window: a lapsed decision window means the
/// human never answered, so the execution parks (awaiting_user) and the lane
/// escalates; a lapsed execution window means an approved grant went stale,
/// which must still fail closed before any tool runs.
/// </summary>
public sealed class ApprovalWindowTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Parameters = "{\"path\":\"window.txt\",\"content\":\"x\"}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-window-tests", Guid.NewGuid().ToString("N"));
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
        if (Directory.Exists(_root))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
                    else if (Directory.Exists(path)) new DirectoryInfo(path).Attributes = FileAttributes.Normal;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
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
    public async Task DecisionWindowExpiry_ParksExecutionInsteadOfFailing()
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("window-park");
        var run = await InsertRunAsync(sessionId);
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, "park:0:0:0");

        await using (var db = await DbFactory().CreateDbContextAsync())
        {
            var approval = await db.ApprovalRequests.SingleAsync(x => x.Id == execution.ApprovalId);
            approval.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var start = await ExecutionCoordinator().TryStartAsync(execution.Id);

        Assert.Equal("awaiting_approval", start.Status);
        await using var verify = await DbFactory().CreateDbContextAsync();
        var parkedApproval = await verify.ApprovalRequests.AsNoTracking().SingleAsync(x => x.Id == execution.ApprovalId);
        Assert.Equal("expired", parkedApproval.Status);
        var parkedExecution = await verify.ToolExecutions.AsNoTracking().SingleAsync(x => x.Id == execution.Id);
        Assert.Equal("awaiting_approval", parkedExecution.Status);

        var events = await LifecycleManager().ReplayEventsAsync(sessionId, 0);
        Assert.Contains(events, e => e.EventType == "approval.park_expired");
    }

    [Fact]
    public async Task ExecutionWindowExpiry_StillFailsClosed()
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("window-exec");
        var run = await InsertRunAsync(sessionId);
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, "exec:0:0:0");

        Assert.NotNull(execution.ApprovalId);
        await ApproveWithBackdatedExecutionWindowAsync(execution.ApprovalId!.Value,
            DateTimeOffset.UtcNow.AddMinutes(-1));

        var start = await ExecutionCoordinator().TryStartAsync(execution.Id);

        Assert.Equal("not_approved", start.Status);
        await using var verify = await DbFactory().CreateDbContextAsync();
        var failed = await verify.ToolExecutions.AsNoTracking().SingleAsync(x => x.Id == execution.Id);
        Assert.Equal("failed", failed.Status);
        Assert.Equal("approval_expired", failed.ErrorCategory);
        var approval = await verify.ApprovalRequests.AsNoTracking().SingleAsync(x => x.Id == execution.ApprovalId);
        Assert.Equal("expired", approval.Status);
    }

    [Fact]
    public async Task ExecutionWindowExpiry_LegacyRowFallsBackToDecisionWindow()
    {
        var (projectId, sessionId) = await CreateProjectAndSessionAsync("window-legacy");
        var run = await InsertRunAsync(sessionId);
        var execution = await PrepareExecutionAsync(projectId, sessionId, run.Id, "legacy:0:0:0");

        await using (var db = await DbFactory().CreateDbContextAsync())
        {
            var approval = await db.ApprovalRequests.SingleAsync(x => x.Id == execution.ApprovalId);
            approval.Status = "approved";
            approval.Decision = "approved";
            approval.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            approval.ExecutionWindowExpiresAt = null;
            await db.SaveChangesAsync();
        }

        var start = await ExecutionCoordinator().TryStartAsync(execution.Id);

        Assert.Equal("not_approved", start.Status);
        await using var verify = await DbFactory().CreateDbContextAsync();
        var failed = await verify.ToolExecutions.AsNoTracking().SingleAsync(x => x.Id == execution.Id);
        Assert.Equal("failed", failed.Status);
        Assert.Equal("approval_expired", failed.ErrorCategory);
    }

    [Fact]
    public async Task AwaitingUserRun_DoesNotCountAgainstActiveRunLimit()
    {
        var (_, sessionId) = await CreateProjectAndSessionAsync("window-admission");
        await InsertRunAsync(sessionId);
        await InsertRunAsync(sessionId, status: "awaiting_user");
        await InsertRunAsync(sessionId, status: "awaiting_user");

        var active = await LifecycleManager().CountActiveRunsAsync(sessionId.ToString());

        Assert.Equal(1, active);
    }

    private async Task ApproveWithBackdatedExecutionWindowAsync(Guid approvalId, DateTimeOffset executionWindow)
    {
        await using var db = await DbFactory().CreateDbContextAsync();
        var approval = await db.ApprovalRequests.SingleAsync(x => x.Id == approvalId);
        approval.Status = "approved";
        approval.Decision = "approved";
        approval.ExecutionWindowExpiresAt = executionWindow;
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

    private async Task<RunRecord> InsertRunAsync(Guid sessionId, string status = "planning")
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
        string risk = "high")
    {
        var coordinator = ExecutionCoordinator();
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
            MutatesWorkspace: true,
            RequiresApproval: true,
            Parameters,
            ToolParametersHash.Compute(Parameters),
            toolCallKey,
            "approval window test");
        var preparation = await coordinator.PrepareAsync(request);
        return preparation.Execution;
    }

    private IDbContextFactory<LifecycleDbContext> DbFactory() =>
        _factory!.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();

    private IToolExecutionCoordinator ExecutionCoordinator() =>
        _factory!.Services.GetRequiredService<IToolExecutionCoordinator>();

    private ILifecycleManager LifecycleManager() =>
        _factory!.Services.GetRequiredService<ILifecycleManager>();

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
