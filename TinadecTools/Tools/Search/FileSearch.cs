using System.Text.Json.Serialization;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Search;

// ── 请求 ──────────────────────────────────────────────────────────────────────

public sealed class FileSearchParams
{
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = ".";

    /// <summary>文件名过滤，如 "*.cs" 或 "*.{ts,tsx}"</summary>
    [JsonPropertyName("glob")]
    public string? Glob { get; set; }

    /// <summary>rg --type 过滤，如 "cs"、"ts"、"rust"</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("case_sensitive")]
    public bool CaseSensitive { get; set; } = false;

    /// <summary>true = 字面量模式（-F），false = 正则模式</summary>
    [JsonPropertyName("fixed_strings")]
    public bool FixedStrings { get; set; } = false;

    /// <summary>上下文行数（rg -C N）</summary>
    [JsonPropertyName("context_lines")]
    public int ContextLines { get; set; } = 0;

    /// <summary>命中行数上限，达到后截断</summary>
    [JsonPropertyName("max_results")]
    public int MaxResults { get; set; } = 50;
}

// ── 响应 ──────────────────────────────────────────────────────────────────────

public sealed class MatchSpan
{
    /// <summary>行内起始字节偏移（rg 原值）</summary>
    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("end")]
    public int End { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// 一条扁平行记录，可能是命中行或上下文行。
/// 多个连续行（含上下文）按 FilePath + LineNumber 升序排列。
/// </summary>
public sealed class FileSearchLine
{
    [JsonPropertyName("filepath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("line_number")]
    public int LineNumber { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>true = 命中行；false = 上下文行</summary>
    [JsonPropertyName("is_match")]
    public bool IsMatch { get; set; }

    /// <summary>仅 IsMatch=true 时有值</summary>
    [JsonPropertyName("submatches")]
    public List<MatchSpan>? Submatches { get; set; }
}

public sealed class FileSearchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("lines")]
    public List<FileSearchLine> Lines { get; set; } = new();

    /// <summary>命中文件的 filepath → file_hash（与 FileRW 工具使用相同算法）</summary>
    [JsonPropertyName("file_hashes")]
    public Dictionary<string, string> FileHashes { get; set; } = new();

    /// <summary>因 MaxResults 被截断时为 true</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    /// <summary>rg summary 中的实际总命中行数（截断时尤其有参考价值）</summary>
    [JsonPropertyName("total_match_count")]
    public int TotalMatchCount { get; set; }

}

// ── JSON Source Generation ────────────────────────────────────────────────────

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(FileSearchParams))]
[JsonSerializable(typeof(FileSearchResponse))]
[JsonSerializable(typeof(FileSearchLine))]
[JsonSerializable(typeof(MatchSpan))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class FileSearchJsonContext : JsonSerializerContext;

// ── 工具入口 ──────────────────────────────────────────────────────────────────

public static class FileSearch
{
    [ToolFunction("file_search")]
    public static async ValueTask<FileSearchResponse> HandleAsync(
        FileSearchParams args,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args.Pattern))
            return new FileSearchResponse { Success = false, Error = "pattern is required." };

        try
        {
            return await RipgrepRunner.RunAsync(args, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new FileSearchResponse { Success = false, Error = ex.Message };
        }
    }
}
