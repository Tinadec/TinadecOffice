using System.Text.Json;
using NLog;
using TinadecTools.Abstractions;

namespace TinadecTools.Runtime;

/// <summary>
/// Reads line-delimited tool calls and dispatches them concurrently so a
/// long-running call (e.g. a 120s shell command) cannot head-of-line block the
/// reserved <c>#terminal</c> control plane (stdin/kill/status) or read-only tools.
///
/// Trade-off: handlers were written against the original serial loop, so calls
/// whose descriptor marks them <see cref="ToolDescriptor.MutatesWorkspace"/> still
/// serialize on a single gate; control-plane (<c>#</c>-prefixed) and read-only
/// tools run concurrently. Response lines are written under one lock so a line
/// never interleaves; each response carries its own call id either way.
/// </summary>
internal static class ToolDispatchLoop
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly SemaphoreSlim MutatingGate = new(1, 1);

    public static async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        var pending = new List<Task>();
        try
        {
            string? line;
            while ((line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                pending.Add(ProcessLineAsync(line, output, cancellationToken));
                pending.RemoveAll(task => task.IsCompleted);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }

        // Drain in-flight calls so their responses are written before exit.
        if (pending.Count > 0)
            await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private static async Task ProcessLineAsync(string line, TextWriter output, CancellationToken cancellationToken)
    {
        ToolCallRequest<JsonElement>? req = null;
        try
        {
            req = JsonSerializer.Deserialize(line, ToolCallJsonContext.Default.ToolCallRequestJsonElement)
                ?? throw new JsonException("Tool call request was null.");
            var resp = await DispatchGatedAsync(req, cancellationToken).ConfigureAwait(false);
            lock (output)
            {
                output.WriteLine(JsonSerializer.Serialize(resp, ToolCallJsonContext.Default.ToolCallResponseJsonElement));
                output.Flush();
            }

            Logger.Debug("处理完毕工具调用{id},工具类型为{type}", req.ToolCallId, req.ToolId);
        }
        catch (Exception ex)
        {
            var error = new ToolCallErrorResponse
            {
                CallId = req?.ToolCallId ?? -1,
                IsSuccess = false,
                Error = ex.Message
            };
            lock (output)
            {
                output.WriteLine(JsonSerializer.Serialize(error, ToolCallJsonContext.Default.ToolCallErrorResponse));
                output.Flush();
            }

            Logger.Warn("工具调用流程出错，错误为{ex}", ex);
        }
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> DispatchGatedAsync(
        ToolCallRequest<JsonElement> request, CancellationToken cancellationToken)
    {
        if (IsConcurrencySafe(request.ToolId))
            return await ToolRegistry.DispatchAsync(request, cancellationToken).ConfigureAwait(false);

        await MutatingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ToolRegistry.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            MutatingGate.Release();
        }
    }

    private static bool IsConcurrencySafe(string toolId)
    {
        // Reserved control-plane tools (#terminal, #manifest) are always concurrent.
        if (toolId.StartsWith('#')) return true;
        // Unknown tools resolve to a dispatch error anyway; treat them as mutating.
        return ToolRegistry.TryGetDescriptor(toolId, out var descriptor) && !descriptor.MutatesWorkspace;
    }
}
