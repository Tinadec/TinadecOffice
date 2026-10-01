using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

/// <summary>
/// The MCP inventory as the Tool Provider actually answered it. Every read carries its own
/// provenance: an empty <see cref="Servers"/> with <c>source = "tool_provider"</c> means
/// "nothing is configured", while the same empty list with <c>source =
/// "tool_provider_unavailable"</c> means "we could not look". Collapsing those two is how a
/// provider outage gets read as a deleted configuration.
/// </summary>
public sealed class McpInventoryDto
{
    /// <summary>Either <c>tool_provider</c> or <c>tool_provider_unavailable</c>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = McpReadSource.Unavailable;

    /// <summary>Why the provider could not answer. Omitted when it did.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>
    /// The workspace root whose provider process was asked. The provider resolves its MCP
    /// config file relative to that process, so the root is part of the answer, not noise.
    /// </summary>
    [JsonPropertyName("workspace_root")]
    public string? WorkspaceRoot { get; init; }

    /// <summary>Config file the provider read, as the provider reported it.</summary>
    [JsonPropertyName("config_path")]
    public string? ConfigPath { get; init; }

    /// <summary>
    /// Rows that carried no <c>id</c> and therefore could not be identified or clicked.
    /// Omitted when every row parsed, so a silently short list stays impossible.
    /// </summary>
    [JsonPropertyName("dropped_rows")]
    public int? DroppedRows { get; init; }

    [JsonPropertyName("servers")]
    public IReadOnlyList<McpServerDto> Servers { get; init; } = [];
}

/// <summary>One configured MCP server and the tools the provider managed to list from it.</summary>
public sealed class McpServerDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Provider-reported state (<c>connected</c> or <c>error</c>), passed through verbatim.
    /// Core never upgrades an unknown value to <c>connected</c>.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = McpServerStatus.Unknown;

    /// <summary>The provider's own failure text for this server. Omitted when it answered.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("tools")]
    public IReadOnlyList<McpToolDto> Tools { get; init; } = [];
}

/// <summary>One tool as advertised by an MCP server.</summary>
public sealed class McpToolDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// The server's input schema. Absent on the list route, which deliberately does not ask
    /// for schemas; on the per-server route an absent value means the server offered none.
    /// </summary>
    [JsonPropertyName("input_schema")]
    public JsonElement? InputSchema { get; init; }
}

/// <summary>
/// The answer about one named server. <see cref="Server"/> is present only when the provider
/// answered; an unknown id is a 404, never this DTO with a null server.
/// </summary>
public sealed class McpServerToolsDto
{
    [JsonPropertyName("source")]
    public string Source { get; init; } = McpReadSource.Unavailable;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("workspace_root")]
    public string? WorkspaceRoot { get; init; }

    [JsonPropertyName("config_path")]
    public string? ConfigPath { get; init; }

    [JsonPropertyName("server_id")]
    public string ServerId { get; init; } = string.Empty;

    [JsonPropertyName("server")]
    public McpServerDto? Server { get; init; }
}

/// <summary>Vocabulary for <see cref="McpInventoryDto.Source"/>.</summary>
public static class McpReadSource
{
    public const string Provider = "tool_provider";
    public const string Unavailable = "tool_provider_unavailable";
}

/// <summary>Vocabulary for <see cref="McpServerDto.Status"/> values Core itself assigns.</summary>
public static class McpServerStatus
{
    /// <summary>The provider reported a status string Core does not know.</summary>
    public const string Unknown = "unknown";
}
