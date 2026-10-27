using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Api.Tests;

public sealed partial class ToolChainEndpointTests
{
    /// <summary>
    /// A result larger than the inline limit stays on its execution row. The checkpoint, which every
    /// later save rewrites in full, holds a bounded head; the master's next turn still reads the
    /// whole result.
    /// </summary>
    [Fact]
    public async Task LargeToolResult_StaysOnItsExecution_AndTheMasterStillReadsItWhole()
    {
        var (client, sessionId, script, provider) = await StartInterruptScenarioAsync("large-result",
            new ToolScriptedClient()
                .WhenWorkerTurns(
                    [new FunctionCallContent("call-big", "read_file", new Dictionary<string, object?> { ["filepath"] = "big.json" })],
                    [new TextContent("读完了。")])
                .WhenMeeting("读完了。"));
        var content = new string('x', 64 * 1024) + "-END-OF-RESULT";
        provider.ResultFor = (_, request) => request.ToolId == "read_file" ? new { success = true, content } : null;

        var active = StartStreamingInvoke(client, sessionId, new { content = "读 big.json", client_message_id = "large-result-1" });
        var runId = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("done", KindOf(Assert.Single(chunks, chunk => KindOf(chunk) is "done" or "error")));

        // The model was handed the whole result on its next turn.
        Assert.Contains(script.WorkerToolResults, result => result.Contains("-END-OF-RESULT", StringComparison.Ordinal));

        var checkpoint = await _factory!.Services.GetRequiredService<ILifecycleManager>().GetCurrentRunCheckpointAsync(runId.ToString());
        Assert.NotNull(checkpoint);
        using var document = JsonDocument.Parse(checkpoint.Content);
        var turn = document.RootElement.GetProperty("tasks").EnumerateArray()
            .SelectMany(task => task.GetProperty("toolTurns").EnumerateArray())
            .Single(item => item.GetProperty("callId").GetString() == "call-big");
        Assert.True(turn.GetProperty("resultOnExecution").GetBoolean());
        var stored = turn.GetProperty("resultJson").GetString()!;
        Assert.True(stored.Length < 4096, $"the checkpoint kept {stored.Length} characters of the result");
        Assert.Contains(turn.GetProperty("executionId").GetString()!, stored);
        Assert.DoesNotContain("-END-OF-RESULT", checkpoint.Content);
    }
}
