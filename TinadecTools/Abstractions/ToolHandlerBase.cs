using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TinadecTools.Abstractions;

// 非 static 工具的基础抽象类。
// 静态工具以后可以走单独的静态方法 + 注册表，不必继承这里。

internal abstract class ToolHandlerBase<TArgs, TResult>
    where TArgs : notnull
{
    public abstract string ToolId { get; }

    protected abstract JsonTypeInfo<TArgs> ArgsTypeInfo { get; }

    protected abstract JsonTypeInfo<TResult> ResultTypeInfo { get; }

    protected abstract ValueTask<TResult> ExecuteAsync(
        TArgs args,
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken);

    public async ValueTask<ToolCallResponse<JsonElement>> HandleAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken = default)
    {
        if (request.Params.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"Tool '{ToolId}' requires params.");
        }

        var args = JsonSerializer.Deserialize(request.Params, ArgsTypeInfo)
            ?? throw new InvalidOperationException($"Tool '{ToolId}' params could not be parsed.");

        var result = await ExecuteAsync(args, request, cancellationToken).ConfigureAwait(false);

        return new ToolCallResponse<JsonElement>
        {
            CallId = request.ToolCallId,
            IsSuccess = true,
            Response = JsonSerializer.SerializeToElement(result, ResultTypeInfo)
        };
    }
}
