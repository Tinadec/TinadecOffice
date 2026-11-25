using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AcpServer;
using TinadecCore.AspNetCore;
using TinadecCore.AspNetCore.Auth;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Models.Harness.Acp;
using TinadecCore.Persistence;
using TinadecCore.Runtime;

namespace TinadecCore.Api.Tests.AcpServer;

/// <summary>
/// Shared in-memory duplex byte pipes, wire-level JSON-RPC client recorder, deterministic model/tool
/// stubs, Kestrel loopback Mode B Core host, and OpenAI SSE loopback server for Self-Dogfood child-process tests.
/// </summary>
internal sealed class ChannelByteStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private readonly object _recordGate = new();
    private readonly MemoryStream _recordedBytes = new();
    private byte[] _currentBuffer = [];
    private int _currentOffset;
    private bool _eof;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public byte[] GetRecordedBytes()
    {
        lock (_recordGate)
        {
            return _recordedBytes.ToArray();
        }
    }

    public void CompleteWriter() => _chunks.Writer.TryComplete();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0 || _eof) return 0;

        while (_currentOffset >= _currentBuffer.Length)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _eof = true;
                return 0;
            }

            if (_chunks.Reader.TryRead(out var next) && next.Length > 0)
            {
                _currentBuffer = next;
                _currentOffset = 0;
                break;
            }
        }

        var toCopy = Math.Min(buffer.Length, _currentBuffer.Length - _currentOffset);
        _currentBuffer.AsMemory(_currentOffset, toCopy).CopyTo(buffer);
        _currentOffset += toCopy;
        return toCopy;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return ValueTask.CompletedTask;
        var copy = buffer.ToArray();
        lock (_recordGate)
        {
            _recordedBytes.Write(copy, 0, copy.Length);
        }

        if (!_chunks.Writer.TryWrite(copy))
        {
            throw new IOException("Pipe writer has been closed.");
        }

        return ValueTask.CompletedTask;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _chunks.Writer.TryComplete();
        }

        base.Dispose(disposing);
    }
}

internal sealed class DuplexAcpPipes : IDisposable
{
    public ChannelByteStream ClientToServerStdin { get; } = new();
    public ChannelByteStream ServerToClientStdout { get; } = new();

    public void CloseClientStdin() => ClientToServerStdin.CompleteWriter();

    public void Dispose()
    {
        ClientToServerStdin.CompleteWriter();
        ServerToClientStdout.CompleteWriter();
        ClientToServerStdin.Dispose();
        ServerToClientStdout.Dispose();
    }
}

internal sealed record RecordedWireEvent(
    long Order,
    string Category,
    string Detail,
    JsonElement Payload);

internal sealed class RecordingAcpWireClient : IAsyncDisposable
{
    private readonly DuplexAcpPipes _pipes;
    private readonly ClientPipeTransport _transport;
    private readonly JsonRpcConnection _rpc;
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _pumpTask;
    private readonly ConcurrentQueue<RecordedWireEvent> _timeline = new();
    private readonly ConcurrentQueue<JsonElement> _sessionUpdates = new();
    private readonly ConcurrentQueue<JsonElement> _permissionRequests = new();
    private long _sequence;

    public Func<JsonElement, CancellationToken, Task<JsonElement>>? OnPermissionRequest { get; set; }

    public RecordingAcpWireClient(DuplexAcpPipes pipes)
    {
        _pipes = pipes;
        _transport = new ClientPipeTransport(pipes.ClientToServerStdin, pipes.ServerToClientStdout);
        _rpc = new JsonRpcConnection(_transport, dispatchRequestsConcurrently: true);
        _rpc.OnMethodNotification = (method, @params) =>
        {
            var clone = @params.Clone();
            var detail = method;
            if (method == "session/update"
                && clone.ValueKind == JsonValueKind.Object
                && clone.TryGetProperty("update", out var upd)
                && upd.ValueKind == JsonValueKind.Object
                && upd.TryGetProperty("sessionUpdate", out var kindEl))
            {
                detail = $"session/update:{kindEl.GetString()}";
                _sessionUpdates.Enqueue(clone);
            }

            _timeline.Enqueue(new RecordedWireEvent(Interlocked.Increment(ref _sequence), "notification", detail, clone));
        };

        _rpc.OnRequest = async (method, @params, ct) =>
        {
            var clone = @params.Clone();
            _timeline.Enqueue(new RecordedWireEvent(Interlocked.Increment(ref _sequence), "server_request", method, clone));
            if (method == "session/request_permission")
            {
                _permissionRequests.Enqueue(clone);
                if (OnPermissionRequest is { } handler)
                {
                    return await handler(clone, ct).ConfigureAwait(false);
                }

                return SelectOptionByKindOrId(clone, "allow_once");
            }

            throw new AcpMethodNotFoundException(method);
        };

        _pumpTask = _rpc.PumpAsync(_pumpCts.Token);
    }

    public IReadOnlyList<RecordedWireEvent> Timeline => _timeline.ToArray();

    public IReadOnlyList<JsonElement> SessionUpdates => _sessionUpdates.ToArray();

    public IReadOnlyList<JsonElement> PermissionRequests => _permissionRequests.ToArray();

    public void ClearRecorded()
    {
        while (_timeline.TryDequeue(out _)) { }
        while (_sessionUpdates.TryDequeue(out _)) { }
        while (_permissionRequests.TryDequeue(out _)) { }
    }

    public async Task<JsonElement> RequestAsync(string method, object? @params, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(90);
        using var cts = new CancellationTokenSource(effectiveTimeout);
        var payload = @params is JsonElement je ? je : AcpWire.ToParams(@params ?? new { });
        try
        {
            var result = await _rpc.RequestAsync(method, payload, effectiveTimeout, cts.Token).ConfigureAwait(false);
            _timeline.Enqueue(new RecordedWireEvent(Interlocked.Increment(ref _sequence), "response", method, result.Clone()));
            return result;
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
        {
            var observed = string.Join(" -> ", _timeline.Select(e => $"{e.Category}:{e.Detail}"));
            throw new TimeoutException($"ACP request '{method}' timed out after {effectiveTimeout.TotalSeconds:F1}s. Recorded wire timeline: [{observed}]", ex);
        }
    }

    public Task NotifyAsync(string method, object? @params)
    {
        var payload = @params is JsonElement je ? je : AcpWire.ToParams(@params ?? new { });
        return _rpc.NotifyAsync(method, payload, CancellationToken.None);
    }

    public static JsonElement SelectOptionByKindOrId(JsonElement permissionParams, string targetKindOrId)
    {
        if (permissionParams.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
        {
            foreach (var opt in opts.EnumerateArray())
            {
                var optionId = opt.TryGetProperty("optionId", out var idEl) ? idEl.GetString() : null;
                var kind = opt.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() : null;
                if (string.Equals(kind, targetKindOrId, StringComparison.Ordinal)
                    || string.Equals(optionId, targetKindOrId, StringComparison.Ordinal))
                {
                    return AcpWire.ToParams(new { outcome = new { outcome = "selected", optionId } });
                }
            }
        }

        return AcpWire.ToParams(new { outcome = new { outcome = "selected", optionId = targetKindOrId } });
    }

    public static JsonElement CancelledOutcome() =>
        AcpWire.ToParams(new { outcome = new { outcome = "cancelled" } });

    public async ValueTask DisposeAsync()
    {
        _pipes.CloseClientStdin();
        _pumpCts.Cancel();
        _transport.SignalExit();
        try { await _pumpTask.ConfigureAwait(false); } catch { }
        _pumpCts.Dispose();
    }

    private sealed class ClientPipeTransport(Stream stdinToServer, Stream stdoutFromServer) : IAcpTransport
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Stream Stdin => stdinToServer;
        public Stream Stdout => stdoutFromServer;
        public Task Exited => _exited.Task;
        public string Description => "in-memory-acp-client-pipe";

        public void SignalExit() => _exited.TrySetResult();
        public void Kill() => _exited.TrySetResult();
        public ValueTask DisposeAsync()
        {
            _exited.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class AcpTestSecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _deleted = new(StringComparer.Ordinal);

    public Task<string> PutAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        _deleted.TryRemove(key, out _);
        _secrets[key] = secret;
        return Task.FromResult(key);
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_secrets.TryGetValue(key, out var explicitValue))
        {
            return Task.FromResult<string?>(explicitValue);
        }

        if (!_deleted.ContainsKey(key) && key.StartsWith("provider-", StringComparison.Ordinal))
        {
            return Task.FromResult<string?>("acp-test-api-key");
        }

        return Task.FromResult<string?>(null);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_secrets.ContainsKey(key) || (!_deleted.ContainsKey(key) && key.StartsWith("provider-", StringComparison.Ordinal)));

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _secrets.TryRemove(key, out _);
        _deleted[key] = 0;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Deterministic in-process model + tool harness used by Mode A and Mode B protocol/integration tests.
/// Supports reasoning chunks, text streaming, low/medium/high-risk tool calls, session plan updates,
/// attachment inspection, cancellation barriers, and stdout/ILogger pollution stress.
/// </summary>
internal sealed class ScriptedAcpTestRig : IAgentChatClientFactory, IToolProvider
{
    private readonly ConcurrentDictionary<string, int> _callCounts = new(StringComparer.Ordinal);

    public IServiceProvider? HostServices { get; set; }
    public bool EmitNoiseLogsAndConsoleWrite { get; set; }
    public TaskCompletionSource? SlowTurnEntered { get; set; }
    public TaskCompletionSource? ReleaseSlowTurn { get; set; }
    public ConcurrentQueue<string> ObservedPrompts { get; } = new();
    public ConcurrentQueue<ToolWireRequestDto> ExecutedTools { get; } = new();

    public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResolution
        {
            IsAvailable = true,
            BaseUrl = "http://127.0.0.1",
            Model = "gpt-4o-mini",
            ApiKey = "acp-test-key",
            ModelId = "openai/gpt-4o-mini"
        });

    public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default) =>
        Task.FromResult<IChatClient>(new ScriptedChatClient(this, resolution.Model ?? "gpt-4o-mini"));

    public Task<ToolManifestDto> EnsureStartedAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
        Task.FromResult(BuildManifest());

    public Task<ToolManifestDto> GetManifestAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
        Task.FromResult(BuildManifest());

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<ToolWireResponseDto> CallAsync(
        string workspaceRoot,
        ToolWireRequestDto request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ExecutedTools.Enqueue(request);
        if (EmitNoiseLogsAndConsoleWrite)
        {
            Console.WriteLine("TOOL_CONSOLE_WRITELINE_NOISE");
            HostServices?.GetService<ILoggerFactory>()?.CreateLogger("AcpNoiseTest")
                .LogError("Simulated error log during tool {ToolId}", request.ToolId);
        }

        if (request.ToolId == "write_file" && request.Params is { ValueKind: JsonValueKind.Object } writeParams)
        {
            var relPath = (writeParams.TryGetProperty("filepath", out var fp) ? fp.GetString()
                : writeParams.TryGetProperty("path", out var p) ? p.GetString() : null) ?? "output.txt";
            var content = writeParams.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty;
            var targetPath = Path.IsPathRooted(relPath) ? relPath : Path.Combine(workspaceRoot, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await File.WriteAllTextAsync(targetPath, content, cancellationToken).ConfigureAwait(false);
            return new ToolWireResponseDto
            {
                CallId = request.ToolCallId,
                IsSuccess = true,
                Result = JsonSerializer.SerializeToElement(new { written = targetPath, bytes = content.Length })
            };
        }

        if (request.ToolId == "read_file" && request.Params is { ValueKind: JsonValueKind.Object } readParams)
        {
            var relPath = (readParams.TryGetProperty("filepath", out var fp) ? fp.GetString()
                : readParams.TryGetProperty("path", out var p) ? p.GetString() : null) ?? "README.md";
            var targetPath = Path.IsPathRooted(relPath) ? relPath : Path.Combine(workspaceRoot, relPath);
            var text = File.Exists(targetPath) ? await File.ReadAllTextAsync(targetPath, cancellationToken).ConfigureAwait(false) : "missing";
            return new ToolWireResponseDto
            {
                CallId = request.ToolCallId,
                IsSuccess = true,
                Result = JsonSerializer.SerializeToElement(new { path = targetPath, content = text })
            };
        }

        if (request.ToolId == "shell" && request.Params is { ValueKind: JsonValueKind.Object } shellParams)
        {
            var cmd = shellParams.TryGetProperty("command", out var c) ? c.GetString() ?? string.Empty : string.Empty;
            return new ToolWireResponseDto
            {
                CallId = request.ToolCallId,
                IsSuccess = true,
                Result = JsonSerializer.SerializeToElement(new { exit_code = 0, stdout = $"executed: {cmd}" })
            };
        }

        return new ToolWireResponseDto
        {
            CallId = request.ToolCallId,
            IsSuccess = true,
            Result = JsonSerializer.SerializeToElement(new { ok = true, tool = request.ToolId })
        };
    }

    private static readonly JsonElement OpenObjectSchema =
        JsonDocument.Parse("""{"type":"object","additionalProperties":true}""").RootElement.Clone();

    private static ToolManifestDto BuildManifest()
    {
        var entries = new List<ToolManifestEntryDto>
        {
            new() { Id = "read_file", Description = "Read workspace file", InputSchema = OpenObjectSchema, RequiresApproval = false, Risk = "low", MutatesWorkspace = false, RetrySafety = "safe" },
            new() { Id = "file_search", Description = "Search files", InputSchema = OpenObjectSchema, RequiresApproval = false, Risk = "low", MutatesWorkspace = false, RetrySafety = "safe" },
            new() { Id = "write_file", Description = "Write workspace file", InputSchema = OpenObjectSchema, RequiresApproval = true, Risk = "medium", MutatesWorkspace = true },
            new() { Id = "delete_line", Description = "Delete lines", InputSchema = OpenObjectSchema, RequiresApproval = true, Risk = "medium", MutatesWorkspace = true },
            new() { Id = "shell", Description = "Execute shell command", InputSchema = OpenObjectSchema, RequiresApproval = true, Risk = "high", MutatesWorkspace = true },
            new() { Id = "web_fetch", Description = "Fetch web URL", InputSchema = OpenObjectSchema, RequiresApproval = true, Risk = "medium", MutatesWorkspace = false }
        };

        return new ToolManifestDto
        {
            ProtocolVersion = 2,
            ManifestHash = ToolManifestHasher.Compute(entries),
            Tools = entries
        };
    }

    private sealed class ScriptedChatClient(ScriptedAcpTestRig rig, string activeModel) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("acp-scripted-chat");
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            var promptView = FlattenMessages(list, options);
            rig.ObservedPrompts.Enqueue(promptView);

            if (rig.EmitNoiseLogsAndConsoleWrite)
            {
                Console.WriteLine("CHAT_CONSOLE_WRITELINE_BANNER");
                var logger = rig.HostServices?.GetService<ILoggerFactory>()?.CreateLogger("AcpNoiseChat");
                logger?.LogWarning("Simulated warning during ACP streaming");
                logger?.LogError("Simulated error during ACP streaming");
            }

            // Supervision review pass if invoked in team/plan mode
            if (promptView.Contains("revise_task_indexes", StringComparison.Ordinal))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    "{\"decision\":\"pass\",\"reasons\":[\"verified\"],\"revise_task_indexes\":[],\"criteria_verdicts\":[]}");
                yield break;
            }

            var hasToolResult = list.Any(m => m.Contents.OfType<FunctionResultContent>().Any());
            var isTaskExecutionPhase = options?.Tools is { Count: > 0 }
                || (options?.Instructions?.Contains(WorkerOutcomeProtocol.Marker, StringComparison.Ordinal) ?? false);
            var activeScenarioView = SliceLatestScenario(promptView);

            if (activeScenarioView.StartsWith("[SCENARIO:SLOW_STREAM]", StringComparison.Ordinal))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("Starting slow analysis...")]);
                rig.SlowTurnEntered?.TrySetResult();
                if (rig.ReleaseSlowTurn is not null)
                {
                    await rig.ReleaseSlowTurn.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    isTaskExecutionPhase ? SoloTaskCompleted("Slow task done.") : "Slow stream finished.");
                yield break;
            }

            if (activeScenarioView.StartsWith("[SCENARIO:FULL_LOOP]", StringComparison.Ordinal))
            {
                if (isTaskExecutionPhase)
                {
                    if (!hasToolResult)
                    {
                        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("Inspecting workspace README and updating plan...")]);
                        await MaybePublishSessionPlanAsync("Inspect README", "in_progress", cancellationToken).ConfigureAwait(false);
                        var callArgs = new Dictionary<string, object?> { ["filepath"] = "README.md" };
                        yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-read-1", "read_file", callArgs)]);
                        yield break;
                    }

                    await MaybePublishSessionPlanAsync("Inspect README", "completed", cancellationToken).ConfigureAwait(false);
                    yield return new ChatResponseUpdate(ChatRole.Assistant, SoloTaskCompleted("README verified."));
                    yield break;
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("Synthesizing final answer...")]);
                yield return new ChatResponseUpdate(ChatRole.Assistant, $"Full loop complete on model={activeModel}: ");
                yield return new ChatResponseUpdate(ChatRole.Assistant, "README verified.");
                yield break;
            }

            if (activeScenarioView.StartsWith("[SCENARIO:WRITE_GATED]", StringComparison.Ordinal))
            {
                if (isTaskExecutionPhase)
                {
                    if (!hasToolResult)
                    {
                        var callArgs = new Dictionary<string, object?>
                        {
                            ["filepath"] = "gated-note.txt",
                            ["content"] = "GOVERNED_WRITE_OK"
                        };
                        yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-write-1", "write_file", callArgs)]);
                        yield break;
                    }

                    var resultSummary = ExtractLatestToolResult(list);
                    yield return new ChatResponseUpdate(ChatRole.Assistant, SoloTaskCompleted($"Write step finished: {resultSummary}"));
                    yield break;
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, "Write tool turn completed.");
                yield break;
            }

            if (activeScenarioView.StartsWith("[SCENARIO:SHELL_PREFIX]", StringComparison.Ordinal))
            {
                if (isTaskExecutionPhase)
                {
                    var turnNo = rig._callCounts.AddOrUpdate("shell", 1, (_, n) => n + 1);
                    if (!hasToolResult)
                    {
                        var cmd = activeScenarioView.Contains("[CMD:SECOND]", StringComparison.Ordinal)
                            ? "git status --short"
                            : "git status";
                        var callArgs = new Dictionary<string, object?> { ["command"] = cmd };
                        yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent($"call-shell-{turnNo}", "shell", callArgs)]);
                        yield break;
                    }

                    yield return new ChatResponseUpdate(ChatRole.Assistant, SoloTaskCompleted($"Shell finished: {ExtractLatestToolResult(list)}"));
                    yield break;
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, "Shell command completed.");
                yield break;
            }

            if (isTaskExecutionPhase)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, SoloTaskCompleted($"Task completed on {activeModel}."));
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("Quick thought.")]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"Echo ({activeModel}): {ExtractLastUserLine(list)}");
        }

        private async Task MaybePublishSessionPlanAsync(string stepTitle, string stepStatus, CancellationToken ct)
        {
            if (rig.HostServices is null) return;
            try
            {
                using var diScope = rig.HostServices.CreateScope();
                var store = diScope.ServiceProvider.GetService<ProjectSessionStore>();
                var planService = diScope.ServiceProvider.GetService<SessionPlanService>();
                var lifecycle = diScope.ServiceProvider.GetService<StorageLifecycleService>();
                if (store is null || planService is null || lifecycle is null) return;

                var sessions = await store.ListSessionsAsync(null, LifecycleStatuses.Active, ct).ConfigureAwait(false);
                var latestSession = sessions.FirstOrDefault();
                if (latestSession is null) return;

                var runs = await lifecycle.ListRunsAsync(latestSession.Id, ct).ConfigureAwait(false);
                var activeRun = runs.FirstOrDefault();

                var planRequest = JsonSerializer.Deserialize<SessionPlanPutRequestDto>(
                    JsonSerializer.Serialize(new
                    {
                        title = "ACP Execution Plan",
                        status = stepStatus == "completed" ? "completed" : "executing",
                        phases = new[]
                        {
                            new
                            {
                                key = "phase-1",
                                title = "Phase 1",
                                steps = new[]
                                {
                                    new
                                    {
                                        key = "step-1",
                                        title = stepTitle,
                                        status = stepStatus
                                    }
                                }
                            }
                        }
                    }),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    })!;

                var updated = await planService.PutAsync(
                    latestSession.Id,
                    planRequest,
                    runId: activeRun?.Id,
                    cancellationToken: ct).ConfigureAwait(false);

                if (activeRun is not null)
                {
                    await lifecycle.AppendEventAsync(
                        activeRun.Id,
                        "session.plan.updated",
                        new { plan = updated },
                        $"Plan step '{stepTitle}' -> {stepStatus}",
                        cancellationToken: ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // Best-effort plan event emission in test rig.
            }
        }

        private static string FlattenMessages(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(options?.Instructions)) sb.AppendLine(options.Instructions);
            foreach (var m in messages)
            {
                sb.AppendLine($"[{m.Role}]: {m.Text}");
                foreach (var fr in m.Contents.OfType<FunctionResultContent>())
                {
                    sb.AppendLine($"[ToolResult {fr.CallId}]: {fr.Result}");
                }
            }

            return sb.ToString();
        }

        private static string SliceLatestScenario(string promptView)
        {
            var idx = promptView.LastIndexOf("[SCENARIO:", StringComparison.Ordinal);
            return idx >= 0 ? promptView[idx..] : string.Empty;
        }

        private static string ExtractLatestToolResult(IReadOnlyList<ChatMessage> messages)
        {
            var last = messages.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).LastOrDefault();
            return last?.Result?.ToString() ?? "no-result";
        }

        private static string ExtractLastUserLine(IReadOnlyList<ChatMessage> messages)
        {
            var userMsg = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
            var lines = userMsg.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.LastOrDefault() ?? userMsg;
        }
    }

    internal const string SoloMasterSuccessCriterion =
        "The goal is satisfied and the answer reports what what actually happened.";

    internal static string SoloTaskCompleted(string summary) =>
        $"{summary}\n{WorkerOutcomeProtocol.CriterionMarker} {SoloMasterSuccessCriterion} || {summary}\n{WorkerOutcomeProtocol.Marker} completed";
}

/// <summary>
/// Real Kestrel loopback TinadecCore HTTP + SSE server for Mode B (<c>--core-url</c>) integration tests.
/// Uses <see cref="ApiTestStorage.CreateRoot"/> so real user <c>~/.tinadec</c> is never touched.
/// </summary>
internal sealed class LoopbackCoreHttpHost : IAsyncDisposable
{
    public const string HostControlHeaderName = "X-Tinadec-Host-Control";

    private readonly WebApplication _app;
    public Uri BaseUri { get; }
    public HttpClient HttpClient { get; }
    public string ExpectedHostToken { get; }
    public ConcurrentQueue<string> ObservedHostTokens { get; } = new();
    public ConcurrentQueue<string> ObservedStorageHeaders { get; } = new();
    public ScriptedAcpTestRig Rig { get; }

    private LoopbackCoreHttpHost(WebApplication app, Uri baseUri, string expectedToken, ScriptedAcpTestRig rig)
    {
        _app = app;
        BaseUri = baseUri;
        ExpectedHostToken = expectedToken;
        Rig = rig;
        HttpClient = new HttpClient { BaseAddress = baseUri, Timeout = Timeout.InfiniteTimeSpan };
        HttpClient.DefaultRequestHeaders.TryAddWithoutValidation(HostControlHeaderName, expectedToken);
    }

    public static async Task<LoopbackCoreHttpHost> StartAsync(string storageRoot, string expectedHostToken)
    {
        ApiTestStorage.ValidateTemporaryPath(storageRoot);
        var dataRoot = Path.Combine(storageRoot, "data");
        var dbPath = Path.Combine(storageRoot, "tinadec-modeb.db");
        Directory.CreateDirectory(dataRoot);

        var rig = new ScriptedAcpTestRig();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TinadecStorage:Enabled"] = "false",
            ["TinadecStorage:UserRoot"] = storageRoot,
            ["TinadecPersistence:DataRoot"] = dataRoot,
            ["TinadecPersistence:Sqlite:DatabasePath"] = dbPath,
            ["TinadecTools:DefaultWorkspaceRoot"] = Path.Combine(storageRoot, "default-ws"),
            ["TinadecAgent:BootstrapPackPath"] = Path.Combine(storageRoot, "no-fixture-pack.json")
        });

        builder.Services.AddTinadecPersistence(builder.Configuration, builder.Environment.ContentRootPath);
        builder.Services.AddTinadecCore();
        builder.Services.AddTinadecCoreHttp();
        builder.Services.AddTinadecOidc(builder.Configuration);
        builder.Services.AddSingleton<ISecretStore>(new AcpTestSecretStore());
        builder.Services.AddSingleton<IAgentChatClientFactory>(rig);
        builder.Services.Replace(ServiceDescriptor.Singleton<IToolProvider>(rig));

        var app = builder.Build();
        rig.HostServices = app.Services;

        var hostWrapper = new LoopbackCoreHttpHostPlaceholder();
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Headers.TryGetValue(HostControlHeaderName, out var tokenValues))
            {
                hostWrapper.Host?.ObservedHostTokens.Enqueue(tokenValues.ToString());
            }

            if (ctx.Request.Headers.TryGetValue(StorageScopeHttpExtensions.StorageHeader, out var storageValues))
            {
                hostWrapper.Host?.ObservedStorageHeaders.Enqueue(storageValues.ToString());
            }

            if (!string.IsNullOrEmpty(expectedHostToken))
            {
                var actual = ctx.Request.Headers[HostControlHeaderName].FirstOrDefault();
                if (!string.Equals(actual, expectedHostToken, StringComparison.Ordinal))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsJsonAsync(new { code = "host_token_invalid", message = "Missing or invalid host token." });
                    return;
                }
            }

            await next();
        });

        app.UseTinadecCoreExceptionHandler();
        using (var scope = app.Services.CreateScope())
        {
            var opts = scope.ServiceProvider.GetRequiredService<IOptions<TinadecPersistenceOptions>>().Value;
            var conn = scope.ServiceProvider.GetRequiredService<IDatabaseConnectionInfo>();
            if (opts.Enabled && conn.IsConfigured)
            {
                await scope.ServiceProvider.GetRequiredService<IStorageMigrationRunner>().RunAsync();
                await scope.ServiceProvider.GetRequiredService<StorageLifecycleService>().ReconcileAsync();
            }

            await DevSeed.SeedIfMissingAsync(scope.ServiceProvider, CancellationToken.None);
            await EmbeddedAcpCoreBackend.EnsureFallbackStarterPackAsync(scope.ServiceProvider, CancellationToken.None);
        }

        app.MapTinadecCore();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        var host = new LoopbackCoreHttpHost(app, new Uri(address.TrimEnd('/') + "/"), expectedHostToken, rig);
        hostWrapper.Host = host;
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        HttpClient.Dispose();
        try { await _app.StopAsync(); } catch { }
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }

    private sealed class LoopbackCoreHttpHostPlaceholder
    {
        public LoopbackCoreHttpHost? Host { get; set; }
    }
}

/// <summary>
/// Loopback OpenAI Chat Completions SSE server used by the True Self-Dogfood child-process E2E tests
/// where a real <c>dotnet TinadecCore.Api.dll --acp</c> subprocess executes multi-turn Q&amp;A and real
/// <c>TinadecTools</c> <c>write_file</c> calls gated by <c>session/request_permission</c>.
/// </summary>
internal sealed class FakeOpenAiHttpServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public Uri BaseUri { get; }
    public string V1BaseUrl => new Uri(BaseUri, "v1").ToString().TrimEnd('/');
    public string? TargetWorkspaceDirectory { get; set; }

    private FakeOpenAiHttpServer(WebApplication app, Uri baseUri)
    {
        _app = app;
        BaseUri = baseUri;
    }

    public static async Task<FakeOpenAiHttpServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        FakeOpenAiHttpServer? instance = null;
        var app = builder.Build();
        app.MapPost("/v1/chat/completions", async (HttpRequest req, HttpResponse res, CancellationToken ct) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            var root = doc.RootElement;
            var stream = root.TryGetProperty("stream", out var streamEl) && streamEl.ValueKind == JsonValueKind.True;
            var messages = root.TryGetProperty("messages", out var msgsEl) && msgsEl.ValueKind == JsonValueKind.Array
                ? msgsEl.EnumerateArray().ToArray()
                : [];

            var hasTools = root.TryGetProperty("tools", out var toolsEl)
                && toolsEl.ValueKind == JsonValueKind.Array
                && toolsEl.GetArrayLength() > 0;
            var hasToolRole = messages.Any(m => m.TryGetProperty("role", out var r) && r.GetString() == "tool");
            var allMessageText = string.Join("\n", messages.Select(ExtractOpenAiMessageText));

            // Determine if the latest user/task prompt asks for dogfood file creation
            var lastUserText = messages
                .LastOrDefault(m => m.TryGetProperty("role", out var r) && r.GetString() == "user") is { ValueKind: JsonValueKind.Object } lastUser
                    ? ExtractOpenAiMessageText(lastUser)
                    : string.Empty;

            var wantsWriteTool = hasTools
                && allMessageText.Contains("DOGFOOD_WRITE_FILE", StringComparison.Ordinal)
                && !hasToolRole;
            if (wantsWriteTool)
            {
                var targetFilePath = instance?.TargetWorkspaceDirectory is { Length: > 0 } wsDir
                    ? Path.Combine(wsDir, "dogfood-proof.txt")
                    : "dogfood-proof.txt";
                var argsJson = JsonSerializer.Serialize(new
                {
                    filepath = targetFilePath,
                    content = "DOGFOOD_E2E_VERIFIED"
                });

                if (!stream)
                {
                    await res.WriteAsJsonAsync(new
                    {
                        id = "chatcmpl-dogfood-tool",
                        @object = "chat.completion",
                        choices = new object[]
                        {
                            new
                            {
                                index = 0,
                                finish_reason = "tool_calls",
                                message = new
                                {
                                    role = "assistant",
                                    content = (string?)null,
                                    tool_calls = new object[]
                                    {
                                        new
                                        {
                                            id = "call_dogfood_write_1",
                                            type = "function",
                                            function = new { name = "write_file", arguments = argsJson }
                                        }
                                    }
                                }
                            }
                        }
                    }, ct);
                    return;
                }

                res.ContentType = "text/event-stream";
                var toolChunk = JsonSerializer.Serialize(new
                {
                    id = "chatcmpl-dogfood-tool",
                    @object = "chat.completion.chunk",
                    choices = new object[]
                    {
                        new
                        {
                            index = 0,
                            delta = new
                            {
                                role = "assistant",
                                tool_calls = new object[]
                                {
                                    new
                                    {
                                        index = 0,
                                        id = "call_dogfood_write_1",
                                        type = "function",
                                        function = new { name = "write_file", arguments = argsJson }
                                    }
                                }
                            },
                            finish_reason = (string?)null
                        }
                    }
                });
                var finishToolChunk = JsonSerializer.Serialize(new
                {
                    id = "chatcmpl-dogfood-tool",
                    @object = "chat.completion.chunk",
                    choices = new object[]
                    {
                        new { index = 0, delta = new { }, finish_reason = "tool_calls" }
                    }
                });
                await res.WriteAsync($"data: {toolChunk}\n\ndata: {finishToolChunk}\n\ndata: [DONE]\n\n", ct);
                await res.Body.FlushAsync(ct);
                return;
            }

            string replyText;
            if (hasTools)
            {
                if (hasToolRole || allMessageText.Contains("DOGFOOD_WRITE_FILE", StringComparison.Ordinal))
                {
                    replyText = ScriptedAcpTestRig.SoloTaskCompleted("DOGFOOD_WRITE_COMPLETED: wrote dogfood-proof.txt.");
                }
                else if (allMessageText.Contains("REMEMBER_CODE_7391", StringComparison.Ordinal)
                         && allMessageText.Contains("WHAT_WAS_THE_CODE", StringComparison.Ordinal))
                {
                    replyText = ScriptedAcpTestRig.SoloTaskCompleted("The code you asked me to remember is 7391.");
                }
                else if (allMessageText.Contains("REMEMBER_CODE_7391", StringComparison.Ordinal))
                {
                    replyText = ScriptedAcpTestRig.SoloTaskCompleted("Understood, I will remember 7391.");
                }
                else
                {
                    replyText = ScriptedAcpTestRig.SoloTaskCompleted($"DOGFOOD_ACK: {lastUserText.Replace('\n', ' ')}");
                }
            }
            else if (allMessageText.Contains("DOGFOOD_WRITE_FILE", StringComparison.Ordinal))
            {
                replyText = "DOGFOOD_WRITE_COMPLETED: wrote dogfood-proof.txt.";
            }
            else if (allMessageText.Contains("REMEMBER_CODE_7391", StringComparison.Ordinal)
                     && allMessageText.Contains("WHAT_WAS_THE_CODE", StringComparison.Ordinal))
            {
                replyText = "The code you asked me to remember is 7391.";
            }
            else if (allMessageText.Contains("REMEMBER_CODE_7391", StringComparison.Ordinal))
            {
                replyText = "Understood, I will remember 7391.";
            }
            else
            {
                replyText = $"DOGFOOD_ACK: {lastUserText}";
            }

            if (!stream)
            {
                await res.WriteAsJsonAsync(new
                {
                    id = "chatcmpl-dogfood-msg",
                    @object = "chat.completion",
                    choices = new object[]
                    {
                        new
                        {
                            index = 0,
                            finish_reason = "stop",
                            message = new { role = "assistant", content = replyText }
                        }
                    }
                }, ct);
                return;
            }

            res.ContentType = "text/event-stream";
            var deltaChunk = JsonSerializer.Serialize(new
            {
                id = "chatcmpl-dogfood-msg",
                @object = "chat.completion.chunk",
                choices = new object[]
                {
                    new { index = 0, delta = new { role = "assistant", content = replyText }, finish_reason = (string?)null }
                }
            });
            var stopChunk = JsonSerializer.Serialize(new
            {
                id = "chatcmpl-dogfood-msg",
                @object = "chat.completion.chunk",
                choices = new object[]
                {
                    new { index = 0, delta = new { }, finish_reason = "stop" }
                }
            });
            await res.WriteAsync($"data: {deltaChunk}\n\ndata: {stopChunk}\n\ndata: [DONE]\n\n", ct);
            await res.Body.FlushAsync(ct);
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        instance = new FakeOpenAiHttpServer(app, new Uri(address.TrimEnd('/') + "/"));
        return instance;
    }

    private static string ExtractOpenAiMessageText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return string.Empty;
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind == JsonValueKind.Array)
        {
            return string.Join("\n", content.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("text", out _))
                .Select(p => p.GetProperty("text").GetString() ?? string.Empty));
        }

        return string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        try { await _app.StopAsync(); } catch { }
        await _app.DisposeAsync();
    }
}
