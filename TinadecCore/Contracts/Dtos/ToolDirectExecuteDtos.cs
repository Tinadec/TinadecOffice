using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

/// <summary>Northbound request for a user-triggered, run-less tool call.</summary>
public sealed class ToolDirectExecuteRequestDto
{
    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; init; }
}

/// <summary>Stable response envelope for user-triggered code tools.</summary>
public sealed class CodeToolExecuteResultDto
{
    [JsonPropertyName("tool_id")]
    public string ToolId { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("evidence")]
    public IReadOnlyList<string> Evidence { get; init; } = [];

    [JsonPropertyName("data")]
    public JsonElement Data { get; init; } = EmptyObject();

    [JsonPropertyName("requires_approval")]
    public bool RequiresApproval { get; init; }

    [JsonPropertyName("approval_summary")]
    public string? ApprovalSummary { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    private static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();
}
