using System.Text.Json;
using System.Text.Json.Serialization;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Demo;

internal sealed class EchoToolArgs
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

internal sealed class EchoToolResult
{
    [JsonPropertyName("echo")]
    public string Echo { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(EchoToolArgs))]
[JsonSerializable(typeof(EchoToolResult))]
internal partial class EchoToolJsonContext : JsonSerializerContext;

internal static class EchoTool
{
    public const string TOOL_ID = "echo";

    [ToolFunction(TOOL_ID)]
    public static ValueTask<ToolCallResponse<JsonElement>> HandleAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (request.Params.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("Tool 'echo' requires params.");
        }

        var args = JsonSerializer.Deserialize(request.Params, EchoToolJsonContext.Default.EchoToolArgs)
            ?? throw new InvalidOperationException("Tool 'echo' params could not be parsed.");

        var result = new EchoToolResult { Echo = args.Text };

        return ValueTask.FromResult(new ToolCallResponse<JsonElement>
        {
            CallId = request.ToolCallId,
            IsSuccess = true,
            Response = JsonSerializer.SerializeToElement(result, EchoToolJsonContext.Default.EchoToolResult)
        });
    }
}
