using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Serializes unsolicited wire events raised during one tool call into the run
/// event log. The provider read loop must never block on persistence, so events
/// are buffered on an unbounded channel and drained by a single consumer task.
/// </summary>
internal sealed class ToolEventPump : IDisposable
{
    private const int PendingLimit = 8_192;

    private readonly Channel<ToolWireEventDto> _queue = Channel.CreateUnbounded<ToolWireEventDto>();
    private readonly ILifecycleManager _lifecycle;
    private readonly ILogger _logger;
    private readonly Guid _runId;
    private readonly Guid _taskId;
    private readonly Guid _executionId;
    private readonly string _toolId;
    private readonly Task _consumer;
    private int _dropped;

    public ToolEventPump(
        ILifecycleManager lifecycle,
        ILogger logger,
        Guid runId,
        Guid taskId,
        Guid executionId,
        string toolId)
    {
        _lifecycle = lifecycle;
        _logger = logger;
        _runId = runId;
        _taskId = taskId;
        _executionId = executionId;
        _toolId = toolId;
        _consumer = Task.Run(DrainAsync);
    }

    /// <summary>Called on the provider read-loop thread; never blocks.</summary>
    public void Enqueue(ToolWireEventDto wireEvent)
    {
        if (Volatile.Read(ref _dropped) > 0) return;
        if (_queue.Writer.TryWrite(wireEvent)) return;
        if (Interlocked.Exchange(ref _dropped, 1) == 0)
        {
            _logger.LogWarning("Dropping further wire events for execution {ExecutionId}: run event backlog exceeded.", _executionId);
        }
    }

    /// <summary>Emits the terminal.command marker before the process starts.</summary>
    public async Task AppendCommandAsync(JsonElement? parameters, string workspaceRoot, CancellationToken cancellationToken)
    {
        var command = ReadString(parameters, "command");
        var cwd = ReadString(parameters, "cwd") ?? workspaceRoot;
        var longLived = ReadBoolean(parameters, "long_lived") ?? false;
        var timeoutMs = ReadInt32(parameters, "timeout_ms");
        try
        {
            await _lifecycle.AppendEventAsync(_runId, "terminal.command", new
            {
                execution_id = _executionId,
                task_id = _taskId,
                tool_id = _toolId,
                command,
                cwd,
                long_lived = longLived,
                timeout_ms = timeoutMs
            }, $"Terminal command: {command}", "info", _taskId, null, _toolId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not append terminal.command for execution {ExecutionId}", _executionId);
        }
    }

    private async Task DrainAsync()
    {
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (_queue.Reader.TryRead(out var wireEvent))
            {
                try
                {
                    await AppendAsync(wireEvent).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Terminal event {Event} could not be persisted for execution {ExecutionId}",
                        wireEvent.Event, _executionId);
                }
            }
        }
    }

    private Task AppendAsync(ToolWireEventDto wireEvent) => _lifecycle.AppendEventAsync(
        _runId,
        wireEvent.Event,
        Payload(wireEvent),
        Summary(wireEvent),
        "info",
        _taskId,
        null,
        _toolId);

    private object Payload(ToolWireEventDto wireEvent)
    {
        var data = new Dictionary<string, object?>
        {
            ["execution_id"] = _executionId,
            ["task_id"] = _taskId,
            ["tool_id"] = _toolId
        };
        if (wireEvent.Payload is { ValueKind: JsonValueKind.Object } payload)
        {
            foreach (var property in payload.EnumerateObject())
            {
                data[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.TryGetInt64(out var number) ? number : property.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => property.Value.Clone()
                };
            }
        }
        return data;
    }

    private static string Summary(ToolWireEventDto wireEvent) => wireEvent.Event switch
    {
        "terminal.stdout" => "Terminal output",
        "terminal.exit" => "Terminal session exited",
        _ => wireEvent.Event
    };

    /// <summary>Signals that no more events will be enqueued and waits for the drain.</summary>
    public async Task CompleteAsync(TimeSpan timeout)
    {
        _queue.Writer.TryComplete();
        try
        {
            await _consumer.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("Terminal event drain timed out for execution {ExecutionId}", _executionId);
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
    }

    private static string? ReadString(JsonElement? source, string name) =>
        source is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? ReadInt32(JsonElement? source, string name) =>
        source is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var property)
        && property.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static bool? ReadBoolean(JsonElement? source, string name) =>
        source is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property)
            ? property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;
}
