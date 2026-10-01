using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Runtime;

namespace TinadecCore.AgentFramework.Tests;

public sealed class GovernanceActionExecutorTests
{
    [Fact]
    public async Task StopRun_UsesTheDurableCancelVerb_AndTheReportRevisionKey()
    {
        var controller = new RecordingController();
        var executor = new OrganizationReportActionExecutor(controller);
        var scope = new TenantContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test");
        var sessionId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var reportId = Guid.NewGuid();

        var result = await executor.ExecuteAsync(new OrganizationReportActionRequest(
            scope, sessionId, Guid.NewGuid(), reportId, Guid.NewGuid(), "stop_run", null,
            "run", runId.ToString("N"), $"report:{reportId:N}:revision:3"));

        Assert.True(result.Completed);
        Assert.Equal("run_control_applied", result.Code);
        Assert.Equal(runId, result.TargetRunId);
        var call = Assert.Single(controller.Calls);
        Assert.Equal("cancel", call.Action);
        Assert.Equal("report:" + reportId.ToString("N") + ":revision:3", call.ClientControlId);
        Assert.Equal(sessionId, call.SessionId);
        Assert.Equal(scope.TenantId, call.Scope.TenantId);
    }

    [Theory]
    [InlineData("reassign", "action_not_implemented")]
    [InlineData("wait", "follow_up_required")]
    [InlineData("ask_user", "user_decision_required")]
    public async Task NonRunControlProposalsRemainOpen(string verb, string code)
    {
        var controller = new RecordingController();
        var executor = new OrganizationReportActionExecutor(controller);

        var result = await executor.ExecuteAsync(new OrganizationReportActionRequest(
            new TenantContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test"), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), verb, null, "task", Guid.NewGuid().ToString("N"), "stable-key"));

        Assert.False(result.Completed);
        Assert.Equal(code, result.Code);
        Assert.Empty(controller.Calls);
    }

    [Fact]
    public async Task SeparateWorktree_ValidatesTheProjectBoundary_AndTakesAnAssignmentLease()
    {
        var root = Path.Combine(Path.GetTempPath(), "tinadec-action-worktree", Guid.NewGuid().ToString("N"));
        var worktree = Path.Combine(root, ".tinadec", "worktrees", "review");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../.git/worktrees/review");
        try
        {
            var sessionId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var scope = new TenantContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test");
            var lease = new RecordingLeases();
            var executor = new OrganizationReportActionExecutor(new RecordingController(), leases: lease,
                sessions: new RecordingSessions(root, sessionId, scope.TenantId, scope.WorkspaceId));
            var result = await executor.ExecuteAsync(new OrganizationReportActionRequest(
                scope, sessionId, Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), "separate_worktree",
                JsonSerializer.Serialize(new { path = worktree, reason = "review" }), "run", runId.ToString(), "key"));

            Assert.True(result.Completed, result.Message);
            var call = Assert.Single(lease.Calls);
            Assert.Equal(ResourceLeaseKinds.Worktree, call.Claim.Kind);
            Assert.Equal(runId, call.Request.RunId);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class RecordingController : IGovernanceRunController
    {
        public List<Call> Calls { get; } = [];

        public Task<GovernanceRunControlResult> ControlAsync(TenantContext scope, Guid sessionId, Guid runId,
            string action, string clientControlId, CancellationToken cancellationToken = default)
        {
            Calls.Add(new Call(scope, sessionId, runId, action, clientControlId));
            return Task.FromResult(new GovernanceRunControlResult(true, action == "cancel" ? "cancelled" : "paused", action, RunId: runId));
        }
    }

    private sealed record Call(TenantContext Scope, Guid SessionId, Guid RunId, string Action, string ClientControlId);

    private sealed class RecordingSessions(string root, Guid sessionId, Guid tenantId, Guid workspaceId) : ISessionLocator
    {
        public Task<SessionReference?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionReference?>(id == sessionId ? new SessionReference(id, Guid.NewGuid(), tenantId, workspaceId) : null);

        public Task<ProjectReference?> FindProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProjectReference?>(new ProjectReference(projectId, tenantId, workspaceId, root));
    }

    private sealed class RecordingLeases : IResourceLeaseService
    {
        public List<(ResourceAcquireRequest Request, ResourceClaim Claim)> Calls { get; } = [];

        public Task<ResourceLeaseDecision> AcquireAsync(ResourceAcquireRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add((request, request.Claim));
            return Task.FromResult(new ResourceLeaseDecision(new ResourceLeaseInfo(Guid.NewGuid(), request.Claim.Kind, request.Claim.ResourceKey,
                request.Claim.Exclusive, request.SessionId, request.RunId, request.TaskId, request.AgentInstanceId,
                ResourceLeaseStatuses.Active, request.Purpose, request.Reason), []));
        }

        public Task<IReadOnlyList<ResourceLeaseInfo>> ProbeAsync(ResourceClaim claim, Guid? runId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResourceLeaseInfo>>([]);
        public Task<int> ReleaseRunAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> ReleaseTaskAsync(Guid runId, Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> ReleaseAsync(Guid leaseId, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<IReadOnlyList<ResourceLeaseInfo>> ListActiveAsync(Guid? sessionId = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResourceLeaseInfo>>([]);
        public Task<IReadOnlyList<ResourceLeaseInfo>> ListTaskAsync(Guid runId, Guid taskId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResourceLeaseInfo>>([]);
    }
}
