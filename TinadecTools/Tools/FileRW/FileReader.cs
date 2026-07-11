using System.Text.Json.Serialization;
using NLog;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.FileRW;



/// <summary>
/// 常规读写：首尾行号
/// </summary>
public sealed class NormalFileReadParams
{
    [JsonPropertyName("filepath")] public string FilePath { get; set; } = string.Empty;
    [JsonPropertyName("start_row")] public int StartRow { get; set; } = 1;
    [JsonPropertyName("end_row")] public int EndRow { get; set; } = int.MaxValue;
}

/// <summary>
/// 包含行哈希的一行
/// </summary>
public sealed class ExtendedLineContent
{
    [JsonPropertyName("content")] public LineContent Content{get; set; } = new LineContent();
    [JsonPropertyName("line_hash")] public string LineHash { get; set; } = string.Empty;

    public ExtendedLineContent()
    {
    }

    public ExtendedLineContent(LineContent lineContent)
    {
        Content = lineContent;
        LineHash = lineContent.LineNumber + "|" +
                   FileHashing.ComputeLineHash(lineContent.Content, lineContent.LineNumber);
    }
}

public sealed class NormalFileReadResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("file_hash")] public string FileHash { get; set; } = string.Empty;
    [JsonPropertyName("all_contents")] public List<ExtendedLineContent> AllContents { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(NormalFileReadParams))]
[JsonSerializable(typeof(LineContent))]
[JsonSerializable(typeof(ExtendedLineContent))]
[JsonSerializable(typeof(NormalFileReadResponse))]
internal partial class FileReaderJsonContext : JsonSerializerContext;

public static class FileReader
{
    private const int max_sentinel_read_lines = 150;
    private static readonly Logger logger = LogManager.GetCurrentClassLogger();

    [ToolFunction("read_file")]
    public static async ValueTask<NormalFileReadResponse> HandleAsync(
        NormalFileReadParams args,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = FileToolRuntime.ResolvePath(args.FilePath);
            var (startLine, endLine) = resolveReadRange(args.StartRow, args.EndRow);
            var slot = FileToolRuntime.GetFileHandle(path);

            using (await slot.RwLock.ReadLockAsync(cancellationToken).ConfigureAwait(false))
            {
                using var file = FileToolRuntime.OpenRead(path);
                var fileHash = await file.ComputeFileHashAsync(cancellationToken).ConfigureAwait(false);
                if (file.LineCount == 0 || startLine > file.LineCount)
                {
                    logger.Debug("read_file读取{path}的{startRow}-{endRow}行，共0行", path, startLine, endLine);
                    return new NormalFileReadResponse { Success = true, FileHash = fileHash };
                }

                var effectiveEndLine = Math.Min(endLine, file.LineCount);
                var lines = await file.ReadLines([new KeyValuePair<int, int>(startLine - 1, effectiveEndLine - 1)])
                    .ConfigureAwait(false);

                var contents = lines
                    .Select(line => new LineContent(line.Content, line.LineNumber + 1, line.StartOffset, line.EndOffset))
                    .Select(line => new ExtendedLineContent(line))
                    .ToList();

                logger.Debug("read_file读取{path}的{startRow}-{endRow}行，共{count}行", path, startLine, effectiveEndLine, contents.Count);
                return new NormalFileReadResponse
                {
                    Success = true,
                    FileHash = fileHash,
                    AllContents = contents
                };
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.Warn(ex, "read_file被取消，文件为{path}", args.FilePath);
            return new NormalFileReadResponse
            {
                Success = false,
                Error = "read_file canceled."
            };
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "read_file失败，文件为{path}，范围为{startRow}-{endRow}", args.FilePath, args.StartRow, args.EndRow);
            return new NormalFileReadResponse
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    private static (int StartLine, int EndLine) resolveReadRange(int startRow, int endRow)
    {
        if (startRow < 1)
            throw new ArgumentOutOfRangeException(nameof(startRow), "start_row must be 1 or greater.");

        if (endRow < startRow)
            throw new ArgumentOutOfRangeException(nameof(endRow), "end_row must be greater than or equal to start_row.");

        var requestedLength = (long)endRow - startRow + 1;
        if (endRow == int.MaxValue || requestedLength == int.MaxValue)
            endRow = (int)Math.Min((long)startRow + max_sentinel_read_lines - 1, int.MaxValue);

        return (startRow, endRow);
    }
}
