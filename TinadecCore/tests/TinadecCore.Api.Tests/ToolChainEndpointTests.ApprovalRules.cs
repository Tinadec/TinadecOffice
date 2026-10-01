using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Runtime;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Standing approval rules end to end (todo E7, architecture §7.4 items 6–7): a person's
/// command-prefix rule releases a covered shell call in the default ask mode without a
/// fresh click, and a per-session delegate_tool opt-in hands a human-only tool to the
/// delegated gates. An uncovered call keeps every gate it had today.
/// </summary>
public sealed partial class ToolChainEndpointTests
{
    private const string ShellPlan = "[{\"task_key\":\"run-cmd\",\"title\":\"跑命令\",\"description\":\"执行一次命令\",\"success_criteria\":[\"命令执行\"],\"dependencies\":[],\"required_capabilities\":[\"tool.code\"],\"required_tools\":[\"shell\"],\"priority\":1,\"risk\":\"low\"}]";

    /// <summary>
    /// Default ask mode: a workspace prefix rule covering "npm run build" releases the
    /// call at the PDP and the approval layer mints from the rule — the run completes
    /// without anyone clicking anything.
    /// </summary>
    [Fact]
    public async Task ShellCoveredByAPrefixRule_RunsInAskModeWithoutAHumanClick()
    {
        var workspace = Path.Combine(_root, "workspace-rule-covered");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var script = new ToolScriptedClient()
            .WhenPlanner(ShellPlan)
            .WhenWorkerTool("shell", new Dictionary<string, object?> { ["command"] = "npm run build" })
            .WhenWorkerFollowUp("命令已执行。")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("命令已执行。");
        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        var pack = await InstallToolChainPackAsync(client);
        var sessionId = await GateSessionAsync(client, workspace, ToolChainPackModeVersionId(pack, "default-mode"));

        // The person wrote this rule once, up front — the click it replaces is one they already gave.
        var rules = _factory.Services.GetRequiredService<IApprovalRules>();
        var rule = await rules.CreateAsync(new ApprovalRuleCreate(ApprovalRuleKinds.CommandPrefix, "shell", "npm run", sessionId));

        var active = StartStreamingInvoke(client, sessionId, new { content = "跑一次构建", client_message_id = "rule-covered-1" });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("done", KindOf(Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error")));
        Assert.Equal(["shell"], provider.ReceivedToolIds.ToArray());

        var events = (await _factory.Services.GetRequiredService<ILifecycleManager>().ReplayEventsAsync(sessionId, 0))
            .Where(e => string.Equals(e.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
        var minted = Assert.Single(events, e => e.EventType == "approval.pre_authorized_minted");
        Assert.StartsWith("approval_rule", ((JsonElement)minted.Payload["payload"]!).GetProperty("source").GetString(), StringComparison.Ordinal);
        // Nobody was asked: no human decision endpoint was touched for this run.
        Assert.DoesNotContain(events, e => e.EventType == "approval.decided"
            && string.Equals(((JsonElement)e.Payload["payload"]!).GetProperty("decided_by").GetString(), "human", StringComparison.Ordinal));

        Assert.Equal(1, (await rules.ListAsync(sessionId)).Single(r => r.Id == rule.Id).UseCount);
    }

    /// <summary>
    /// The rule is for "npm run"; "npm ci" is not covered — the same run parks for the
    /// person exactly as it would without any rule, and the command never executes.
    /// </summary>
    [Fact]
    public async Task ShellNotCovered_StillParksForThePerson()
    {
        var workspace = Path.Combine(_root, "workspace-rule-uncovered");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var script = new ToolScriptedClient()
            .WhenPlanner(ShellPlan)
            .WhenWorkerTool("shell", new Dictionary<string, object?> { ["command"] = "npm ci" })
            .WhenWorkerFollowUp("命令已执行。")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("命令已执行。");
        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        var pack = await InstallToolChainPackAsync(client);
        var sessionId = await GateSessionAsync(client, workspace, ToolChainPackModeVersionId(pack, "default-mode"));

        var rules = _factory.Services.GetRequiredService<IApprovalRules>();
        await rules.CreateAsync(new ApprovalRuleCreate(ApprovalRuleKinds.CommandPrefix, "shell", "npm run", sessionId));

        var active = StartStreamingInvoke(client, sessionId, new { content = "跑一次构建", client_message_id = "rule-uncovered-1" });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));
        Assert.Equal("permission", (await client.GetFromJsonAsync<JsonElement>($"/api/v1/approvals/{approvalId}")).GetProperty("kind").GetString());
        Assert.Equal(0, provider.CallCount);

        await client.PostAsJsonAsync($"/api/v1/runs/{runId}/control", new { action = "cancel" });
    }

    /// <summary>
    /// A session opt-in delegates only a call inside the ceiling. A high-risk shell
    /// call still waits for the person. The provider is inert for both cases.
    /// </summary>
    [Theory]
    [InlineData("medium", true)]
    [InlineData("high", false)]
    public async Task DelegateReviewer_WithShellOptedIn_StillEnforcesRiskCeiling(string risk, bool delegated)
    {
        var workspace = Path.Combine(_root, "workspace-rule-shell-gate");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider { ShellRisk = risk };
        var script = new ToolScriptedClient()
            .WhenPlanner(ShellPlan)
            .WhenWorkerTool("shell", new Dictionary<string, object?> { ["command"] = "npm run build" })
            .WhenWorkerFollowUp("命令已执行。")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("命令已执行。")
            .WhenReviewerGate(Approve);
        _factory = new ToolChainFactory(_root, script, provider, settings: GatesByHand);
        var client = _factory.CreateClient();
        var pack = await InstallToolChainPackAsync(client);
        var sessionId = await GateSessionAsync(client, workspace, ToolChainPackModeVersionId(pack, "default-mode"));

        var rules = _factory.Services.GetRequiredService<IApprovalRules>();
        await rules.CreateAsync(new ApprovalRuleCreate(ApprovalRuleKinds.DelegateTool, "shell", null, sessionId));

        var active = StartStreamingInvoke(client, sessionId, new { content = "跑一次构建", client_message_id = "rule-shell-gate-1", permission_mode = "delegate-reviewer" });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));
        Assert.Equal(delegated ? "tool" : "permission", (await client.GetFromJsonAsync<JsonElement>($"/api/v1/approvals/{approvalId}")).GetProperty("kind").GetString());

        Assert.Equal(delegated ? 1 : 0, await _factory.Services.GetRequiredService<ApprovalGateService>().RunPassAsync());
        if (!delegated)
        {
            Assert.Empty(script.GateCalls);
            Assert.Equal(0, provider.CallCount);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" })).StatusCode);
        }
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("done", KindOf(Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error")));
        Assert.Equal(["shell"], provider.ReceivedToolIds.ToArray());

        if (delegated)
        {
            var gate = Assert.Single(script.GateCalls);
            Assert.Equal("reviewer", gate.Gate);
            var gates = await client.GetFromJsonAsync<ApprovalGatesDto>($"/api/v1/approvals/{approvalId}/gates", SnakeWire);
            Assert.Equal("approved", gates!.Status);
        }
        else Assert.Empty(script.GateCalls);
    }
}
