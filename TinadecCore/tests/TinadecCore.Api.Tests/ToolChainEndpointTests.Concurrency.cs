using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Api.Tests;

public sealed partial class ToolChainEndpointTests
{
    [Fact]
    public async Task IndependentToolWorkers_OverlapProviderCalls_AndMergeAllUsage()
    {
        var workspace = Path.Combine(_root, "parallel-readers");
        Directory.CreateDirectory(workspace);
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var provider = new FakeToolProvider
        {
            Hold = async _ =>
            {
                if (Interlocked.Increment(ref entered) == 2) bothEntered.TrySetResult();
                await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
        };
        var script = new ToolScriptedClient { UsageTokensPerTurn = 7 }
            .WhenPlanner("[{\"task_key\":\"a\",\"title\":\"Read A\",\"success_criteria\":[\"read\"],\"dependencies\":[],\"required_tools\":[\"read_file\"],\"risk\":\"low\"},{\"task_key\":\"b\",\"title\":\"Read B\",\"success_criteria\":[\"read\"],\"dependencies\":[],\"required_tools\":[\"read_file\"],\"risk\":\"low\"}]")
            .WhenTaskWorkerTurns("Read A",
                [new FunctionCallContent("a", "read_file", new Dictionary<string, object?> { ["filepath"] = "a.txt" })],
                [new TextContent("Read A completed")])
            .WhenTaskWorkerTurns("Read B",
                [new FunctionCallContent("b", "read_file", new Dictionary<string, object?> { ["filepath"] = "b.txt" })],
                [new TextContent("Read B completed")])
            .WhenMeeting("Both read.");
        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        await InstallToolChainPackAsync(client);
        var (sessionId, runId, active) = await StartRunAsync(client, workspace, "parallel-read", "Read both files");
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.True(bothEntered.Task.IsCompletedSuccessfully, "Both provider calls must enter before either finishes.");
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(4, script.WorkerCalls);
        var done = Assert.Single(chunks, chunk => KindOf(chunk) == "done");
        Assert.Equal(28, done.GetProperty("usage").GetProperty("input_tokens").GetInt64());
        var lifecycle = _factory.Services.GetRequiredService<ILifecycleManager>();
        var checkpoint = await lifecycle.GetCurrentRunCheckpointAsync(runId.ToString());
        using var body = JsonDocument.Parse(checkpoint!.Content);
        Assert.Equal(2, body.RootElement.GetProperty("tasks").GetArrayLength());
    }
}
