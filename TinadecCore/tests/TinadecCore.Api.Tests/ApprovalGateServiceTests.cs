using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentGraph;
using TinadecCore.Governance;
using TinadecCore.Lifecycle;
using TinadecCore.Runtime;

namespace TinadecCore.Api.Tests;

/// <summary>Real SQLite query coverage without a background host, model, or tool process.</summary>
public sealed class ApprovalGateServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _lifecycle = new("Data Source=:memory:");
    private readonly SqliteConnection _graph = new("Data Source=:memory:");
    private readonly TenantAccessor _tenant = new();
    private readonly RecordingJudge _judge = new();
    private ServiceProvider _services = null!;
    private Factory<LifecycleDbContext> _lifecycleFactory = null!;
    private Factory<AgentGraphDbContext> _graphFactory = null!;
    private ApprovalGateService _gates = null!;

    public async Task InitializeAsync()
    {
        await _lifecycle.OpenAsync();
        await _graph.OpenAsync();
        var lifecycleOptions = new DbContextOptionsBuilder<LifecycleDbContext>().UseSqlite(_lifecycle).Options;
        var graphOptions = new DbContextOptionsBuilder<AgentGraphDbContext>().UseSqlite(_graph).Options;
        _lifecycleFactory = new(() => new(lifecycleOptions));
        _graphFactory = new(() => new(graphOptions));
        await using var lifecycle = _lifecycleFactory.CreateDbContext();
        await using var graph = _graphFactory.CreateDbContext();
        await lifecycle.Database.EnsureCreatedAsync();
        await graph.Database.EnsureCreatedAsync();
        _services = new ServiceCollection().AddSingleton<IApprovalGateJudge>(_judge)
            .AddSingleton<IApprovalRules, AlwaysOptedIn>().BuildServiceProvider();
        _gates = new(_services, _lifecycleFactory, _graphFactory, _tenant,
            NullLogger<ApprovalGateService>.Instance,
            Options.Create(new ApprovalGateOptions { BatchSize = 1, Parallelism = 1 }));
    }

    public async Task DisposeAsync()
    {
        _gates.Dispose();
        await _services.DisposeAsync();
        await _lifecycle.DisposeAsync();
        await _graph.DisposeAsync();
    }

    [Fact]
    public async Task EmptyDatabase_CanExecuteTheCandidateQuery()
    {
        Assert.Equal(0, await _gates.RunPassAsync());
        Assert.Empty(_judge.Approvals);
    }

    [Fact]
    public async Task EscalatedFirstPage_DoesNotStarveTheNextCandidate()
    {
        var ids = await SeedAsync(513, "write_file", "low");
        await using var graph = _graphFactory.CreateDbContext();
        foreach (var id in ids.Take(512))
            graph.ApprovalGates.Add(new ApprovalGateRecord
            {
                Id = Guid.NewGuid(), TenantId = _tenant.Current.TenantId, WorkspaceId = _tenant.Current.WorkspaceId,
                PermissionRequestId = id, GateKind = ApprovalGateKinds.ReviewerAgent,
                Status = ApprovalGateStatuses.Escalated, Revision = 1,
            });
        await graph.SaveChangesAsync();

        Assert.Equal(1, await _gates.RunPassAsync());
        Assert.Equal(ids[^1], Assert.Single(_judge.Approvals));
        Assert.Equal(0, await _gates.RunPassAsync());
    }

    [Theory]
    [InlineData("git_push", "low")]
    [InlineData("mcp_invoke", "low")]
    [InlineData("create_workspace", "low")]
    [InlineData("delete_file", "low")]
    [InlineData("shell", "high")]
    [InlineData("command_run", "elevated")]
    [InlineData("write_file", "critical")]
    [InlineData("write_file", "unknown")]
    public async Task OptInNeverAsksAModelToDecideAHumanOnlyCall(string tool, string risk)
    {
        var id = Assert.Single(await SeedAsync(1, tool, risk));
        Assert.Equal(1, await _gates.RunPassAsync());
        Assert.Empty(_judge.Approvals);
        var gates = await _gates.GetAsync(id);
        Assert.Equal("escalated", gates!.Status);
        await using var db = _lifecycleFactory.CreateDbContext();
        Assert.Equal("pending", (await db.ApprovalRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task GateTransitions_EmitApprovalGateUpdatedEvents_WithSnakeCasePayload()
    {
        var events = new List<(string EventType, string PayloadJson)>();
        var approvals = new FakeApprovalCoordinator(_lifecycleFactory, _tenant);
        var lifecycleManager = RecordingLifecycleProxy.Create(events);
        var approvingJudge = new VerdictJudge("approve", "Safe low-risk write.");

        await using var customServices = new ServiceCollection()
            .AddSingleton<IApprovalGateJudge>(approvingJudge)
            .AddSingleton<IApprovalRules, AlwaysOptedIn>()
            .AddSingleton<IToolApprovalCoordinator>(approvals)
            .AddSingleton<ILifecycleManager>(lifecycleManager)
            .BuildServiceProvider();
        using var service = new ApprovalGateService(
            customServices,
            _lifecycleFactory,
            _graphFactory,
            _tenant,
            NullLogger<ApprovalGateService>.Instance,
            Options.Create(new ApprovalGateOptions { BatchSize = 1, Parallelism = 1 }));

        var id = Assert.Single(await SeedAsync(1, "write_file", "low"));
        Assert.Equal(1, await service.RunPassAsync());

        var gateUpdates = events.Where(e => e.EventType == "approval.gate_updated").ToList();
        Assert.Equal(2, gateUpdates.Count);

        using var firstDoc = System.Text.Json.JsonDocument.Parse(gateUpdates[0].PayloadJson);
        Assert.Equal(id, firstDoc.RootElement.GetProperty("approval_id").GetGuid());
        Assert.NotEqual(Guid.Empty, firstDoc.RootElement.GetProperty("session_id").GetGuid());
        Assert.Equal("evaluating", firstDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal("evaluating", firstDoc.RootElement.GetProperty("gates")[0].GetProperty("status").GetString());

        using var secondDoc = System.Text.Json.JsonDocument.Parse(gateUpdates[1].PayloadJson);
        Assert.Equal("approved", secondDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal("approved", secondDoc.RootElement.GetProperty("gates")[0].GetProperty("status").GetString());
        Assert.Equal("reviewer_agent", secondDoc.RootElement.GetProperty("gates")[0].GetProperty("gate_kind").GetString());
    }

    private async Task<Guid[]> SeedAsync(int count, string tool, string risk)
    {
        await using var db = _lifecycleFactory.CreateDbContext();
        var scope = _tenant.Current;
        var run = new RunRecord { Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            SessionId = Guid.NewGuid(), PermissionMode = "delegate-reviewer", Status = "awaiting_approval" };
        db.Runs.Add(run);
        // Lexical and Guid ordering agree for these ids, on every supported database.
        var ids = Enumerable.Range(1, count).Select(i => Guid.Parse($"{i:x8}-0000-0000-0000-000000000000")).ToArray();
        foreach (var id in ids)
        {
            var executionId = Guid.NewGuid();
            db.ApprovalRequests.Add(new ApprovalRequestRecord { Id = id, TenantId = scope.TenantId,
                WorkspaceId = scope.WorkspaceId, SessionId = run.SessionId, RunId = run.Id,
                ExecutionId = executionId, ToolId = tool, Risk = risk, Kind = "tool", Status = "pending" });
            db.ToolExecutions.Add(new ToolExecutionRecord { Id = executionId, TenantId = scope.TenantId,
                WorkspaceId = scope.WorkspaceId, SessionId = run.SessionId, RunId = run.Id,
                ToolCallKey = id.ToString(), ApprovalId = id, ToolId = tool, Risk = risk });
        }
        await db.SaveChangesAsync();
        return ids;
    }

    private sealed class Factory<T>(Func<T> create) : IDbContextFactory<T> where T : DbContext
    {
        public T CreateDbContext() => create();
    }

    private sealed class TenantAccessor : ITenantContextAccessor
    {
        public TenantContext Current { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "owner");
    }

    private sealed class RecordingJudge : IApprovalGateJudge
    {
        public List<Guid> Approvals { get; } = [];
        public Task<ApprovalGateVerdict> JudgeAsync(ApprovalGateRequest request, CancellationToken ct = default)
        {
            Approvals.Add(request.ApprovalId);
            return Task.FromResult(new ApprovalGateVerdict("escalate", "Test handoff to the person.", "{}", null));
        }
    }

    private sealed class AlwaysOptedIn : IApprovalRules
    {
        public Task<bool> IsDelegatedToolAsync(Guid runId, string toolId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<ApprovalRuleView>> ListAsync(Guid? sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApprovalRuleView> CreateAsync(ApprovalRuleCreate rule, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RevokeAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApprovalRuleView?> MatchCommandAsync(Guid sessionId, string tool, string command, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> VerifyCommandRuleAsync(Guid id, Guid runId, string tool, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApprovalRuleView> RecordUseAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class VerdictJudge(string decision, string rationale) : IApprovalGateJudge
    {
        public Task<ApprovalGateVerdict> JudgeAsync(ApprovalGateRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ApprovalGateVerdict(decision, rationale, "{}", "reviewer"));
    }

    private sealed class FakeApprovalCoordinator(IDbContextFactory<LifecycleDbContext> factory, ITenantContextAccessor tenant) : IToolApprovalCoordinator
    {
        public Task<Guid> CreateToolApprovalAsync(string sessionId, string runId, string taskId, string toolId, string parametersHash, string summary, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> TryConsumeApprovalAsync(Guid approvalId, string executionId, string expectedRequestHash, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public async Task<ToolApprovalDecision> DecideAsync(Guid approvalId, string decision, string? reason, CancellationToken ct = default)
        {
            var scope = tenant.Current;
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.ApprovalRequests.SingleAsync(x => x.Id == approvalId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, ct);
            row.Status = decision;
            row.Decision = decision;
            row.DecisionReason = reason;
            row.DecidedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return new ToolApprovalDecision(row.Id, decision, row.RunId, row.TaskId, row.ExecutionId, row.DecidedAt.Value);
        }
    }

    public class RecordingLifecycleProxy : System.Reflection.DispatchProxy
    {
        private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);
        public List<(string EventType, string PayloadJson)> Events { get; set; } = [];

        public static ILifecycleManager Create(List<(string EventType, string PayloadJson)> events)
        {
            var proxy = Create<ILifecycleManager, RecordingLifecycleProxy>();
            ((RecordingLifecycleProxy)(object)proxy).Events = events;
            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ILifecycleManager.AppendEventAsync) && args is { Length: >= 3 })
            {
                var eventType = args[1] as string ?? string.Empty;
                var payloadJson = System.Text.Json.JsonSerializer.Serialize(args[2], JsonOptions);
                Events.Add((eventType, payloadJson));
                return Task.FromResult((long)Events.Count);
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
