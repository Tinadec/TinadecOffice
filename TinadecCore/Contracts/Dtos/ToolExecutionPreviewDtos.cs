using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

/// <summary>
/// A bounded, read-only view of one finished tool call for the conversation feed:
/// the redacted argument evidence (never the raw parameters) plus a typed preview of
/// what the call returned. <see cref="Preview"/> is null until the execution reaches
/// a terminal state, and null when a terminal call stored no result; it is always written,
/// so a reader can tell "no preview" from an older server that lacks the field.
/// </summary>
public sealed class ToolExecutionPreviewDto
{
    [JsonPropertyName("execution_id")] public string ExecutionId { get; init; } = string.Empty;
    [JsonPropertyName("run_id")] public string RunId { get; init; } = string.Empty;
    [JsonPropertyName("task_id")] public string? TaskId { get; init; }
    [JsonPropertyName("tool_id")] public string ToolId { get; init; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("tool_success")] public bool? ToolSuccess { get; init; }
    [JsonPropertyName("error_category")] public string? ErrorCategory { get; init; }
    [JsonPropertyName("safe_error_message")] public string? SafeErrorMessage { get; init; }

    /// <summary>The same secret-free projection an approval shows; bulk values are sizes and hashes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonPropertyName("arguments")] public JsonNode? Arguments { get; init; }

    /// <summary>Length in characters of the stored result, so a reader knows how much was left out.</summary>
    [JsonPropertyName("result_length")] public long? ResultLength { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonPropertyName("preview")] public ToolResultPreviewDto? Preview { get; init; }

    /// <summary>True when any part of <see cref="Preview"/> was cut to fit its limits.</summary>
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }
}

/// <summary>
/// One preview, discriminated by <see cref="Kind"/>: <c>file_lines</c>, <c>search_matches</c>,
/// <c>directory</c>, <c>command_output</c>, <c>text</c>, <c>json</c> or <c>withheld</c>.
/// Only the members that belong to the kind are set; the rest are omitted on the wire.
/// </summary>
public sealed class ToolResultPreviewDto
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    /// <summary><c>file_lines</c> and <c>search_matches</c>.</summary>
    [JsonPropertyName("lines")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ToolPreviewLineDto>? Lines { get; init; }

    /// <summary><c>directory</c>.</summary>
    [JsonPropertyName("entries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ToolPreviewEntryDto>? Entries { get; init; }

    /// <summary>Items the tool reported in total (matches, entries, lines) when it said so.</summary>
    [JsonPropertyName("total_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TotalCount { get; init; }

    [JsonPropertyName("stdout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stdout { get; init; }

    [JsonPropertyName("stderr")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stderr { get; init; }

    [JsonPropertyName("exit_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExitCode { get; init; }

    [JsonPropertyName("timed_out")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TimedOut { get; init; }

    /// <summary><c>text</c>.</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonPropertyName("language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; init; }

    /// <summary><c>json</c>: depth, array and string bounded, secret keys dropped at any depth.</summary>
    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Value { get; init; }

    /// <summary><c>withheld</c>: why the content is not shown (for example <c>sensitive_path</c>).</summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}

public sealed class ToolPreviewLineDto
{
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    [JsonPropertyName("line_number")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LineNumber { get; init; }

    [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;

    /// <summary><c>search_matches</c>: false for a context line around a match.</summary>
    [JsonPropertyName("is_match")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsMatch { get; init; }
}

public sealed class ToolPreviewEntryDto
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Size { get; init; }
}
