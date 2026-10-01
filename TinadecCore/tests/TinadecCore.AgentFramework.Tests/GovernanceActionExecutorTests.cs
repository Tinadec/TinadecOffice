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
}
