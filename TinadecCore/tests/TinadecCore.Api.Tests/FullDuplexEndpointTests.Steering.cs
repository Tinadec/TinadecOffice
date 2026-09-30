using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Steering a working run (todo D2). A soft insert takes effect at the run's next model call; a hard
/// insert also cuts off the call in progress, and the step is redone with the steering in view.
/// </summary>
public sealed partial class FullDuplexEndpointTests
{
    private const string OneTaskPlan = "[{\"title\":\"任务\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]";

    /// <summary>
    /// An insert used to be recorded and then read by nobody: the context was built from messages
    /// only, and an insert posts none. The worker redone after the change now reads it.
    /// </summary>
    [Fact]
    public async Task SoftInsert_TheNextModelCallReadsTheSteering()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ScriptedChatClient()
            .WhenPlanner(OneTaskPlan)
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("好了。");
        script.BeforeWorker = gate.Task;
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var active = StartStreamingInvoke(client, sessionId, new { content = "整理一下项目", client_message_id = "soft-steer-1" });
        var runId = RunIdOf(await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)));
        await WaitForAsync(() => script.WorkerGateEntries >= 1);

        var insert = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new
        {
            content = "SOFT-STEER-3 只看 README",
            client_message_id = "soft-steer-2",
            dispatch_mode = "insert",
            target_run_id = runId
        });
        Assert.Equal(HttpStatusCode.Created, insert.StatusCode);
        Assert.False((await insert.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("interrupt").GetBoolean());
        gate.SetResult();
        await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        // The worker that ran before the insert did not see it; the one redone after it did.
        Assert.Equal(2, script.WorkerCalls);
        Assert.Contains(script.Instructions, instructions => instructions.Contains("User steering", StringComparison.Ordinal)
            && instructions.Contains("SOFT-STEER-3 只看 README", StringComparison.Ordinal));
    }

    /// <summary>
    /// A hard insert while the planner thinks: the planner call is cut off, the pass ends, and the
    /// next pass plans again — with the steering in the planner's context.
    /// </summary>
    [Fact]
    public async Task HardInsert_DuringPlanning_RedoesTheStepWithTheSteering()
    {
        var plannerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ScriptedChatClient()
            .WhenPlanner(OneTaskPlan)
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("好了。");
        script.BeforePlanner = plannerGate.Task;
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var active = StartStreamingInvoke(client, sessionId, new { content = "写一个功能", client_message_id = "hard-steer-1" });
        var runId = RunIdOf(await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)));
        await WaitForAsync(() => script.PlannerGateEntries >= 1);

        var insert = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new
        {
            content = "HARD-STEER-4 先写测试",
            client_message_id = "hard-steer-2",
            dispatch_mode = "insert",
            target_run_id = runId,
            interrupt = true
        });
        Assert.Equal(HttpStatusCode.Created, insert.StatusCode);
        Assert.Equal(1, (await insert.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("interrupted_model_calls").GetInt32());
        plannerGate.SetResult();
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("completed", chunks.Last(chunk => KindOf(chunk) is "done" or "error").GetProperty("finish_reason").GetString());

        // Planned twice: the cut-off call, then the redone one, which read the steering.
        Assert.True(script.PlannerGateEntries >= 2, $"PlannerGateEntries={script.PlannerGateEntries}");
        Assert.Equal(1, script.PlannerCalls);
        Assert.Contains(script.Instructions, instructions => instructions.Contains("任务规划智能体", StringComparison.Ordinal)
            && instructions.Contains("HARD-STEER-4 先写测试", StringComparison.Ordinal));
        var events = (await factory.Services.GetRequiredService<ILifecycleManager>().ReplayEventsAsync(sessionId, 0))
            .Where(e => e.RunId == runId.ToString()).ToList();
        Assert.Single(events, e => e.EventType == "run.interrupted");
        Assert.Single(events, e => e.EventType == "interaction.interrupt_requested");
        Assert.DoesNotContain(events, e => e.EventType == "run.failed");
    }

    [Fact]
    public async Task Interrupt_IsOnlyForAnInsert()
    {
        var factory = CreateFactory(new ScriptedChatClient());
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var queued = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new { content = "你好", client_message_id = "interrupt-queued", dispatch_mode = "queued", interrupt = true });
        Assert.Equal(HttpStatusCode.BadRequest, queued.StatusCode);
        var notBoolean = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new { content = "你好", client_message_id = "interrupt-text", dispatch_mode = "insert", target_run_id = Guid.NewGuid(), interrupt = "yes" });
        Assert.Equal(HttpStatusCode.BadRequest, notBoolean.StatusCode);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!condition() && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
        Assert.True(condition(), "The run never reached the expected point.");
    }
}
