using System.Text.Json.Serialization;
using System.Text.Json;
using TinadecTools.Abstractions;
using TinadecTools.Tools.FileRW;

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

    [JsonPropertyName("include_hidden")]
    public bool IncludeHidden { get; set; }

    [JsonPropertyName("respect_ignore_files")]
    public bool RespectIgnoreFiles { get; set; } = true;

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
    [JsonPropertyName("root_id")] public string? RootId { get; set; }
    [JsonPropertyName("relative_path")] public string? RelativePath { get; set; }
    [JsonPropertyName("filepath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("line_number")]
    public int LineNumber { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// true = content is a window of a longer line (see <see cref="ContentStart"/>/<see cref="LineLength"/>).
    /// A matched line can be a whole minified file; returning it entire put megabytes into the
    /// model's context and into every run checkpoint written after the call.
    /// </summary>
    [JsonPropertyName("content_truncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ContentTruncated { get; set; }

    /// <summary>Character offset of content within the original line; only when truncated.</summary>
    [JsonPropertyName("content_start")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContentStart { get; set; }

    /// <summary>Length of the original line in characters; only when truncated.</summary>
    [JsonPropertyName("line_length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LineLength { get; set; }

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
internal partial class FileSearchJsonContext : JsonSerializerContext { }

// ── 工具入口 ──────────────────────────────────────────────────────────────────

public static class FileSearch
{
    [ToolFunction("file_search", Description = "Search file contents in the run workspace (ripgrep). path must be an absolute directory path inside the workspace ('.' = the workspace root); results carry filepath/line_number/content plus per-file hashes. A line longer than 400 characters is returned as a window around its first match (content_truncated, content_start, line_length) with at most 16 submatches; use read_file for the full line.")]
    public static async ValueTask<FileSearchResponse> HandleAsync(
        FileSearchParams args,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args.Pattern))
            return new FileSearchResponse { Success = false, Error = "pattern is required." };

        try
        {
            if (args.Path == "." && WorkspacePathResolver.SourceRoots.Count > 1)
            {
                var combined = new FileSearchResponse { Success = true };
                foreach (var root in WorkspacePathResolver.SourceRoots)
                {
                    var scoped = JsonSerializer.Deserialize(JsonSerializer.Serialize(args, FileSearchJsonContext.Default.FileSearchParams), FileSearchJsonContext.Default.FileSearchParams)!;
                    scoped.Path = root.Path;
                    scoped.MaxResults = Math.Max(1, args.MaxResults - combined.Lines.Count(line => line.IsMatch));
                    var result = await RipgrepRunner.RunAsync(scoped, cancellationToken).ConfigureAwait(false);
                    if (!result.Success) return result;
                    foreach (var line in result.Lines)
                    {
                        line.RootId = root.Id;
                        var absolute = System.IO.Path.GetFullPath(line.FilePath, root.Path);
                        line.RelativePath = System.IO.Path.GetRelativePath(root.Path, absolute).Replace('\\', '/');
                        line.FilePath = absolute;
                    }
                    combined.Lines.AddRange(result.Lines);
                    foreach (var hash in result.FileHashes) combined.FileHashes[System.IO.Path.GetFullPath(hash.Key, root.Path)] = hash.Value;
                    combined.TotalMatchCount += result.TotalMatchCount;
                    if (result.Truncated || combined.Lines.Count(line => line.IsMatch) >= args.MaxResults) { combined.Truncated = true; break; }
                }
                return combined;
            }
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
