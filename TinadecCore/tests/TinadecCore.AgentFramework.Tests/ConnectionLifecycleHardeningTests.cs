using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;
using TinadecCore.Models.Harness;

namespace TinadecCore.AgentFramework.Tests;

public sealed class ConnectionLifecycleHardeningTests
{
    [Fact]
    public async Task TinadecBranding_CreateLongLivedClient_SharesPoolAcrossDisposals()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var baseUri = new Uri($"http://127.0.0.1:{port}");

        var serverTask = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var tcp = await listener.AcceptTcpClientAsync();
                await using var stream = tcp.GetStream();
                var buf = new byte[1024];
                _ = await stream.ReadAsync(buf);
                var body = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
                await stream.WriteAsync(body);
            }
        });

        var client1 = TinadecBranding.CreateLongLivedClient(baseUri);
        Assert.Equal("ok", await client1.GetStringAsync("/one"));
        client1.Dispose();

        using var client2 = TinadecBranding.CreateLongLivedClient(baseUri);
        Assert.Equal("ok", await client2.GetStringAsync("/two"));
        await serverTask;
    }

    [Fact]
    public async Task OpenCodeChatClient_Dispose_BoundsUnresponsiveAbortRequest()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var acceptedStall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            // 1) POST /session -> create session "sess-1"
            using (var tcp1 = await listener.AcceptTcpClientAsync(serverCts.Token))
            await using (var s1 = tcp1.GetStream())
            {
                var buf = new byte[2048];
                _ = await s1.ReadAsync(buf, serverCts.Token);
                var json = "{\"id\":\"sess-1\"}";
                var resp = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\n\r\n{json}";
                await s1.WriteAsync(Encoding.UTF8.GetBytes(resp), serverCts.Token);
            }

            // 2) POST /session/sess-1/message -> SSE stream with immediate EOF
            using (var tcp2 = await listener.AcceptTcpClientAsync(serverCts.Token))
            await using (var s2 = tcp2.GetStream())
            {
                var buf = new byte[2048];
                _ = await s2.ReadAsync(buf, serverCts.Token);
                var resp = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await s2.WriteAsync(Encoding.UTF8.GetBytes(resp), serverCts.Token);
            }

            // 3) POST /session/sess-1/abort during Dispose -> accept and stall headers intentionally
            using var tcp3 = await listener.AcceptTcpClientAsync(serverCts.Token);
            acceptedStall.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(8), serverCts.Token).ContinueWith(_ => { });
        });

        var client = new OpenCodeChatClient($"http://127.0.0.1:{port}", null, NullLogger<OpenCodeChatClient>.Instance);
        _ = await client.GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hi")], cancellationToken: serverCts.Token);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        client.Dispose();
        sw.Stop();

        Assert.True(acceptedStall.Task.IsCompleted);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Dispose took {sw.Elapsed.TotalSeconds:F2}s, expected bounded < 5s");
        await serverCts.CancelAsync();
        await serverTask.ContinueWith(_ => { });
    }

    [Fact]
    public async Task FollowAsync_SkipsPerBatchGetRunStateReadsOnLiveStream_AndDoesNotSpinOnDeferredDone()
    {
        var runId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var lifecycle = new InstrumentedRunStreamLifecycle(runId, turnId);
        var projection = new RunStreamProjection();
        var coordinator = new FullDuplexRunCoordinator(
            conversations: null!,
            lifecycle: lifecycle,
            configurationResolver: null!,
            toolManifestResolver: null!,
            engine: null!,
            runToolCancellations: [],
            projection: projection);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var chunks = new List<RunStreamChunk>();
            var reading = Task.Run(async () =>
            {
                await foreach (var c in coordinator.FollowAsync(runId, cancellationToken: cts.Token))
                {
                    chunks.Add(c);
                }
            }, cts.Token);

            // Push 10 live frames in sequence via lifecycle -> IRunStreamPublicationSource -> projection
            for (var i = 1; i <= 10; i++)
            {
                await lifecycle.AppendRunStreamAsync(runId.ToString(), new DurableRunStreamAppend(turnId, "answer.delta", Delta: $"tok-{i}"), cts.Token);
                await Task.Delay(5, cts.Token);
            }

            var readsBeforeTerminal = lifecycle.GetRunStateCount;
            Assert.True(readsBeforeTerminal <= 4, $"Expected live stream to skip per-chunk GetRunStateAsync calls, saw {readsBeforeTerminal} for 10 chunks.");

            // Emit "done" while status is still Reviewing -> deferred, then flip Completed after 90ms
            await lifecycle.AppendRunStreamAsync(runId.ToString(), new DurableRunStreamAppend(turnId, "done", FinishReason: "completed"), cts.Token);
            await Task.Delay(90, cts.Token);
            var readsDuringDeferral = lifecycle.GetRunStateCount - readsBeforeTerminal;
            Assert.True(readsDuringDeferral <= 12, $"Deferred done busy-spun GetRunStateAsync: {readsDuringDeferral} calls in 90ms.");

            lifecycle.Status = RunStatus.Completed;
            await reading;

            Assert.Equal(11, chunks.Count);
            Assert.Equal("done", chunks[^1].Kind);
        }
        finally
        {
            coordinator.Dispose();
        }
    }

    private sealed class InstrumentedRunStreamLifecycle(Guid runId, Guid turnId) : ILifecycleManager, IRunStreamPublicationSource
    {
        private readonly object _gate = new();
        private readonly List<DurableRunStreamChunk> _chunks = [];
        private readonly List<Action<Guid, DurableRunStreamChunk>> _listeners = [];
        public RunStatus Status { get; set; } = RunStatus.Executing;
        public int GetRunStateCount { get; private set; }

        public IDisposable SubscribeRunStream(Action<Guid, DurableRunStreamChunk> listener)
        {
            lock (_gate) _listeners.Add(listener);
            return new Unsub(_gate, _listeners, listener);
        }

        public Task<RunState> GetRunStateAsync(string id, CancellationToken ct = default)
        {
            GetRunStateCount++;
            return Task.FromResult(new RunState { RunId = runId.ToString(), TurnId = turnId.ToString(), Status = Status });
        }

        public Task<DurableRunStreamChunk> AppendRunStreamAsync(string id, DurableRunStreamAppend item, CancellationToken ct = default)
        {
            DurableRunStreamChunk chunk;
            Action<Guid, DurableRunStreamChunk>[] snapshot;
            lock (_gate)
            {
                var seq = _chunks.Count + 1L;
                chunk = new DurableRunStreamChunk(runId.ToString(), item.TurnId, item.MessageId, seq, item.Kind, item.Delta, item.UsageJson, item.FinishReason, item.ErrorCategory, item.SafeErrorMessage, DateTimeOffset.UtcNow);
                _chunks.Add(chunk);
                snapshot = _listeners.ToArray();
            }
            foreach (var l in snapshot) l(runId, chunk);
            return Task.FromResult(chunk);
        }

        public Task<IReadOnlyList<DurableRunStreamChunk>> ReplayRunStreamAsync(string id, Guid? tid, long afterSequence, CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<DurableRunStreamChunk>>(
                    _chunks.Where(c => c.Sequence > afterSequence).ToArray());
            }
        }

        private sealed class Unsub(object gate, List<Action<Guid, DurableRunStreamChunk>> list, Action<Guid, DurableRunStreamChunk> item) : IDisposable
        {
            public void Dispose() { lock (gate) list.Remove(item); }
        }

        public Task<string> StartRunAsync(string sessionId, string? triggerMessageId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> StartRunAsync(RunStartRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RunStartResult> StartOrGetRunAsync(RunStartRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RunState?> FindRunByTriggerMessageAsync(string sessionId, string triggerMessageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteRunAsync(string runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetRunStatusAsync(string runId, string status, string? summary = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> CountActiveRunsAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AdvanceRunContextRevisionAsync(string runId, long contextRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> AppendEventAsync(Guid runId, string eventType, object? payload, string summary, string severity = "info", Guid? taskId = null, Guid? approvalId = null, string? toolId = null, CancellationToken cancellationToken = default, string? idempotencyKey = null) => throw new NotSupportedException();
        public Task<string> StartToolExecutionAsync(ToolExecutionStart start, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteToolExecutionAsync(string executionId, ToolExecutionCompletion completion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task FailToolExecutionAsync(string executionId, string errorCategory, string safeMessage, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RunState>> ListNonTerminalRunsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RunState>> ListActiveRunsAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RunState>> ListLeaseEligibleRunsAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FrozenRunConfiguration> FreezeRunConfigurationAsync(string runId, FrozenRunConfigurationWrite configuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RunCheckpoint> SaveRunCheckpointAsync(string runId, RunCheckpointWrite checkpoint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RunCheckpoint?> GetCurrentRunCheckpointAsync(string runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RunDirective>> ListPendingRunDirectivesAsync(Guid runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RunDirectiveDrainResult> DrainRunDirectivesAsync(Guid runId, IReadOnlyList<Guid> directiveIds, string drainedStatus, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RequeueRunDirectivesAsync(Guid fromRunId, IReadOnlyList<Guid> directiveIds, Guid toRunId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RunDirective> EnqueueRunDirectiveAsync(RunDirectiveWrite write, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RunLease> TryAcquireRunLeaseAsync(string runId, string ownerId, TimeSpan duration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> HeartbeatRunLeaseAsync(string runId, string ownerId, TimeSpan duration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReleaseRunLeaseAsync(string runId, string ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateTaskSnapshotAsync(Guid runId, object taskNode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FrozenRunConfiguration?> GetFrozenRunConfigurationAsync(string runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Contracts.Events.EventEnvelope>> ReplayEventsAsync(Guid? sessionId, long afterSequence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
