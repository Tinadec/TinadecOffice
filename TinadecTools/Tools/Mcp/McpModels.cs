using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace TinadecTools.Tools.Mcp;

public sealed class McpServersFile
{
    [JsonPropertyName("servers")] public List<McpServerConfig> Servers { get; set; } = new();
}

public sealed class McpServerConfig
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("command")] public string Command { get; set; } = string.Empty;
    [JsonPropertyName("args")] public List<string> Args { get; set; } = new();
    [JsonPropertyName("env")] public Dictionary<string, string?>? Env { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
}

public sealed class McpToolSummary
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("input_schema")] public JsonElement InputSchema { get; set; }
}

public sealed class McpServerToolList
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("tools")] public List<McpToolSummary> Tools { get; set; } = new();
}

public sealed class McpListParams
{
    [JsonPropertyName("include_schema")] public bool IncludeSchema { get; set; } = true;
}

public sealed class McpListResponse
{
    [JsonPropertyName("config_path")] public string ConfigPath { get; set; } = string.Empty;
    [JsonPropertyName("servers")] public List<McpServerToolList> Servers { get; set; } = new();
}

public sealed class McpInvokeParams
{
    [JsonPropertyName("server_id")] public string ServerId { get; set; } = string.Empty;
    [JsonPropertyName("tool_name")] public string ToolName { get; set; } = string.Empty;
    [JsonPropertyName("arguments")] public JsonElement? Arguments { get; set; }
}

public sealed class McpInvokeResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("server_id")] public string ServerId { get; set; } = string.Empty;
    [JsonPropertyName("tool_name")] public string ToolName { get; set; } = string.Empty;
    [JsonPropertyName("result")] public JsonElement Result { get; set; }
}

public sealed class McpSearchParams
{
    [JsonPropertyName("query")] public string Query { get; set; } = string.Empty;
    [JsonPropertyName("limit")] public int Limit { get; set; } = 20;
    [JsonPropertyName("include_schema")] public bool IncludeSchema { get; set; }
}

public sealed class McpSearchResult
{
    [JsonPropertyName("server_id")] public string ServerId { get; set; } = string.Empty;
    [JsonPropertyName("server_name")] public string ServerName { get; set; } = string.Empty;
    [JsonPropertyName("score")] public int Score { get; set; }
    [JsonPropertyName("tool")] public McpToolSummary Tool { get; set; } = new();
}

public sealed class McpSearchResponse
{
    [JsonPropertyName("results")] public List<McpSearchResult> Results { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(McpServersFile))]
[JsonSerializable(typeof(McpServerConfig))]
[JsonSerializable(typeof(McpToolSummary))]
[JsonSerializable(typeof(McpServerToolList))]
[JsonSerializable(typeof(McpListParams))]
[JsonSerializable(typeof(McpListResponse))]
[JsonSerializable(typeof(McpInvokeParams))]
[JsonSerializable(typeof(McpInvokeResponse))]
[JsonSerializable(typeof(McpSearchParams))]
[JsonSerializable(typeof(McpSearchResult))]
[JsonSerializable(typeof(McpSearchResponse))]
[JsonSerializable(typeof(CallToolResult))]
internal partial class McpJsonContext : JsonSerializerContext { }

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(McpListParams))]
[JsonSerializable(typeof(McpListResponse))]
internal partial class McpListToolJsonContext : JsonSerializerContext { }

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(McpInvokeParams))]
[JsonSerializable(typeof(McpInvokeResponse))]
internal partial class McpInvokeToolJsonContext : JsonSerializerContext { }

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(McpSearchParams))]
[JsonSerializable(typeof(McpSearchResponse))]
internal partial class McpSearchToolJsonContext : JsonSerializerContext { }
