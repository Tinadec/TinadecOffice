using System.Text;
using System.Text.Json.Serialization;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.FileRW;

public sealed class ListDirectoryParams
{
    [JsonPropertyName("path")] public string Path { get; set; } = ".";
    [JsonPropertyName("limit")] public int Limit { get; set; } = 100;
    [JsonPropertyName("cursor")] public string? Cursor { get; set; }
}

public sealed class StatPathParams
{
    [JsonPropertyName("path")] public string Path { get; set; } = ".";
}

public sealed class FileSystemEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("modified_at")] public DateTime ModifiedAt { get; set; }
    [JsonPropertyName("is_readonly")] public bool IsReadOnly { get; set; }
    [JsonPropertyName("is_hidden")] public bool IsHidden { get; set; }
    [JsonPropertyName("link_target")] public string? LinkTarget { get; set; }
}

public sealed class ListDirectoryResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("entries")] public List<FileSystemEntry> Entries { get; set; } = new();
    [JsonPropertyName("has_more")] public bool HasMore { get; set; }
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; set; }
}

public sealed class StatPathResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("entry")] public FileSystemEntry? Entry { get; set; }
}

public sealed class WriteFileParams
{
    [JsonPropertyName("filepath")] public string FilePath { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("file_hash")] public string? FileHash { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ListDirectoryParams))]
[JsonSerializable(typeof(StatPathParams))]
[JsonSerializable(typeof(FileSystemEntry))]
[JsonSerializable(typeof(ListDirectoryResponse))]
[JsonSerializable(typeof(StatPathResponse))]
[JsonSerializable(typeof(WriteFileParams))]
[JsonSerializable(typeof(FileMutationResponse))]
internal partial class FileSystemToolsJsonContext : JsonSerializerContext;

public static class FileSystemTools
{
    private const int MaxPageSize = 500;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    [ToolFunction("ls")]
    public static ValueTask<ListDirectoryResponse> ListAsync(ListDirectoryParams args, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (args.Limit is < 1 or > MaxPageSize)
                throw new ArgumentOutOfRangeException(nameof(args.Limit), $"limit must be between 1 and {MaxPageSize}.");

            var directory = WorkspacePathResolver.ResolveDirectory(args.Path);
            var entries = Directory.EnumerateFileSystemEntries(directory)
                .Select(CreateEntry)
                .OrderBy(entry => entry.Name, NaturalNameComparer.Instance)
                .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var start = DecodeCursor(args.Cursor);
            if (start > entries.Count)
                throw new ArgumentException("cursor is no longer valid.", nameof(args.Cursor));

            var page = entries.Skip(start).Take(args.Limit).ToList();
            var next = start + page.Count;
            return ValueTask.FromResult(new ListDirectoryResponse
            {
                Success = true,
                Entries = page,
                HasMore = next < entries.Count,
                NextCursor = next < entries.Count ? EncodeCursor(next) : null
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ValueTask.FromResult(new ListDirectoryResponse { Success = false, Error = ex.Message });
        }
    }

    [ToolFunction("stat")]
    public static ValueTask<StatPathResponse> StatAsync(StatPathParams args, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = WorkspacePathResolver.ResolvePath(args.Path, allowFinalLink: true);
            if (!WorkspacePathResolver.Exists(path))
                throw new FileNotFoundException($"Path '{args.Path}' does not exist.");

            return ValueTask.FromResult(new StatPathResponse { Success = true, Entry = CreateEntry(path) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ValueTask.FromResult(new StatPathResponse { Success = false, Error = ex.Message });
        }
    }

    [ToolFunction("write_file", RequiresApproval = true)]
    public static async ValueTask<FileMutationResponse> WriteAsync(
        WriteFileParams args,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = FileToolRuntime.ResolvePath(args.FilePath);
            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                throw new DirectoryNotFoundException("The parent directory must already exist.");

            var slot = FileToolRuntime.GetFileHandle(path);
            using (await slot.RwLock.WriteLockAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!File.Exists(path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await using var writer = new StreamWriter(output, Utf8NoBom);
                    await writer.WriteAsync(NormalizeLineEndings(args.Content)).ConfigureAwait(false);
                    await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(args.FileHash))
                        throw new InvalidOperationException("REJECT write_file: file_hash is required when overwriting an existing file.");

                    using var writableFile = FileToolRuntime.OpenWrite(path, cancellationToken);
                    var actualHash = await writableFile.ComputeFileHashAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(args.FileHash, actualHash, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"REJECT write_file: file_hash mismatch, expected {args.FileHash}, actual {actualHash}.");

                    await writableFile.ReplaceBytes(0, writableFile.Length,
                        Encoding.UTF8.GetBytes(NormalizeLineEndings(args.Content))).ConfigureAwait(false);
                    var overwrittenHash = await writableFile.ComputeFileHashAsync(CancellationToken.None).ConfigureAwait(false);
                    return new FileMutationResponse { Success = true, FileHash = overwrittenHash };
                }

                using var writtenFile = FileToolRuntime.OpenRead(path, cancellationToken);
                var fileHash = await writtenFile.ComputeFileHashAsync(CancellationToken.None).ConfigureAwait(false);
                return new FileMutationResponse { Success = true, FileHash = fileHash };
            }
        }
        catch (OperationCanceledException)
        {
            return new FileMutationResponse { Success = false, Error = "write_file canceled." };
        }
        catch (Exception ex)
        {
            return new FileMutationResponse { Success = false, Error = ex.Message };
        }
    }

    private static FileSystemEntry CreateEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        return new FileSystemEntry
        {
            Name = info.Name,
            Path = WorkspacePathResolver.ToWorkspaceRelativePath(path),
            Type = isLink ? "link" : isDirectory ? "directory" : "file",
            Size = isDirectory ? 0 : ((FileInfo)info).Length,
            ModifiedAt = info.LastWriteTimeUtc,
            IsReadOnly = (attributes & FileAttributes.ReadOnly) != 0,
            IsHidden = (attributes & FileAttributes.Hidden) != 0,
            LinkTarget = isLink ? info.LinkTarget : null
        };
    }

    private static string NormalizeLineEndings(string content) => content.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string EncodeCursor(int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"v1:{offset}"));

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return 0;

        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (!value.StartsWith("v1:", StringComparison.Ordinal) ||
                !int.TryParse(value[3..], out var offset) || offset < 0)
                throw new ArgumentException("cursor is invalid.", nameof(cursor));
            return offset;
        }
        catch (FormatException)
        {
            throw new ArgumentException("cursor is invalid.", nameof(cursor));
        }
    }

    private sealed class NaturalNameComparer : IComparer<string>
    {
        public static NaturalNameComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;

            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
                {
                    var leftEnd = ConsumeDigits(left, leftIndex);
                    var rightEnd = ConsumeDigits(right, rightIndex);
                    var numberComparison = CompareNumberRuns(left[leftIndex..leftEnd], right[rightIndex..rightEnd]);
                    if (numberComparison != 0) return numberComparison;
                    leftIndex = leftEnd;
                    rightIndex = rightEnd;
                    continue;
                }

                var comparison = char.ToUpperInvariant(left[leftIndex]).CompareTo(char.ToUpperInvariant(right[rightIndex]));
                if (comparison != 0) return comparison;
                leftIndex++;
                rightIndex++;
            }

            return left.Length.CompareTo(right.Length);
        }

        private static int ConsumeDigits(string value, int index)
        {
            while (index < value.Length && char.IsDigit(value[index])) index++;
            return index;
        }

        private static int CompareNumberRuns(string left, string right)
        {
            var leftTrimmed = left.TrimStart('0');
            var rightTrimmed = right.TrimStart('0');
            leftTrimmed = leftTrimmed.Length == 0 ? "0" : leftTrimmed;
            rightTrimmed = rightTrimmed.Length == 0 ? "0" : rightTrimmed;
            var lengthComparison = leftTrimmed.Length.CompareTo(rightTrimmed.Length);
            if (lengthComparison != 0) return lengthComparison;
            var valueComparison = string.CompareOrdinal(leftTrimmed, rightTrimmed);
            return valueComparison != 0 ? valueComparison : left.Length.CompareTo(right.Length);
        }
    }
}
