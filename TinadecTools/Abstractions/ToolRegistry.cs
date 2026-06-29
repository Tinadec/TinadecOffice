using System.Text.Json;

namespace TinadecTools.Abstractions;

internal delegate ValueTask<ToolCallResponse<JsonElement>> ToolHandlerDelegate(
    ToolCallRequest<JsonElement> request,
    CancellationToken cancellationToken);

internal static class ToolRegistry
{
    private static readonly Dictionary<string, ToolHandlerDelegate> Handlers = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string toolId, ToolHandlerDelegate handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(handler);

        Handlers[toolId] = handler;
    }

    public static void Register<TArgs, TResult>(ToolHandlerBase<TArgs, TResult> handler)
        where TArgs : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        Register(handler.ToolId, handler.HandleAsync);
    }

    public static bool TryResolve(string toolId, out ToolHandlerDelegate handler)
        => Handlers.TryGetValue(toolId, out handler!);

    public static ValueTask<ToolCallResponse<JsonElement>> DispatchAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolve(request.ToolId, out var handler))
        {
            throw new InvalidOperationException($"Unknown tool '{request.ToolId}'.");
        }

        return handler(request, cancellationToken);
    }
}
