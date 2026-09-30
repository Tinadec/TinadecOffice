using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Runtime;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Delegated approval gates end to end (architecture §7.2, todo R1/R2): the user picks, per message,
/// who approves on their behalf — the conversation identity, a reviewer in its own context, or both —
/// and the call runs only when every gate the mode asks for has approved.
/// </summary>
public sealed partial class ToolChainEndpointTests
{
    private const string GoalSentinel = "SENTINEL-GOAL-4F2A";
    private const string Approve = "{\"decision\":\"approve\",\"rationale\":\"写入 docs/a.md 正是任务要的。\"}";
    private static readonly Dictionary<string, string?> GatesByHand = new() { ["TinadecApprovalGates:Enabled"] = "false" };

    /// <summary>
    /// Two gates, stepped one pass at a time in a Team run. The reviewer — the run's standing
    /// governance reviewer, with its own frozen prompt — decides first, seeing the call and the task
    /// but not the user's words; its approval alone releases nothing. The conversation identity then
    /// decides knowing the goal, and only now does the call run. Each gate's record holds exactly what
    /// it was shown.
    /// </summary>
    [Fact]
    public async Task DelegateBoth_TeamRun_OnlyBothGatesApprovingRunsTheCall_AndTheReviewerNeverSeesTheConversation()
    {
        var workspace = Path.Combine(_root, "workspace-gates-both");
        Directory.CreateDirectory(Path.Combine(workspace, "docs"));
        var provider = new FakeToolProvider();
        var script = new ToolScriptedClient()
            .WhenPlanner("[{\"task_key\":\"doc\",\"title\":\"写文档\",\"description\":\"在 docs 下写说明。\",\"success_criteria\":[\"文档存在\"],\"dependencies\":[],\"required_capabilities\":[],\"required_tools\":[\"write_file\"],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorkerTurns(
                [new FunctionCallContent("call-doc", "write_file", new Dictionary<string, object?> { ["filepath"] = "docs/a.md", ["content"] = "x", ["token"] = "sk-live-SECRET-9" })],
                [new TextContent("docs/a.md 已写入。")])
            .WhenMeeting("文档已写入。")
            .WhenReviewerGate(Approve)
            .WhenConversationGate("{\"decision\":\"approve\",\"rationale\":\"用户要的就是这份文档。\"}");
        _factory = new ToolChainFactory(_root, script, provider, settings: GatesByHand);
        var client = _factory.CreateClient();
        await InstallGraphSeedPackAsync(client);
        var sessionId = await GateSessionAsync(client, workspace, await LatestPublishedModeVersionIdAsync("free_director"));

        var active = StartStreamingInvoke(client, sessionId, new { content = $"写一份文档 {GoalSentinel}", client_message_id = "gates-both-1", permission_mode = "delegate-both" });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));
        // The PDP released the write to the approval layer: what waits is the tool approval, not a policy park.
        Assert.Equal("tool", (await client.GetFromJsonAsync<JsonElement>($"/api/v1/approvals/{approvalId}")).GetProperty("kind").GetString());

        var gates = _factory.Services.GetRequiredService<ApprovalGateService>();
        Assert.Equal(1, await gates.RunPassAsync());
        var first = await client.GetFromJsonAsync<ApprovalGatesDto>($"/api/v1/approvals/{approvalId}/gates", SnakeWire);
        Assert.Equal("evaluating", first!.Status);
        Assert.Equal(["approved", "pending"], first.Gates.Select(gate => gate.Status).ToArray());
        Assert.Equal(["reviewer_agent", "conversation_identity"], first.Gates.Select(gate => gate.GateKind).ToArray());
        Assert.Equal("governance_reviewer", first.Gates[0].DeciderAgent);
        // One approval is not two: the call is still waiting and has not run.
        Assert.Equal("pending", (await client.GetFromJsonAsync<JsonElement>($"/api/v1/approvals/{approvalId}")).GetProperty("status").GetString());
        await Task.Delay(500);
        Assert.Equal(0, provider.CallCount);

        Assert.Equal(1, await gates.RunPassAsync());
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("done", KindOf(Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error")));
        Assert.Equal(["write_file"], provider.ReceivedToolIds.ToArray());

        var decided = await client.GetFromJsonAsync<ApprovalGatesDto>($"/api/v1/approvals/{approvalId}/gates", SnakeWire);
        Assert.Equal("approved", decided!.Status);
        Assert.Equal("meeting", decided.Gates[1].DeciderAgent);

        // R1: the reviewer's whole context is the call, the task and the run's facts — never the conversation.
        var reviewer = Assert.Single(script.GateCalls, call => call.Gate == "reviewer");
        Assert.DoesNotContain(GoalSentinel, reviewer.Instructions + reviewer.Facts, StringComparison.Ordinal);
        Assert.Contains("治理审查智能体", reviewer.Instructions, StringComparison.Ordinal);
        Assert.Contains("write_file", reviewer.Facts, StringComparison.Ordinal);
        Assert.Contains("docs/a.md", reviewer.Facts, StringComparison.Ordinal);
        // A secret in the call never reaches a gate — not the reviewer, not the conversation identity.
        Assert.DoesNotContain(script.GateCalls, call => call.Facts.Contains("sk-live-SECRET-9", StringComparison.Ordinal));
        var conversation = Assert.Single(script.GateCalls, call => call.Gate == "conversation");
        Assert.Contains(GoalSentinel, conversation.Facts, StringComparison.Ordinal);

        // ...and each record says exactly what its gate looked at.
        var reviewerEvidence = decided.Gates[0].Evidence!.Value;
        Assert.Equal("write_file", reviewerEvidence.GetProperty("tool_id").GetString());
        Assert.Equal("写文档", reviewerEvidence.GetProperty("task_title").GetString());
        Assert.Equal(JsonValueKind.Null, reviewerEvidence.GetProperty("user_goal").ValueKind);
        Assert.Contains(GoalSentinel, decided.Gates[1].Evidence!.Value.GetProperty("user_goal").GetString(), StringComparison.Ordinal);

        var events = (await _factory.Services.GetRequiredService<ILifecycleManager>().ReplayEventsAsync(sessionId, 0))
            .Where(e => string.Equals(e.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(2, events.Count(e => e.EventType == "approval.gate_decided"));
        var applied = Assert.Single(events, e => e.EventType == "approval.decided");
        Assert.Equal("delegated_gates", ((JsonElement)applied.Payload["payload"]!).GetProperty("decided_by").GetString());
    }

    /// <summary>A refusal at the first gate refuses: the second gate is never asked and nothing runs.</summary>
    [Fact]
    public async Task DelegateBoth_AReviewerRejection_RefusesTheCall_AndTheConversationIsNeverAsked()
    {
        var workspace = Path.Combine(_root, "workspace-gates-reject");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var script = DefaultModeWrite()
            .WhenReviewerGate("{\"decision\":\"reject\",\"rationale\":\"任务没说要写 probe.txt。\"}");
        _factory = new ToolChainFactory(_root, script, provider, settings: GatesByHand);
        var client = _factory.CreateClient();
        var pack = await InstallToolChainPackAsync(client);
        var sessionId = await GateSessionAsync(client, workspace, ToolChainPackModeVersionId(pack, "default-mode"));

        var active = StartStreamingInvoke(client, sessionId, new { content = "写一个文件", client_message_id = "gates-reject-1", permission_mode = "delegate-both" });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));

        Assert.Equal(1, await _factory.Services.GetRequiredService<ApprovalGateService>().RunPassAsync());
        await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        var gates = await client.GetFromJsonAsync<ApprovalGatesDto>($"/api/v1/approvals/{approvalId}/gates", SnakeWire);
        Assert.Equal("rejected", gates!.Status);
        Assert.Equal(["rejected", "skipped"], gates.Gates.Select(gate => gate.Status).ToArray());
        Assert.Equal("core_reviewer", gates.Gates[0].DeciderAgent);
        Assert.Equal("rejected", (await client.GetFromJsonAsync<JsonElement>($"/api/v1/approvals/{approvalId}")).GetProperty("status").GetString());
        Assert.DoesNotContain(script.GateCalls, call => call.Gate == "conversation");
        Assert.Equal(0, provider.CallCount);
    }

    /// <summary>Each single-gate mode: its one gate decides, the other is never asked, and the call runs.</summary>
    [Theory]
    [InlineData("delegate-reviewer", "reviewer", "reviewer_agent")]
    [InlineData("delegate-conversation", "conversation", "conversation_identity")]
    public async Task SingleGateModes_TheirOneGateDecides_AndTheCallRuns(string mode, string gate, string gateKind)
    {
        var workspace = Path.Combine(_root, $"workspace-gates-{gate}");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var script = DefaultModeWrite();
        script = gate == "reviewer" ? script.WhenReviewerGate(Approve) : script.WhenConversationGate(Approve);
        // The background service drives these: no pass is stepped by hand.
        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        var pack = await InstallToolChainPackAsync(client);
        var sessionId = await GateSessionAsync(client, workspace, ToolChainPackModeVersionId(pack, "default-mode"));

        var active = StartStreamingInvoke(client, sessionId, new { content = "写一个文件", client_message_id = $"gates-{gate}-1", permission_mode = mode });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(90));
        Assert.Equal("done", KindOf(Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error")));
        Assert.Equal(["write_file"], provider.ReceivedToolIds.ToArray());

        var call = Assert.Single(script.GateCalls);
        Assert.Equal(gate, call.Gate);
        var approvals = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/approvals?run_id={runId}");
        var approvalId = Assert.Single(approvals!, item => item.GetProperty("kind").GetString() == "tool").GetProperty("id").GetGuid();
        var gates = await client.GetFromJsonAsync<ApprovalGatesDto>($"/api/v1/approvals/{approvalId}/gates", SnakeWire);
        Assert.Equal("approved", gates!.Status);
        Assert.Equal(gateKind, Assert.Single(gates.Gates).GateKind);
    }

    /// <summary>
    /// A gate that is unsure hands the decision back: the approval stays the person's, no further
    /// pass touches it, and the person's click still decides it.
    /// </summary>
    [Fact]
    public async Task DelegateReviewer_AnUnsureReviewerEscalates_AndThePersonStillDecides()
    {
        var workspace = Path.Combine(_root, "workspace-gates-escalate");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var script = DefaultModeWrite()
            .WhenReviewerGate("{\"decision\":\"escalate\",\"rationale\":\"看不出 probe.txt 是否该写。\"}");
        _factory = new ToolChainFactory(_root, script, provider, settings: GatesByHand);
        var client = _factory.CreateClient();
        var pack = await InstallToolChainPackAsync(client);
        var sessionId = await GateSessionAsync(client, workspace, ToolChainPackModeVersionId(pack, "default-mode"));

        var active = StartStreamingInvoke(client, sessionId, new { content = "写一个文件", client_message_id = "gates-escalate-1", permission_mode = "delegate-reviewer" });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));

        var service = _factory.Services.GetRequiredService<ApprovalGateService>();
        Assert.Equal(1, await service.RunPassAsync());
        Assert.Equal(0, await service.RunPassAsync());
        var gates = await client.GetFromJsonAsync<ApprovalGatesDto>($"/api/v1/approvals/{approvalId}/gates", SnakeWire);
        Assert.Equal("escalated", gates!.Status);
        Assert.Contains("probe.txt", Assert.Single(gates.Gates).Reason, StringComparison.Ordinal);
        Assert.Equal("pending", (await client.GetFromJsonAsync<JsonElement>($"/api/v1/approvals/{approvalId}")).GetProperty("status").GetString());
        Assert.Equal(0, provider.CallCount);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" })).StatusCode);
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("done", KindOf(Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error")));
        Assert.Equal(1, provider.CallCount);
    }

    /// <summary>
    /// Delegation never reaches a human-only tool: in a delegated run a shell call parks at the policy
    /// layer for the person exactly as in ask, no gate is opened or asked, and the person's click runs it.
    /// </summary>
    [Fact]
    public async Task DelegatedRun_AHumanOnlyToolParksForThePerson_AndNoGateIsAsked()
    {
        var workspace = Path.Combine(_root, "workspace-gates-shell");
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        var script = new ToolScriptedClient()
            .WhenPlanner("[{\"task_key\":\"run-cmd\",\"title\":\"跑命令\",\"description\":\"执行一次构建命令\",\"success_criteria\":[\"命令执行\"],\"dependencies\":[],\"required_capabilities\":[\"tool.code\"],\"required_tools\":[\"shell\"],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorkerTool("shell", new Dictionary<string, object?> { ["command"] = "npm run build" })
            .WhenWorkerFollowUp("命令已执行。")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("命令已执行。")
            .WhenReviewerGate(Approve)
            .WhenConversationGate(Approve);
        _factory = new ToolChainFactory(_root, script, provider, settings: GatesByHand);
        var client = _factory.CreateClient();
        var pack = await InstallToolChainPackAsync(client);
        var sessionId = await GateSessionAsync(client, workspace, ToolChainPackModeVersionId(pack, "default-mode"));

        var active = StartStreamingInvoke(client, sessionId, new { content = "跑一次构建", client_message_id = "gates-shell-1", permission_mode = "delegate-both" });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        var approvalId = await WaitForPendingApprovalAsync(client, sessionId, runId, TimeSpan.FromSeconds(45));
        Assert.Equal("permission", (await client.GetFromJsonAsync<JsonElement>($"/api/v1/approvals/{approvalId}")).GetProperty("kind").GetString());

        Assert.Equal(0, await _factory.Services.GetRequiredService<ApprovalGateService>().RunPassAsync());
        Assert.Empty(script.GateCalls);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/approvals/{approvalId}/gates")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "approved" })).StatusCode);
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("done", KindOf(Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error")));
        Assert.Equal(["shell"], provider.ReceivedToolIds.ToArray());
        Assert.Empty(script.GateCalls);
    }

    private static ToolScriptedClient DefaultModeWrite() => new ToolScriptedClient()
        .WhenPlanner("[{\"task_key\":\"write-probe\",\"title\":\"写探针文件\",\"description\":\"创建 probe.txt\",\"success_criteria\":[\"文件存在\"],\"dependencies\":[],\"required_capabilities\":[\"tool.file\"],\"required_tools\":[\"write_file\"],\"priority\":1,\"risk\":\"low\"}]")
        .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[\"ok\"],\"revise_task_indexes\":[]}")
        .WhenMeeting("文件已写入。");

    private static async Task<Guid> GateSessionAsync(HttpClient client, string workspace, Guid modeVersionId)
    {
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "gate project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new
        {
            project_id = project.GetProperty("id").GetGuid(),
            title = "gate session",
            mode_version_id = modeVersionId
        })).Content.ReadFromJsonAsync<JsonElement>();
        return session.GetProperty("id").GetGuid();
    }
}
