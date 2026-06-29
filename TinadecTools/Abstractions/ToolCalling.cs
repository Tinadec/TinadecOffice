using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecTools.Abstractions;

// 用于序列化/反序列化json请求的类

internal class ToolCallRequest<TParams> where TParams : notnull
{
    [JsonRequired]
    [Required]
    [JsonPropertyName("tool_id")]
    public string ToolId { get; set; } = string.Empty;

    [JsonRequired]
    [Required]
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonRequired]
    [Required]
    [JsonPropertyName("toolcall_id")]
    public long ToolCallId { get; set; } = -1;

    [JsonRequired]
    [Required]
    [JsonPropertyName("approved")]
    public bool Approved { get; set; } = false;

    [JsonPropertyName("params")]
    public TParams? Params { get; set; }
}

internal class ToolCallResponse<TResponse>
{
    [JsonRequired]
    [Required]
    [JsonPropertyName("call_id")]
    public long CallId { get; set; } = -1;

    [JsonRequired]
    [Required]
    [JsonPropertyName("success")]
    public bool IsSuccess { get; set; } = false;

    [JsonRequired]
    [Required]
    [JsonPropertyName("result")]
    public required TResponse Response { get; set; }
}

internal sealed class ToolCallErrorResponse
{
    [JsonRequired]
    [Required]
    [JsonPropertyName("call_id")]
    public long CallId { get; set; } = -1;

    [JsonRequired]
    [Required]
    [JsonPropertyName("success")]
    public bool IsSuccess { get; set; } = false;

    [JsonRequired]
    [Required]
    [JsonPropertyName("error")]
    public required string Error { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ToolCallRequest<JsonElement>))]
[JsonSerializable(typeof(ToolCallResponse<JsonElement>))]
[JsonSerializable(typeof(ToolCallErrorResponse))]
internal partial class ToolCallJsonContext : JsonSerializerContext;

//对于一个ToolCallResponse有一个基本模板：未审核的访问

internal class NotApprovedResponse
{
    [JsonRequired] [Required] public const string MESSAGE = "Unapproved Tool Request... Rejected.";
}
