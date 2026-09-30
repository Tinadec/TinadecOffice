using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Parallel = instances of one conversation identity sharing the session's context (todo D3).
/// </summary>
public sealed partial class FullDuplexEndpointTests
{
    /// <summary>
    /// Two runs side by side. A fact run B records reaches run A's next model call, labelled with the
    /// run that recorded it and its context revision; a fact run A builds on a revision that has moved
    /// on (B's message came in after A's task started) is rejected as stale, never written over B's.
    /// Each run's conversation instance joins the pool of instances it runs alongside.
    /// </summary>
    [Fact]
    public async Task Parallel_RunsShareContext_WithRevisions_AndAStaleFactIsRejectedNotOverwritten()
    {
        var holds = new Dictionary<string, TaskCompletionSource>
        {
            ["A-worker"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["B-worker"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["A-supervisor"] = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var entered = holds.Keys.ToDictionary(key => key, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var script = new ScriptedChatClient()
            .WhenPlanner(OneTaskPlan)
            .WhenWorker("完成。\nCONTEXT_PATCH: 端口约定 || 所有服务统一用 5174 端口")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("好了。");
        script.BeforeCall = (instructions, prompt) =>
        {
            var run = prompt.Contains("目标甲", StringComparison.Ordinal) ? "A" : prompt.Contains("目标乙", StringComparison.Ordinal) ? "B" : null;
            var step = instructions.Contains("执行以下任务", StringComparison.Ordinal) ? "worker"
                : instructions.Contains("监督智能体", StringComparison.Ordinal) || prompt.Contains("执行证据", StringComparison.Ordinal) ? "supervisor" : null;
            if (run is null || step is null || !holds.TryGetValue($"{run}-{step}", out var hold)) return Task.CompletedTask;
            entered[$"{run}-{step}"].TrySetResult();
            return hold.Task;
        };
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var lifecycle = factory.Services.GetRequiredService<ILifecycleManager>();

        var runA = StartStreamingInvoke(client, sessionId, new { content = "目标甲", client_message_id = "parallel-a", dispatch_mode = "parallel" });
        var idA = RunIdOf(await runA.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)));
        await entered["A-worker"].Task.WaitAsync(TimeSpan.FromSeconds(30));
        var runB = StartStreamingInvoke(client, sessionId, new { content = "目标乙", client_message_id = "parallel-b", dispatch_mode = "parallel" });
        var idB = RunIdOf(await runB.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)));
        await entered["B-worker"].Task.WaitAsync(TimeSpan.FromSeconds(30));

        // A's task started before B's message moved the session revision: its fact is stale.
        holds["A-worker"].SetResult();
        await entered["A-supervisor"].Task.WaitAsync(TimeSpan.FromSeconds(30));
        // B's fact is built on the current revision and applies.
        holds["B-worker"].SetResult();
        await WaitForAsync(() => EventsOf(lifecycle, sessionId).Any(e => e.EventType == "context.patch.accepted" && e.RunId == idB.ToString()));
        holds["A-supervisor"].SetResult();
        await runA.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        await runB.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        var events = EventsOf(lifecycle, sessionId);
        Assert.Contains(events, e => e.EventType == "context.patch.stale" && e.RunId == idA.ToString());
        Assert.Contains(events, e => e.EventType == "context.patch.accepted" && e.RunId == idB.ToString());

        // A's answer was written with B's fact in view, labelled with B and the revision it applied at.
        var label = "[run " + idB.ToString("N")[..8] + " · rev ";
        Assert.Contains(script.Instructions, instructions => instructions.Contains("You are 'meeting'", StringComparison.Ordinal)
            && instructions.Contains(label, StringComparison.Ordinal) && instructions.Contains("所有服务统一用 5174 端口", StringComparison.Ordinal));

        // B started while A worked, so B's conversation instance joined a pool with A's.
        var poolA = await PoolOfAsync(lifecycle, idA);
        var poolB = await PoolOfAsync(lifecycle, idB);
        Assert.Equal(poolA.Identity, poolA.Main);
        Assert.Equal(poolB.Identity, poolB.Main);
        Assert.Contains(poolA.Identity, poolB.Pool);
        Assert.Contains(poolB.Identity, poolB.Pool);
        Assert.Contains(events, e => e.EventType == "conversation.pool_joined" && e.RunId == idB.ToString());
    }

    private static List<TinadecCore.Contracts.Events.EventEnvelope> EventsOf(ILifecycleManager lifecycle, Guid sessionId) =>
        lifecycle.ReplayEventsAsync(sessionId, 0).GetAwaiter().GetResult().ToList();

    private static async Task<(Guid Identity, Guid Main, Guid[] Pool)> PoolOfAsync(ILifecycleManager lifecycle, Guid runId)
    {
        var stored = await lifecycle.GetCurrentRunCheckpointAsync(runId.ToString());
        using var document = JsonDocument.Parse(stored!.Content);
        JsonElement Field(string pascal) => document.RootElement.EnumerateObject()
            .First(property => string.Equals(property.Name.Replace("_", ""), pascal, StringComparison.OrdinalIgnoreCase)).Value;
        return (Field("ConversationIdentityInstanceId").GetGuid(), Field("MainInstanceId").GetGuid(),
            Field("InstancePoolIds").EnumerateArray().Select(item => item.GetGuid()).ToArray());
    }
}
