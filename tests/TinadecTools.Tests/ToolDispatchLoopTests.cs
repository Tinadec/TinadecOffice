using System.Text.Json;
using TinadecTools.Abstractions;
using TinadecTools.Runtime;

namespace TinadecTools.Tests;

public sealed class ToolDispatchLoopTests
{
    private static long _nextCallId = 80_000;

    private static long NextCallId() => Interlocked.Increment(ref _nextCallId);

    private static string RequestLine(long callId, string toolId) =>
        JsonSerializer.Serialize(new ToolCallRequest<JsonElement>
        {
            ToolId = toolId,
            SessionId = "loop-test",
            ToolCallId = callId,
            Approved = true,
            // notnull-constrained TParams? is a plain JsonElement here; a default
            // (undefined) JsonElement cannot be serialized.
            Params = JsonDocument.Parse("{}").RootElement.Clone()
        }, ToolCallJsonContext.Default.ToolCallRequestJsonElement);

    private static ToolCallResponse<JsonElement> OkResponse(long callId) => new()
    {
        CallId = callId,
        IsSuccess = true,
        Response = JsonSerializer.SerializeToElement("ok", ToolCallJsonContext.Default.String)
    };

    private static string ReadOutput(StringWriter output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }

    [Fact]
    public async Task RunAsync_ControlPlaneRespondsWhileMutatingCallIsBlocked()
    {
        var blockingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ToolRegistry.Register("test.loop-blocking-mut", async (req, _) =>
        {
            blockingStarted.TrySetResult();
            await releaseBlocking.Task;
            return OkResponse(req.ToolCallId);
        }, requiresApproval: false, mutatesWorkspace: true);
        ToolRegistry.Register("#test.loop-control",
            (req, _) => ValueTask.FromResult(OkResponse(req.ToolCallId)),
            requiresApproval: false, mutatesWorkspace: false);

        var blockingId = NextCallId();
        var controlId = NextCallId();
        var input = new StringReader(
            RequestLine(blockingId, "test.loop-blocking-mut") + "\n" +
            RequestLine(controlId, "#test.loop-control") + "\n");
        var output = new StringWriter();

        var loop = ToolDispatchLoop.RunAsync(input, output);
        await blockingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The control-plane call must get its response while the mutating call is
        // still blocked — this is what keeps #terminal stdin/kill responsive during
        // a long-running shell command.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!ReadOutput(output).Contains($"\"call_id\":{controlId}"))
        {
            if (DateTime.UtcNow > deadline)
            {
                releaseBlocking.TrySetResult();
                Assert.Fail("Control-plane response was head-of-line blocked by the mutating call.");
            }
            await Task.Delay(25);
        }
        Assert.DoesNotContain($"\"call_id\":{blockingId}", ReadOutput(output));

        releaseBlocking.TrySetResult();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains($"\"call_id\":{blockingId}", ReadOutput(output));
    }

    [Fact]
    public async Task RunAsync_MutatingCallsStillSerialize()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = 0;
        ToolRegistry.Register("test.loop-mut-a", async (req, _) =>
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task;
            return OkResponse(req.ToolCallId);
        }, requiresApproval: false, mutatesWorkspace: true);
        ToolRegistry.Register("test.loop-mut-b", (req, _) =>
        {
            Interlocked.Increment(ref secondEntered);
            return ValueTask.FromResult(OkResponse(req.ToolCallId));
        }, requiresApproval: false, mutatesWorkspace: true);

        var firstId = NextCallId();
        var secondId = NextCallId();
        var input = new StringReader(
            RequestLine(firstId, "test.loop-mut-a") + "\n" +
            RequestLine(secondId, "test.loop-mut-b") + "\n");
        var output = new StringWriter();

        var loop = ToolDispatchLoop.RunAsync(input, output);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Give the second call a window to (incorrectly) enter while the first holds the gate.
        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref secondEntered));

        releaseFirst.TrySetResult();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, Volatile.Read(ref secondEntered));
        var text = ReadOutput(output);
        Assert.Contains($"\"call_id\":{firstId}", text);
        Assert.Contains($"\"call_id\":{secondId}", text);
    }

    [Fact]
    public async Task RunAsync_WritesOneResponseLinePerRequest_IncludingErrors()
    {
        ToolRegistry.Register("test.loop-fast-read",
            (req, _) => ValueTask.FromResult(OkResponse(req.ToolCallId)),
            requiresApproval: false, mutatesWorkspace: false);

        var unknownId = NextCallId();
        var fastId = NextCallId();
        var input = new StringReader(
            RequestLine(unknownId, "test.loop-no-such-tool") + "\n" +
            "{not json\n" +
            RequestLine(fastId, "test.loop-fast-read") + "\n");
        var output = new StringWriter();

        await ToolDispatchLoop.RunAsync(input, output).WaitAsync(TimeSpan.FromSeconds(10));

        var lines = ReadOutput(output)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, lines.Length);

        var unknown = lines.Select(l => JsonDocument.Parse(l)).Single(d => d.RootElement.GetProperty("call_id").GetInt64() == unknownId);
        Assert.False(unknown.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("Unknown tool", unknown.RootElement.GetProperty("error").GetString());

        var malformed = lines.Select(l => JsonDocument.Parse(l)).Single(d => d.RootElement.GetProperty("call_id").GetInt64() == -1);
        Assert.False(malformed.RootElement.GetProperty("success").GetBoolean());

        var fast = lines.Select(l => JsonDocument.Parse(l)).Single(d => d.RootElement.GetProperty("call_id").GetInt64() == fastId);
        Assert.True(fast.RootElement.GetProperty("success").GetBoolean());
    }
}
