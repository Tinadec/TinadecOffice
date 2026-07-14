using System.Text;
using System.Text.Json.Serialization;
using NLog;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.FileRW;

public sealed class HashedLineContent
{
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("hash")] public string Hash { get; set; } = string.Empty;
}

public sealed class ReplaceLinesParams
{
    [JsonPropertyName("filepath")] public string FilePath { get; set; } = string.Empty;
    [JsonPropertyName("start_row")] public int StartRow { get; set; }
    [JsonPropertyName("end_row")] public int EndRow { get; set; }
    [JsonPropertyName("content")] public List<HashedLineContent> Content { get; set; } = new();
}

public sealed class FileHashMutationParams
{
    [JsonPropertyName("filepath")] public string FilePath { get; set; } = string.Empty;
    [JsonPropertyName("start_offset")] public long StartOffset { get; set; }
    [JsonPropertyName("length")] public long Length { get; set; }
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("file_hash")] public string FileHash { get; set; } = string.Empty;
}

public sealed class InsertLineParams
{
    [JsonPropertyName("filepath")] public string FilePath { get; set; } = string.Empty;
    [JsonPropertyName("line_number")] public int LineNumber { get; set; }
    [JsonPropertyName("position")] public string Position { get; set; } = "after";
    [JsonPropertyName("content")] public List<string> Content { get; set; } = new();
    [JsonPropertyName("file_hash")] public string FileHash { get; set; } = string.Empty;
}

public sealed class DeleteLineParams
{
    [JsonPropertyName("filepath")] public string FilePath { get; set; } = string.Empty;
    [JsonPropertyName("start_row")] public int StartRow { get; set; }
    [JsonPropertyName("end_row")] public int EndRow { get; set; }
    [JsonPropertyName("file_hash")] public string FileHash { get; set; } = string.Empty;
}

public sealed class FileMutationResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("file_hash")] public string FileHash { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HashedLineContent))]
[JsonSerializable(typeof(ReplaceLinesParams))]
[JsonSerializable(typeof(FileHashMutationParams))]
[JsonSerializable(typeof(InsertLineParams))]
[JsonSerializable(typeof(DeleteLineParams))]
[JsonSerializable(typeof(FileMutationResponse))]
internal partial class FileWriterJsonContext : JsonSerializerContext;

public static class FileWriter
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    [ToolFunction("replace_lines", RequiresApproval = true)]
    public static ValueTask<FileMutationResponse> ReplaceLinesAsync(
        ReplaceLinesParams args,
        CancellationToken cancellationToken)
    {
        return RunMutationAsync(args.FilePath, "replace_lines", async file =>
        {
            var (startLine, endLine, expectedCount) = GetReplaceRange(args);
            await ValidateLineAnchorsAsync(file, args.Content, startLine, endLine, expectedCount, cancellationToken)
                .ConfigureAwait(false);

            await file.ReplaceLines(
                    startLine,
                    endLine,
                    args.Content.Select(line => line.Content).ToArray())
                .ConfigureAwait(false);
        }, cancellationToken);
    }

    [ToolFunction("replace_bytes", RequiresApproval = true)]
    public static ValueTask<FileMutationResponse> ReplaceBytesAsync(
        FileHashMutationParams args,
        CancellationToken cancellationToken)
    {
        return RunHashCheckedMutationAsync(
            args.FilePath,
            "replace_bytes",
            args.FileHash,
            file => file.ReplaceBytes(args.StartOffset, args.Length, Encoding.UTF8.GetBytes(args.Content)),
            cancellationToken);
    }

    [ToolFunction("insert_bytes", RequiresApproval = true)]
    public static ValueTask<FileMutationResponse> InsertBytesAsync(
        FileHashMutationParams args,
        CancellationToken cancellationToken)
    {
        return RunHashCheckedMutationAsync(
            args.FilePath,
            "insert_bytes",
            args.FileHash,
            file => file.InsertBytes(args.StartOffset, Encoding.UTF8.GetBytes(args.Content)),
            cancellationToken);
    }

    [ToolFunction("insert_byte", RequiresApproval = true)]
    public static ValueTask<FileMutationResponse> InsertByteAsync(
        FileHashMutationParams args,
        CancellationToken cancellationToken)
    {
        return InsertBytesAsync(args, cancellationToken);
    }

    [ToolFunction("delete_bytes", RequiresApproval = true)]
    public static ValueTask<FileMutationResponse> DeleteBytesAsync(
        FileHashMutationParams args,
        CancellationToken cancellationToken)
    {
        return RunHashCheckedMutationAsync(
            args.FilePath,
            "delete_bytes",
            args.FileHash,
            file => file.DeleteBytes(args.StartOffset, args.Length),
            cancellationToken);
    }

    [ToolFunction("insert_line", RequiresApproval = true)]
    public static ValueTask<FileMutationResponse> InsertLineAsync(
        InsertLineParams args,
        CancellationToken cancellationToken)
    {
        return RunHashCheckedMutationAsync(
            args.FilePath,
            "insert_line",
            args.FileHash,
            file =>
            {
                var lineNumber = ToZeroBasedLine(args.LineNumber, nameof(args.LineNumber));
                if (string.Equals(args.Position, "before", StringComparison.OrdinalIgnoreCase))
                    return file.InsertLinesBeforeLine(lineNumber, args.Content);

                if (string.Equals(args.Position, "after", StringComparison.OrdinalIgnoreCase))
                    return file.InsertLinesAfterLine(lineNumber, args.Content);

                throw new ArgumentException("position must be 'before' or 'after'.");
            },
            cancellationToken);
    }

    [ToolFunction("delete_line", RequiresApproval = true)]
    public static ValueTask<FileMutationResponse> DeleteLineAsync(
        DeleteLineParams args,
        CancellationToken cancellationToken)
    {
        return RunHashCheckedMutationAsync(
            args.FilePath,
            "delete_line",
            args.FileHash,
            file => file.DeleteLines(
                ToZeroBasedLine(args.StartRow, nameof(args.StartRow)),
                ToZeroBasedLine(args.EndRow, nameof(args.EndRow))),
            cancellationToken);
    }

    private static ValueTask<FileMutationResponse> RunHashCheckedMutationAsync(
        string filePath,
        string operation,
        string expectedFileHash,
        Func<FileAccessor, Task> mutation,
        CancellationToken cancellationToken)
    {
        return RunMutationAsync(filePath, operation, async file =>
        {
            var actualFileHash = await file.ComputeFileHashAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(expectedFileHash, actualFileHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"REJECT {operation}: file_hash mismatch, expected {expectedFileHash}, actual {actualFileHash}.");
            }

            await mutation(file).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static async ValueTask<FileMutationResponse> RunMutationAsync(
        string filePath,
        string operation,
        Func<FileAccessor, Task> mutation,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = FileToolRuntime.ResolvePath(filePath);
            var slot = FileToolRuntime.GetFileHandle(path);

            using (await slot.RwLock.WriteLockAsync(cancellationToken).ConfigureAwait(false))
            {
                using var file = FileToolRuntime.OpenWrite(path, cancellationToken);
                await mutation(file).ConfigureAwait(false);
                var newFileHash = await file.ComputeFileHashAsync(CancellationToken.None).ConfigureAwait(false);
                Logger.Debug("{operation}写入{path}成功，新file_hash为{fileHash}", operation, path, newFileHash);
                return new FileMutationResponse { Success = true, FileHash = newFileHash };
            }
        }
        catch (OperationCanceledException ex)
        {
            Logger.Warn(ex, "{operation}被取消，文件为{path}", operation, filePath);
            return new FileMutationResponse { Success = false, Error = $"{operation} canceled." };
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "{operation}失败，文件为{path}", operation, filePath);
            return new FileMutationResponse { Success = false, Error = ex.Message };
        }
    }

    private static async Task ValidateLineAnchorsAsync(
        FileAccessor file,
        IReadOnlyList<HashedLineContent> expectedLines,
        int startLine,
        int endLine,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        var currentLines = await file.ReadLines([new KeyValuePair<int, int>(startLine, endLine)])
            .ConfigureAwait(false);

        if (currentLines.Count != expectedCount)
            throw new InvalidOperationException("target lines could not be read for hash validation.");

        for (var index = 0; index < currentLines.Count; index++)
        {
            var currentLine = currentLines[index];
            var lineNumber = currentLine.LineNumber + 1;
            var actualHash = BuildLineHash(currentLine.Content, lineNumber);
            var expectedHash = expectedLines[index].Hash;

            if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"REJECT line {lineNumber}: hash mismatch, expected {expectedHash}, actual {actualHash}.");
            }
        }
    }

    private static (int StartLine, int EndLine, int ExpectedCount) GetReplaceRange(ReplaceLinesParams args)
    {
        var startLine = ToZeroBasedLine(args.StartRow, nameof(args.StartRow));
        var endLine = ToZeroBasedLine(args.EndRow, nameof(args.EndRow));
        if (startLine > endLine)
            throw new ArgumentException("start_row must be less than or equal to end_row.");

        var expectedCount = endLine - startLine + 1;
        if (args.Content.Count != expectedCount)
            throw new ArgumentException($"content count {args.Content.Count} does not match target line count {expectedCount}.");

        return (startLine, endLine, expectedCount);
    }

    private static int ToZeroBasedLine(int lineNumber, string parameterName)
    {
        if (lineNumber < 1)
            throw new ArgumentOutOfRangeException(parameterName, "line number must be 1 or greater.");

        return lineNumber - 1;
    }

    private static string BuildLineHash(string content, int lineNumber)
    {
        return lineNumber + "|" + FileHashing.ComputeLineHash(content, lineNumber);
    }
}
