using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The environment steward end to end (todo E1), through the shipped pack: the solo master sees the
/// registry, takes the test environment for its run, and gives it back; the slot is the run's in between.
/// </summary>
public sealed partial class ToolChainEndpointTests
{
    [Fact]
    public async Task EnvironmentSteward_TheMasterTakesAnEnvironmentForItsRun_AndGivesItBack()
    {
        var workspace = Path.Combine(_root, "workspace-environment");
        Directory.CreateDirectory(workspace);
        var script = new ToolScriptedClient()
            .WhenWorkerTurns(
                [new FunctionCallContent("call-list", "environment_list", new Dictionary<string, object?>())],
                [new FunctionCallContent("call-take", "environment_acquire", new Dictionary<string, object?> { ["kind"] = "test", ["reason"] = "run the e2e suite" })],
                [new FunctionCallContent("call-back", "environment_release", new Dictionary<string, object?> { ["key"] = "staging" })],
                [new TextContent("在 staging 上跑完了，已归还。")])
            .WhenMeeting("在 staging 上跑完了，已归还。");
        var atRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        script.BeforeWorkerTurn = turn =>
        {
            if (turn != 3) return Task.CompletedTask;
            atRelease.TrySetResult();
            return proceed.Task;
        };
        _factory = new ToolChainFactory(_root, script, new FakeToolProvider());
        var client = _factory.CreateClient();
        await InstallGraphSeedPackAsync(client);
        var registered = await client.PostAsJsonAsync("/api/v1/environments", new
        {
            key = "staging", kind = "test", display_name = "Staging", connection = new { url = "https://staging.example", secret_ref = "staging-token" }
        });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "environment project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "environment session", mode_version_id = await LatestPublishedModeVersionIdAsync("solo") })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var active = StartStreamingInvoke(client, sessionId, new { content = "在测试环境上跑一遍端到端", client_message_id = "environment-1" });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        await atRelease.Task.WaitAsync(TimeSpan.FromSeconds(60));

        // Between taking and giving back, the slot is this run's.
        var held = Assert.Single(await client.GetFromJsonAsync<JsonElement[]>("/api/v1/environments") ?? []);
        var holder = Assert.Single(held.GetProperty("holders").EnumerateArray());
        Assert.Equal(runId, holder.GetProperty("run_id").GetGuid());
        Assert.Equal("run the e2e suite", holder.GetProperty("reason").GetString());
        Assert.Equal(0, held.GetProperty("free_slots").GetInt32());

        proceed.TrySetResult();
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("done", KindOf(Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error")));

        // The master read the registry (with the credential only named) and was told it got a slot.
        Assert.Contains(script.WorkerToolResults, result => result.Contains("\"secret_ref\":\"staging-token\"", StringComparison.Ordinal));
        Assert.Contains(script.WorkerToolResults, result => result.Contains("\"granted\":true", StringComparison.Ordinal));
        var after = Assert.Single(await client.GetFromJsonAsync<JsonElement[]>("/api/v1/environments") ?? []);
        Assert.Empty(after.GetProperty("holders").EnumerateArray());
        var events = (await _factory.Services.GetRequiredService<ILifecycleManager>().ReplayEventsAsync(sessionId, 0))
            .Where(e => string.Equals(e.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(events, e => e.EventType == "environment.assigned");
        Assert.Single(events, e => e.EventType == "environment.released");
    }
}
