using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Lifecycle;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Hard insert end to end (todo D2): the steering cuts off what the working agent is doing, and the
/// agent's next turn reads it — with a record of the round that was cut off.
/// </summary>
public sealed partial class ToolChainEndpointTests
{
    /// <summary>
    /// A hard insert while the solo master waits on its model: that call is cut off, the round leaves
    /// a note the master reads next turn (it was interrupted, and by what), and the steering is also
    /// in the context of every later model call of the run.
    /// </summary>
    [Fact]
    public async Task HardInsert_CutsOffTheModelCall_AndTheNextTurnReadsTheSteering()
    {
        var (client, sessionId, script, _) = await StartInterruptScenarioAsync("interrupt-model",
            new ToolScriptedClient().WhenWorkerTurns([new TextContent("按新的要求只做了只读检查。")]).WhenMeeting("已按新要求完成。"));
        var firstTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The first turn only ends when something cuts it off.
        script.BeforeWorkerTurn = turn =>
        {
            if (turn != 1) return Task.CompletedTask;
            firstTurn.TrySetResult();
            return Task.Delay(Timeout.Infinite);
        };
        var active = StartStreamingInvoke(client, sessionId, new { content = "检查项目并修改配置", client_message_id = "interrupt-model-1", permission_mode = "full-access" });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        await firstTurn.Task.WaitAsync(TimeSpan.FromSeconds(60));

        var receipt = await HardInsertAsync(client, sessionId, runId, "STEER-7 不要改配置，只读检查");
        Assert.Equal(1, receipt.GetProperty("interrupted_model_calls").GetInt32());

        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("done", KindOf(Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error")));

        // The cut-off turn never got past its gate; the next one read the note and the steering.
        Assert.Equal(2, script.WorkerCalls);
        var next = Assert.Single(script.WorkerPrompts);
        Assert.Contains("[用户打断]", next);
        Assert.Contains("STEER-7 不要改配置，只读检查", next);
        Assert.Contains("STEER-7 不要改配置，只读检查", Assert.Single(script.WorkerInstructions));

        var events = await RunEventsAsync(sessionId, runId);
        var interrupted = PayloadOf(Assert.Single(events, e => e.EventType == "worker.interrupted"));
        Assert.Equal("model_call", interrupted.GetProperty("during").GetString());
        Assert.Single(events, e => e.EventType == "interaction.interrupt_requested");
        Assert.Contains(events, e => e.EventType == "model.output.interrupted");
        Assert.DoesNotContain(events, e => e.EventType == "model.output.failed");

        // On the model ledger the call was cut off, not failed.
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync();
        Assert.Contains(await db.ModelInvocations.AsNoTracking().Where(x => x.RunId == runId).ToListAsync(),
            invocation => invocation.Status == "cancelled" && invocation.ErrorCategory == "interrupted");
    }

    /// <summary>
    /// A hard insert while a tool call runs: that call finishes — its outcome must stay known — and the
    /// call the model asked for after it never starts. The master is told both.
    /// </summary>
    [Fact]
    public async Task HardInsert_DuringAToolCall_LetsItFinish_AndDoesNotRunTheCallsNotStarted()
    {
        var (client, sessionId, script, provider) = await StartInterruptScenarioAsync("interrupt-tool",
            new ToolScriptedClient()
                .WhenWorkerTurns(
                    [
                        new FunctionCallContent("call-a", "read_file", new Dictionary<string, object?> { ["filepath"] = "a.txt" }),
                        new FunctionCallContent("call-b", "read_file", new Dictionary<string, object?> { ["filepath"] = "b.txt" })
                    ],
                    [new TextContent("只读了 a.txt，按新要求停下。")])
                .WhenMeeting("按新要求停下。"));
        var inCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Hold = _ =>
        {
            inCall.TrySetResult();
            return release.Task;
        };
        var active = StartStreamingInvoke(client, sessionId, new { content = "读 a 和 b 两个文件", client_message_id = "interrupt-tool-1", permission_mode = "full-access" });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        await inCall.Task.WaitAsync(TimeSpan.FromSeconds(60));

        var receipt = await HardInsertAsync(client, sessionId, runId, "STEER-8 b 不用读了");
        // No model call was in flight: the running tool call is left to finish.
        Assert.Equal(0, receipt.GetProperty("interrupted_model_calls").GetInt32());
        release.TrySetResult();

        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("done", KindOf(Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error")));
        Assert.Equal(["read_file"], provider.ReceivedToolIds.ToArray());
        Assert.Contains(script.WorkerToolResults, result => result.Contains("\"not_run\"", StringComparison.Ordinal));
        Assert.Equal(2, script.WorkerPrompts.Count);
        Assert.Contains("[用户打断]", script.WorkerPrompts[1]);
        Assert.Contains("STEER-8 b 不用读了", script.WorkerPrompts[1]);
        Assert.Contains("1 个工具调用没有执行", script.WorkerPrompts[1]);

        var events = await RunEventsAsync(sessionId, runId);
        var interrupted = PayloadOf(Assert.Single(events, e => e.EventType == "worker.interrupted"));
        Assert.Equal("tool_calls", interrupted.GetProperty("during").GetString());
        Assert.Equal(["read_file"], interrupted.GetProperty("skipped_tools").EnumerateArray().Select(tool => tool.GetString()!).ToArray());
    }

    private async Task<(HttpClient Client, Guid SessionId, ToolScriptedClient Script, FakeToolProvider Provider)> StartInterruptScenarioAsync(string name, ToolScriptedClient script)
    {
        var workspace = Path.Combine(_root, "workspace-" + name);
        Directory.CreateDirectory(workspace);
        var provider = new FakeToolProvider();
        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        await InstallGraphSeedPackAsync(client);
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = name + " project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = name + " session", mode_version_id = await LatestPublishedModeVersionIdAsync("solo") })).Content.ReadFromJsonAsync<JsonElement>();
        return (client, session.GetProperty("id").GetGuid(), script, provider);
    }

    private static async Task<JsonElement> HardInsertAsync(HttpClient client, Guid sessionId, Guid runId, string content)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new
        {
            content,
            client_message_id = "steer-" + Guid.NewGuid().ToString("N"),
            dispatch_mode = "insert",
            target_run_id = runId,
            interrupt = true
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("steering_injected", receipt.GetProperty("status").GetString());
        Assert.True(receipt.GetProperty("interrupt").GetBoolean());
        return receipt;
    }

    private async Task<List<TinadecCore.Contracts.Events.EventEnvelope>> RunEventsAsync(Guid sessionId, Guid runId) =>
        (await _factory.Services.GetRequiredService<ILifecycleManager>().ReplayEventsAsync(sessionId, 0))
            .Where(e => string.Equals(e.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();

    private static JsonElement PayloadOf(TinadecCore.Contracts.Events.EventEnvelope envelope) => (JsonElement)envelope.Payload["payload"]!;
}
