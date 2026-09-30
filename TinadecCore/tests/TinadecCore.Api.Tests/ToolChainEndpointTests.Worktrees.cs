using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The worktree steward end to end (todo R5): a worktree the solo master creates is assigned to its
/// run — one writer per worktree, so another run is refused inside it — and removing it releases
/// the assignment.
/// </summary>
public sealed partial class ToolChainEndpointTests
{
    [Fact]
    public async Task WorktreeSteward_AssignsTheCreatedWorktreeToItsRun_AndRemovalReleasesIt()
    {
        var workspace = Path.Combine(_root, "workspace-worktree");
        Directory.CreateDirectory(workspace);
        var worktree = Path.Combine(workspace, ".tinadec", "worktrees", "feat-a");
        var provider = new FakeToolProvider
        {
            // Like the real tool: the result names the worktree it made or removed.
            ResultFor = (_, request) => request.ToolId switch
            {
                "git_worktree_create" => new { success = true, action = "create", path = worktree, branch = "feat-a" },
                "git_worktree_remove" => new { success = true, action = "remove", path = worktree },
                _ => null
            }
        };
        var script = new ToolScriptedClient()
            .WhenWorkerTurns(
                [new FunctionCallContent("call-wt", "git_worktree_create", new Dictionary<string, object?>
                {
                    ["repository_path"] = workspace, ["branch"] = "feat-a", ["confirm_worktree_create"] = "feat-a worktree for the parallel task"
                })],
                [new FunctionCallContent("call-rm", "git_worktree_remove", new Dictionary<string, object?>
                {
                    ["repository_path"] = workspace, ["path"] = worktree, ["confirm_worktree_remove"] = "remove feat-a worktree"
                })],
                [new TextContent("worktree 用完已回收。")])
            .WhenMeeting("worktree 用完已回收。");
        var atRemoval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        script.BeforeWorkerTurn = turn =>
        {
            if (turn != 2) return Task.CompletedTask;
            atRemoval.TrySetResult();
            return proceed.Task;
        };
        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        await InstallGraphSeedPackAsync(client);
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "worktree project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "worktree session", mode_version_id = await LatestPublishedModeVersionIdAsync("solo") })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        // Full access: the run is unattended, so the test observes the steward rather than clicking approvals.
        var active = StartStreamingInvoke(client, sessionId, new { content = "开个 worktree 并行做，做完回收", client_message_id = "worktree-1", permission_mode = "full-access" });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        await atRemoval.Task.WaitAsync(TimeSpan.FromSeconds(60));

        // Between creation and removal the worktree belongs to this run: not to the call, not to the task.
        var ledger = _factory.Services.GetRequiredService<IResourceLeaseService>();
        var assignment = Assert.Single(await ledger.ListActiveAsync(sessionId), lease => lease.Purpose == ResourceLeasePurposes.Assignment);
        Assert.Equal(ResourceLeaseKinds.Worktree, assignment.Kind);
        Assert.Equal(runId, assignment.RunId);
        Assert.Null(assignment.TaskId);
        Assert.EndsWith(".tinadec/worktrees/feat-a", assignment.ResourceKey, StringComparison.OrdinalIgnoreCase);
        // One writer per worktree: another run is refused anything inside it, and told who holds it.
        var holders = await ledger.ProbeAsync(new ResourceClaim(ResourceLeaseKinds.Path, Path.Combine(worktree, "src", "a.ts"), Exclusive: true), Guid.NewGuid());
        Assert.Contains(holders, holder => holder.Id == assignment.Id);

        proceed.TrySetResult();
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("done", KindOf(Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error")));
        Assert.Equal(["git_worktree_create", "git_worktree_remove"], provider.ReceivedToolIds.ToArray());

        var events = (await _factory.Services.GetRequiredService<ILifecycleManager>().ReplayEventsAsync(sessionId, 0))
            .Where(e => string.Equals(e.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(events, e => e.EventType == "worktree.assigned");
        var released = Assert.Single(events, e => e.EventType == "worktree.released");
        Assert.Equal(1, ((JsonElement)released.Payload["payload"]!).GetProperty("released").GetInt32());
        Assert.DoesNotContain(await ledger.ListActiveAsync(sessionId), lease => lease.Purpose == ResourceLeasePurposes.Assignment);
    }
}
