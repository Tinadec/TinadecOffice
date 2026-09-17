using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
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

    [JsonPropertyName("params")] public TParams? Params { get; set; }
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

// `#manifest` 协议握手响应：Core 启动/刷新工具清单时调用。

internal sealed class ToolManifest
{
    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; set; } = 2;

    [JsonPropertyName("manifest_hash")]
    public string ManifestHash { get; set; } = string.Empty;

    [JsonPropertyName("tools")]
    public List<ToolManifestEntry> Tools { get; set; } = [];
}

internal sealed class ToolManifestEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("requires_approval")]
    public bool RequiresApproval { get; set; }

    [JsonPropertyName("input_schema")]
    public JsonElement InputSchema { get; set; }

    [JsonPropertyName("risk")]
    public string Risk { get; set; } = "low";

    [JsonPropertyName("mutates_workspace")]
    public bool MutatesWorkspace { get; set; }

    [JsonPropertyName("retry_safety")]
    public string RetrySafety { get; set; } = "safe";

    [JsonPropertyName("confirmation_fields")]
    public List<string> ConfirmationFields { get; set; } = [];
}

internal static class ToolManifestHash
{
    public static string Compute(IReadOnlyList<ToolManifestEntry> tools)
    {
        var builder = new StringBuilder();
        foreach (var tool in tools.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(tool.Id).Append('\0')
                .Append(tool.Description).Append('\0')
                .Append(tool.RequiresApproval).Append('\0')
                .Append(CanonicalSchema(tool.InputSchema)).Append('\0')
                .Append(tool.Risk).Append('\0')
                .Append(tool.MutatesWorkspace).Append('\0')
                .Append(tool.RetrySafety).Append('\0')
                .AppendJoin('\u001f', tool.ConfirmationFields).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// The schema text both processes hash. CANONICAL (re-written) rather than raw,
    /// because Core hashes the schema it parsed off the wire while this side hashes the
    /// one it parsed from its own literal: those texts legitimately differ in escaping
    /// (a description carrying an apostrophe or a quote is literal on one side and
    /// \u-escaped on the other) and in whitespace, so raw-text hashing made an identical
    /// manifest look tampered and admission refused every call.
    ///
    /// Written through a Utf8JsonWriter rather than JsonSerializer: this process runs with
    /// reflection-based serialization disabled, so the serializer overload is not an
    /// option here. Core's ToolManifestHasher.GetRawSchema uses the identical approach,
    /// which is what makes the two hashes agree.
    /// </summary>
    private static string CanonicalSchema(JsonElement schema)
    {
        if (schema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return string.Empty;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) schema.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ToolCallRequest<JsonElement>))]
[JsonSerializable(typeof(ToolCallResponse<JsonElement>))]
[JsonSerializable(typeof(ToolCallErrorResponse))]
[JsonSerializable(typeof(ToolManifest))]
[JsonSerializable(typeof(ToolManifestEntry))]
[JsonSerializable(typeof(List<ToolManifestEntry>))]
[JsonSerializable(typeof(string))]
internal partial class ToolCallJsonContext : JsonSerializerContext { }

//对于一个ToolCallResponse有一个基本模板：未审核的访问

internal class NotApprovedResponse
{
    [JsonRequired] [Required] public const string MESSAGE = "Unapproved Tool Request... Rejected.";
}
