using System.Text.Json;
using TinadecCore.Models.Harness.Acp;

namespace TinadecCore.AgentFramework.Tests.Acp;

/// <summary>
/// Round 1's permission posture, tested directly. The point of these assertions is that a refusal is
/// a recorded decision, not an absence: the agent must get an answer, the turn must survive, and the
/// reason has to be readable later.
/// </summary>
public sealed class AcpInteractionRouterTests
{
    private static JsonElement PermissionParams(params (string OptionId, string Kind)[] options)
    {
        var payload = new
        {
            sessionId = "s1",
            toolCall = new { toolCallId = "t1", title = "run shell", kind = "execute" },
            options = options.Select(option => new { optionId = option.OptionId, name = option.OptionId, kind = option.Kind }).ToArray()
        };
        return JsonSerializer.SerializeToElement(payload);
    }

    [Fact]
    public void RejectOnce_IsChosenBeforeRejectAlways()
    {
        var router = new RefusingAcpInteractionRouter("s1");
        var observer = new RecordingAcpTurnObserver();

        var answer = router.RouteAsync("session/request_permission",
            PermissionParams(("allow-once", "allow_once"), ("reject-always", "reject_always"), ("reject-once", "reject_once")),
            observer, CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal("selected", answer.GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.Equal("reject-once", answer.GetProperty("outcome").GetProperty("optionId").GetString());
    }

    [Fact]
    public void RejectAlways_IsUsedWhenNoSingleUseRejectExists()
    {
        var router = new RefusingAcpInteractionRouter("s1");
        var observer = new RecordingAcpTurnObserver();

        var answer = router.RouteAsync("session/request_permission",
            PermissionParams(("allow-always", "allow_always"), ("reject-always", "reject_always")),
            observer, CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal("reject-always", answer.GetProperty("outcome").GetProperty("optionId").GetString());
    }

    [Fact]
    public void WhenTheAgentOfferedNoWayToRefuse_TheAnswerIsCancelledNotGuessed()
    {
        // Answering "selected" with an allow option would be the harness approving its own write.
        var router = new RefusingAcpInteractionRouter("s1");
        var observer = new RecordingAcpTurnObserver();

        var answer = router.RouteAsync("session/request_permission",
            PermissionParams(("allow-once", "allow_once")),
            observer, CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal("cancelled", answer.GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.False(answer.GetProperty("outcome").TryGetProperty("optionId", out var missing) && missing.ValueKind != JsonValueKind.Null);
        Assert.Single(observer.Refusals);
        Assert.Null(observer.Refusals[0].OptionId);
    }

    [Fact]
    public void RefusalIsRecordedWithTheToolAndTheReason_RatherThanBeingSilent()
    {
        var router = new RefusingAcpInteractionRouter("s1");
        var observer = new RecordingAcpTurnObserver();

        router.RouteAsync("session/request_permission", PermissionParams(("reject-once", "reject_once")), observer, CancellationToken.None)
            .GetAwaiter().GetResult();

        var refusal = Assert.Single(observer.Refusals);
        Assert.Equal("t1", refusal.ToolCallId);
        Assert.Equal("run shell", refusal.Title);
        Assert.Equal(RefusingAcpInteractionRouter.NoApprovalBridge, refusal.Reason);
    }

    [Fact]
    public async Task UnknownVendorMethod_IsDeclinedNotGuessed_AndItsNameIsRemembered()
    {
        var router = new RefusingAcpInteractionRouter("s1");
        var observer = new RecordingAcpTurnObserver();

        var error = await Assert.ThrowsAsync<AcpMethodNotFoundException>(() =>
            router.RouteAsync("_codebuddy.ai/question", JsonSerializer.SerializeToElement(new { schema = new { } }), observer, CancellationToken.None));

        Assert.Equal("_codebuddy.ai/question", error.Method);
        // Batch 2 needs the list of what real harnesses actually ask, which cannot be written from a
        // specification. Declining must therefore also record.
        Assert.Contains("_codebuddy.ai/question", RefusingAcpInteractionRouter.ObservedUnsupportedMethods, StringComparer.Ordinal);
    }

    [Fact]
    public void RoundOneRouterHoldsNothingOpen_SoTheDrainWindowCanClose()
    {
        var router = new RefusingAcpInteractionRouter("s1");

        Assert.Equal(0, router.PendingCount);
        router.CloseAll();
        Assert.Equal(0, router.PendingCount);
    }

    [Fact]
    public void FactoryGivesEachSessionItsOwnRouter()
    {
        var factory = new RefusingAcpInteractionRouterFactory();

        var first = factory.Create("session-a");
        var second = factory.Create("session-b");

        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task ApprovalBridgingRouter_HoldsPendingCountOpenWhileParked_AndMapsApproveRejectAndCloseAll()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<TinadecCore.Abstractions.Ports.AcpPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new DelegatingApprovalBridge(async (_, ct) =>
        {
            entered.TrySetResult();
            return await gate.Task.WaitAsync(ct);
        });
        var context = new AcpInteractionContext("s-bridge", RunId: "run-1", TeammateHandle: "fe", WorkingDirectory: "/repo/wt-fe", WriteScope: ["src"]);
        var router = new ApprovalBridgingAcpInteractionRouter(context, bridge);
        var observer = new RecordingAcpTurnObserver();

        // 1. Parked approval keeps PendingCount > 0 until human/delegate decision resolves -> allow_once selected.
        var routeTask = router.RouteAsync(
            "session/request_permission",
            PermissionParams(("allow-always", "allow_always"), ("allow-once", "allow_once"), ("reject-once", "reject_once")),
            observer,
            CancellationToken.None);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, router.PendingCount);
        gate.SetResult(new TinadecCore.Abstractions.Ports.AcpPermissionDecision(true, "approved"));

        var approvedAnswer = await routeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, router.PendingCount);
        Assert.Equal("selected", approvedAnswer.GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.Equal("allow-once", approvedAnswer.GetProperty("outcome").GetProperty("optionId").GetString());

        // 2. Denied decision selects reject_once and records refusal reason.
        var denyBridge = new DelegatingApprovalBridge((_, _) =>
            Task.FromResult(new TinadecCore.Abstractions.Ports.AcpPermissionDecision(false, "outside_write_scope")));
        var denyRouter = new ApprovalBridgingAcpInteractionRouter(context, denyBridge);
        var denyObserver = new RecordingAcpTurnObserver();
        var deniedAnswer = await denyRouter.RouteAsync(
            "session/request_permission",
            PermissionParams(("allow-once", "allow_once"), ("reject-always", "reject_always"), ("reject-once", "reject_once")),
            denyObserver,
            CancellationToken.None);
        Assert.Equal("selected", deniedAnswer.GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.Equal("reject-once", deniedAnswer.GetProperty("outcome").GetProperty("optionId").GetString());
        Assert.Equal("outside_write_scope", Assert.Single(denyObserver.Refusals).Reason);

        // 3. CloseAll cancels parked bridge calls cleanly and resets PendingCount to 0.
        var parkedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<TinadecCore.Abstractions.Ports.AcpPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelRouter = new ApprovalBridgingAcpInteractionRouter(context, new DelegatingApprovalBridge(async (_, ct) =>
        {
            parkedEntered.TrySetResult();
            return await never.Task.WaitAsync(ct);
        }));
        var cancelObserver = new RecordingAcpTurnObserver();
        var parkedTask = cancelRouter.RouteAsync("session/request_permission", PermissionParams(("allow-once", "allow_once")), cancelObserver, CancellationToken.None);
        await parkedEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, cancelRouter.PendingCount);
        cancelRouter.CloseAll();
        var cancelledAnswer = await parkedTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, cancelRouter.PendingCount);
        Assert.Equal("cancelled", cancelledAnswer.GetProperty("outcome").GetProperty("outcome").GetString());
    }

    private sealed class DelegatingApprovalBridge(
        Func<TinadecCore.Abstractions.Ports.AcpPermissionCheckRequest, CancellationToken, Task<TinadecCore.Abstractions.Ports.AcpPermissionDecision>> handler)
        : TinadecCore.Abstractions.Ports.IAcpApprovalBridge
    {
        public Task<TinadecCore.Abstractions.Ports.AcpPermissionDecision> EvaluateOrWaitForApprovalAsync(
            TinadecCore.Abstractions.Ports.AcpPermissionCheckRequest request,
            CancellationToken cancellationToken = default) =>
            handler(request, cancellationToken);
    }
}
