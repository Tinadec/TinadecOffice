using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Demo;

internal sealed class StatefulToolArgs
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

internal sealed class StatefulToolResult
{
    [JsonPropertyName("prefix")]
    public string Prefix { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(StatefulToolArgs))]
[JsonSerializable(typeof(StatefulToolResult))]
internal partial class StatefulToolJsonContext : JsonSerializerContext;

internal sealed class StatefulTool(string prefix) : ToolHandlerBase<StatefulToolArgs, StatefulToolResult>
{
    public override string ToolId => "stateful_echo";

    protected override JsonTypeInfo<StatefulToolArgs> ArgsTypeInfo => StatefulToolJsonContext.Default.StatefulToolArgs;

    protected override JsonTypeInfo<StatefulToolResult> ResultTypeInfo => StatefulToolJsonContext.Default.StatefulToolResult;

    protected override ValueTask<StatefulToolResult> ExecuteAsync(
        StatefulToolArgs args,
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new StatefulToolResult
        {
            Prefix = prefix,
            Message = args.Message
        });
    }
}
