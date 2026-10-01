using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Lifecycle;

namespace TinadecCore.Api.Tests;

/// <summary>Delivery semantics (todo D1): queued means after what the session is doing, never alongside it.</summary>
public sealed partial class FullDuplexEndpointTests
{
    /// <summary>
    /// Two messages sent while a run works are queued behind it — the limit would have admitted a
    /// second run, and no second run is made. When the run ends only the head of the queue is
    /// admitted; the rest moves to wait behind it, so the three runs happen strictly one after another.
    /// </summary>
    [Fact]
    public async Task Queued_NeverRunsAlongsideTheActiveRun_AndTheQueueDrainsInOrder()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"任务\",\"description\":\"\",\"success_criteria\":[\"完成\"],\"dependencies\":[],\"required_capabilities\":[],\"priority\":1,\"risk\":\"low\"}]")
            .WhenWorker("完成")
            .WhenSupervisor("{\"decision\":\"pass\",\"reasons\":[],\"revise_task_indexes\":[]}")
            .WhenMeeting("好了。");
        script.BeforeWorker = gate.Task;
        var factory = CreateFactory(script);
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var first = StartStreamingInvoke(client, sessionId, new { content = "任务一", client_message_id = "queue-1" });
        var firstRun = RunIdOf(await first.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30)));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (script.WorkerGateEntries < 1 && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);

        var second = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new { content = "任务二", client_message_id = "queue-2", dispatch_mode = "queued" });
        var third = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new { content = "任务三", client_message_id = "queue-3", dispatch_mode = "queued" });
        foreach (var response in new[] { second, third })
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("queued", body.GetProperty("status").GetString());
            Assert.Equal(firstRun, body.GetProperty("run_id").GetGuid());
        }
        var lifecycleDb = factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();
        await using (var db = await lifecycleDb.CreateDbContextAsync())
            Assert.Equal(1, await db.Runs.CountAsync(run => run.SessionId == sessionId));

        gate.SetResult();
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        // All three run, one after another.
        List<RunRecord> runs = [];
        deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var db = await lifecycleDb.CreateDbContextAsync();
            runs = (await db.Runs.AsNoTracking().Where(run => run.SessionId == sessionId).ToListAsync()).OrderBy(run => run.CreatedAt).ToList();
            if (runs.Count == 3 && runs.All(run => run.Status is "completed" or "failed" or "cancelled")) break;
            await Task.Delay(200);
        }
        Assert.Equal(3, runs.Count);
        Assert.All(runs, run => Assert.Equal("completed", run.Status));
        Assert.True(runs[1].CreatedAt >= runs[0].CompletedAt, "The second run started before the first finished.");
        Assert.True(runs[2].CreatedAt >= runs[1].CompletedAt, "The third run started before the second finished.");

        // In the order the messages were sent.
        var messages = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/messages");
        string ContentOf(Guid messageId) => messages!.Single(message => message.GetProperty("id").GetGuid() == messageId).GetProperty("content").GetString()!;
        Assert.Equal(["任务一", "任务二", "任务三"], runs.Select(run => ContentOf(run.TriggerMessageId)).ToArray());

        // The queue said where it went: after the first run, the rest waited behind the second.
        var events = await factory.Services.GetRequiredService<ILifecycleManager>().ReplayEventsAsync(sessionId, 0);
        var moved = Assert.Single(events, e => e.EventType == "interaction.queue_moved");
        Assert.Equal(runs[1].Id.ToString(), ((JsonElement)moved.Payload["payload"]!).GetProperty("behind_run_id").GetString());
    }
}
