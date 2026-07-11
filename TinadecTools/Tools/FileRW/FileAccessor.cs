using System.Buffers;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NLog;
using UtfUnknown;

namespace TinadecTools.Tools.FileRW;

/// <summary>
/// 一行的索引
/// </summary>
/// <param name="LineStart">本行开始的偏移</param>
/// <param name="LineEnd">本行结束的偏移（不包含换行符）</param>
/// <param name="NextStart">下一行开始的偏移</param>
internal record struct LineSpan(
    long LineStart,
    long LineEnd,
    long NextStart
);

/// <summary>
/// 一行的内容
/// </summary>
/// <param name="Content">具体内容（不包括换行符）</param>
/// <param name="LineNumber">行号</param>
/// <param name="StartOffset">本行起始字节偏移</param>
/// <param name="EndOffset">本行结束字节偏移（排他，包含换行符）</param>
public record struct LineContent(
    string Content,
    int LineNumber,
    long StartOffset,
    long EndOffset
);

internal class FileAccessor : IDisposable
{
    private readonly FileStream _file;
    private List<LineSpan> _index = new();
    private readonly string _filepath;
    private readonly SafeFileHandle _handle;
    private readonly bool _canWrite;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private const int StreamBufferSize = 128 * 1024;
    private const int TextBufferSize = 16 * 1024;

    public int LineCount => _index.Count;

    public long Length => _file.Length;

    static FileAccessor()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// 打开一个文件。
    /// </summary>
    /// <param name="filepath">文件路径</param>
    /// <param name="canWrite">是否以可写模式打开。</param>
    internal FileAccessor(string filepath, bool canWrite = true)
    {
        _filepath = filepath;
        _canWrite = canWrite;
        _file = new FileStream(filepath, FileMode.Open, canWrite ? FileAccess.ReadWrite : FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024,
            FileOptions.Asynchronous);
        _handle = _file.SafeFileHandle;
        if (_canWrite)
            NormalizeTextFile();
        BuildIndex();
    }

    /// <summary>
    /// 打开文件时流式检测全文编码，并静默保存为 UTF-8（无 BOM）+ LF。
    /// </summary>
    private void NormalizeTextFile()
    {
        if (_file.Length == 0)
            return;

        _file.Seek(0, SeekOrigin.Begin);
        var detection = CharsetDetector.DetectFromStream(_file);
        var detected = detection.Detected;
        var encoding = detected?.Encoding ?? Encoding.UTF8;

        if (IsUtf8Compatible(encoding) &&
            detected?.HasBOM != true &&
            !ContainsCarriageReturn())
        {
            _file.Seek(0, SeekOrigin.Begin);
            return;
        }

        var tempPath = GetTempPath();
        try
        {
            WriteNormalizedTempFile(tempPath, encoding);

            _file.SetLength(0);
            _file.Seek(0, SeekOrigin.Begin);
            using var tempInput = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                StreamBufferSize, FileOptions.SequentialScan);
            tempInput.CopyTo(_file, StreamBufferSize);
            _file.Flush();
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (IOException ex)
            {
                Logger.Warn(ex, $"删除临时文件 {tempPath} 失败");
            }

            _file.Seek(0, SeekOrigin.Begin);
        }
    }

    private static bool IsUtf8Compatible(Encoding encoding)
    {
        return encoding.CodePage == Encoding.UTF8.CodePage ||
               encoding.CodePage == Encoding.ASCII.CodePage;
    }

    private string GetTempPath()
    {
        var directory = Path.GetDirectoryName(_filepath);
        var filename = Path.GetFileName(_filepath);
        return Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            $".{filename}.{Guid.NewGuid():N}.tmp");
    }

    private bool ContainsCarriageReturn()
    {
        _file.Seek(0, SeekOrigin.Begin);

        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            while (true)
            {
                var bytesRead = _file.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                    return false;

                for (var i = 0; i < bytesRead; i++)
                    if (buffer[i] == '\r')
                        return true;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _file.Seek(0, SeekOrigin.Begin);
        }
    }

    private void WriteNormalizedTempFile(string tempPath, Encoding encoding)
    {
        _file.Seek(0, SeekOrigin.Begin);

        using var reader = new StreamReader(_file, encoding, false, StreamBufferSize, true);
        using var tempOutput = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            StreamBufferSize, FileOptions.SequentialScan);
        using var writer = new StreamWriter(tempOutput, Utf8NoBom, StreamBufferSize);
        var buffer = ArrayPool<char>.Shared.Rent(TextBufferSize);
        var firstChar = true;
        var previousWasCr = false;

        try
        {
            while (true)
            {
                var charsRead = reader.Read(buffer, 0, buffer.Length);
                if (charsRead == 0)
                    break;

                for (var i = 0; i < charsRead; i++)
                {
                    var ch = buffer[i];

                    if (firstChar)
                    {
                        firstChar = false;
                        if (ch == '\uFEFF')
                            continue;
                    }

                    if (previousWasCr)
                    {
                        previousWasCr = false;
                        if (ch == '\n')
                            continue;
                    }

                    if (ch == '\r')
                    {
                        writer.Write('\n');
                        previousWasCr = true;
                        continue;
                    }

                    writer.Write(ch);
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 手动建立行号偏移索引
    /// </summary>
    private void BuildIndex()
    {
        _index.Clear();
        _file.Seek(0, SeekOrigin.Begin);

        if (_file.Length == 0)
            return;

        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            long currentOffset = 0;
            long lineStart = 0;

            while (true)
            {
                var bytesRead = _file.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                    break;

                for (var i = 0; i < bytesRead; i++)
                {
                    currentOffset++;
                    if (buffer[i] == '\n')
                    {
                        _index.Add(new LineSpan(lineStart, currentOffset - 1, currentOffset));
                        lineStart = currentOffset;
                    }
                }
            }

            if (lineStart < _file.Length)
                _index.Add(new LineSpan(lineStart, _file.Length, _file.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _file.Seek(0, SeekOrigin.Begin);
        }
    }

    /// <summary>
    /// 读取多个行范围（每个范围包括起始和结束行）
    /// </summary>
    /// <param name="lineRanges">行范围列表，Key=起始行，Value=结束行（都包括）</param>
    /// <returns></returns>
    public async Task<List<LineContent>> ReadLines(List<KeyValuePair<int, int>> lineRanges)
    {
        var contents = new List<LineContent>();

        foreach (var range in lineRanges)
        {
            var startLine = range.Key;
            var endLine = range.Value;

            // 验证范围
            if (startLine < 0 || startLine >= _index.Count)
            {
                Logger.Warn($"起始行号 {startLine} 超出范围 [0, {_index.Count})");
                continue;
            }

            if (endLine < 0 || endLine >= _index.Count)
            {
                Logger.Warn($"结束行号 {endLine} 超出范围 [0, {_index.Count})");
                continue;
            }

            if (startLine > endLine)
            {
                Logger.Warn($"起始行号 {startLine} 大于结束行号 {endLine}");
                continue;
            }

            // 读取这个范围内的所有行
            for (var line = startLine; line <= endLine; line++)
            {
                var span = _index[line];
                var length = span.LineEnd - span.LineStart;

                if (length <= 0)
                {
                    contents.Add(new LineContent(
                        string.Empty,
                        line,
                        span.LineStart,
                        span.NextStart));
                    continue;
                }

                var buf = ArrayPool<byte>.Shared.Rent((int)length);
                try
                {
                    var memory = buf.AsMemory(0, (int)length);
                    var bytesRead =
                        await RandomAccess.ReadAsync(_handle, memory, span.LineStart, CancellationToken.None);

                    if (bytesRead < length)
                        Logger.Warn($"读取 {_filepath} 偏移 {span.LineStart} 预期 {length} 字节，实际 {bytesRead}");

                    var content = Encoding.UTF8.GetString(buf, 0, bytesRead);
                    contents.Add(new LineContent(
                        content,
                        line,
                        span.LineStart,
                        span.NextStart));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }
        }

        return contents;
    }

    public async Task<string> ComputeFileHashAsync(CancellationToken cancellationToken = default)
    {
        if (_file.Length > int.MaxValue)
            throw new InvalidOperationException($"文件 {_filepath} 太大，无法计算内存内哈希");

        var bytes = new byte[(int)_file.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var bytesRead = await RandomAccess.ReadAsync(_handle, bytes.AsMemory(offset), offset, cancellationToken);
            if (bytesRead == 0)
                break;

            offset += bytesRead;
        }

        return FileHashing.ComputeFileHash(bytes.AsSpan(0, offset));
    }

    /// <summary>
    /// 按字节偏移替换一段内容，替换后的长度可以与原长度不同。
    /// </summary>
    /// <param name="byteOffset">起始字节偏移</param>
    /// <param name="byteCount">要被替换掉的原始字节数</param>
    /// <param name="replacement">替换内容</param>
    public async Task<bool> ReplaceBytes(long byteOffset, long byteCount, ReadOnlyMemory<byte> replacement)
    {
        EnsureWritable();
        if (byteOffset < 0 || byteOffset > _file.Length)
            throw new ArgumentOutOfRangeException(nameof(byteOffset), $"字节偏移 {byteOffset} 超出范围 [0, {_file.Length}]");

        if (byteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(byteCount), "字节数量不能小于 0");

        if (byteCount > _file.Length - byteOffset)
            throw new ArgumentOutOfRangeException(nameof(byteCount), $"字节数量 {byteCount} 超出范围，无法从偏移 {byteOffset} 开始替换");

        if (byteCount == 0 && replacement.Length == 0)
            return true;

        if (byteCount == replacement.Length)
            await OverwriteBytesInPlaceAsync(byteOffset, replacement);
        else
            await RewriteFileWithSegmentAsync(byteOffset, byteCount, replacement);

        BuildIndex();
        return true;
    }

    /// <summary>
    /// 删除指定字节段。
    /// </summary>
    public Task<bool> DeleteBytes(long byteOffset, long byteCount)
    {
        return ReplaceBytes(byteOffset, byteCount, ReadOnlyMemory<byte>.Empty);
    }

    /// <summary>
    /// 在指定字节偏移插入内容。
    /// </summary>
    public Task<bool> InsertBytes(long byteOffset, ReadOnlyMemory<byte> insertion)
    {
        return ReplaceBytes(byteOffset, 0, insertion);
    }

    /// <summary>
    /// 删除单行。
    /// </summary>
    public Task<bool> DeleteLine(int lineNumber)
    {
        return DeleteLines(lineNumber, lineNumber);
    }

    /// <summary>
    /// 删除连续多行。
    /// </summary>
    public Task<bool> DeleteLines(int startLine, int endLine)
    {
        var startSpan = GetLineSpanOrThrow(startLine);
        var endSpan = GetLineSpanOrThrow(endLine);

        if (startLine > endLine)
            throw new ArgumentException($"起始行号 {startLine} 不能大于结束行号 {endLine}");

        return ReplaceBytes(startSpan.LineStart, endSpan.NextStart - startSpan.LineStart, ReadOnlyMemory<byte>.Empty);
    }

    /// <summary>
    /// 在指定行后插入字节。
    /// </summary>
    public Task<bool> InsertBytesAfterLine(int lineNumber, ReadOnlyMemory<byte> insertion)
    {
        return InsertBytes(GetLineSpanOrThrow(lineNumber).NextStart, insertion);
    }

    /// <summary>
    /// 在指定行前插入字节。
    /// </summary>
    public Task<bool> InsertBytesBeforeLine(int lineNumber, ReadOnlyMemory<byte> insertion)
    {
        return InsertBytes(GetLineSpanOrThrow(lineNumber).LineStart, insertion);
    }

    /// <summary>
    /// 在指定行后插入若干行。
    /// </summary>
    public Task<bool> InsertLinesAfterLine(int lineNumber, IReadOnlyList<string> lines)
    {
        return InsertBytesAfterLine(lineNumber, EncodeLinesToUtf8Bytes(lines, true));
    }

    /// <summary>
    /// 在指定行前插入若干行。
    /// </summary>
    public Task<bool> InsertLinesBeforeLine(int lineNumber, IReadOnlyList<string> lines)
    {
        return InsertBytesBeforeLine(lineNumber, EncodeLinesToUtf8Bytes(lines, true));
    }

    /// <summary>
    /// 在两行之间插入字节。
    /// </summary>
    public Task<bool> InsertBytesBetweenLines(int upperLineNumber, int lowerLineNumber, ReadOnlyMemory<byte> insertion)
    {
        if (upperLineNumber < 0 || upperLineNumber >= _index.Count)
            throw new ArgumentOutOfRangeException(nameof(upperLineNumber),
                $"行号 {upperLineNumber} 超出范围 [0, {_index.Count})");

        if (lowerLineNumber < 0 || lowerLineNumber >= _index.Count)
            throw new ArgumentOutOfRangeException(nameof(lowerLineNumber),
                $"行号 {lowerLineNumber} 超出范围 [0, {_index.Count})");

        if (upperLineNumber >= lowerLineNumber)
            throw new ArgumentException($"上边行号 {upperLineNumber} 必须小于下边行号 {lowerLineNumber}");

        return InsertBytes(_index[upperLineNumber].NextStart, insertion);
    }

    /// <summary>
    /// 在两行之间插入若干行。
    /// </summary>
    public Task<bool> InsertLinesBetweenLines(int upperLineNumber, int lowerLineNumber, IReadOnlyList<string> lines)
    {
        return InsertBytesBetweenLines(upperLineNumber, lowerLineNumber, EncodeLinesToUtf8Bytes(lines, true));
    }

    /// <summary>
    /// 替换单个连续范围的行（写入后全量重建索引）
    /// </summary>
    /// <param name="startLine">起始行号（包括）</param>
    /// <param name="endLine">结束行号（包括）</param>
    /// <param name="content">新内容，按行传入且不包含换行符</param>
    public async Task<bool> ReplaceLines(int startLine, int endLine, IReadOnlyList<string> content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (startLine < 0 || startLine >= _index.Count)
            throw new ArgumentOutOfRangeException(nameof(startLine), $"起始行号 {startLine} 超出范围 [0, {_index.Count})");

        if (endLine < 0 || endLine >= _index.Count)
            throw new ArgumentOutOfRangeException(nameof(endLine), $"结束行号 {endLine} 超出范围 [0, {_index.Count})");

        if (startLine > endLine)
            throw new ArgumentException($"起始行号 {startLine} 不能大于结束行号 {endLine}");

        var expectedCount = endLine - startLine + 1;
        if (content.Count != expectedCount)
            throw new ArgumentException($"内容行数 {content.Count} 与范围行数 {expectedCount} 不匹配");

        for (var i = 0; i < content.Count; i++)
        {
            var line = content[i];
            if (line.Contains('\n') || line.Contains('\r'))
                throw new ArgumentException($"内容行 {startLine + i} 包含换行符", nameof(content));
        }

        var startSpan = _index[startLine];
        var endSpan = _index[endLine];
        var replacementBytes = EncodeLinesToUtf8Bytes(content, endSpan.NextStart > endSpan.LineEnd);

        return await ReplaceBytes(startSpan.LineStart, endSpan.NextStart - startSpan.LineStart, replacementBytes);
    }

    private LineSpan
        GetLineSpanOrThrow(int lineNumber)
    {
        if (lineNumber < 0 || lineNumber >= _index.Count)
            throw new ArgumentOutOfRangeException(nameof(lineNumber), $"行号 {lineNumber} 超出范围 [0, {_index.Count})");

        return _index[lineNumber];
    }

    private static ReadOnlyMemory<byte> EncodeLinesToUtf8Bytes(IReadOnlyList<string> lines, bool trailingNewline)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
            return ReadOnlyMemory<byte>.Empty;

        using var memoryStream = new MemoryStream();
        using (var writer = new StreamWriter(memoryStream, Utf8NoBom, StreamBufferSize, true))
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line is null)
                    throw new ArgumentNullException(nameof(lines));

                if (line.Contains('\n') || line.Contains('\r'))
                    throw new ArgumentException($"内容行 {i} 包含换行符", nameof(lines));

                writer.Write(line);

                if (i < lines.Count - 1 || trailingNewline)
                    writer.Write('\n');
            }

            writer.Flush();
        }

        return memoryStream.ToArray();
    }

    private async Task OverwriteBytesInPlaceAsync(long offset, ReadOnlyMemory<byte> replacement)
    {
        if (replacement.Length == 0)
            return;

        _file.Seek(offset, SeekOrigin.Begin);
        await _file.WriteAsync(replacement, CancellationToken.None);
        await _file.FlushAsync(CancellationToken.None);
        _file.Seek(0, SeekOrigin.Begin);
    }

    private async Task RewriteFileWithSegmentAsync(long removeOffset, long removeLength,
        ReadOnlyMemory<byte> replacement)
    {
        var originalLength = _file.Length;
        var tempPath = GetTempPath();

        try
        {
            await using (var tempOutput = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             StreamBufferSize, FileOptions.SequentialScan))
            {
                await CopyRangeToStreamAsync(tempOutput, 0, removeOffset);
                if (!replacement.IsEmpty)
                    await tempOutput.WriteAsync(replacement, CancellationToken.None);
                await CopyRangeToStreamAsync(tempOutput, removeOffset + removeLength,
                    originalLength - (removeOffset + removeLength));
                await tempOutput.FlushAsync();
            }

            _file.SetLength(0);
            _file.Seek(0, SeekOrigin.Begin);
            await using var tempInput = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                StreamBufferSize, FileOptions.SequentialScan);
            await tempInput.CopyToAsync(_file, StreamBufferSize, CancellationToken.None);
            await _file.FlushAsync(CancellationToken.None);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (IOException ex)
            {
                Logger.Warn(ex, $"删除临时文件 {tempPath} 失败");
            }

            _file.Seek(0, SeekOrigin.Begin);
        }
    }

    private async Task CopyRangeToStreamAsync(Stream destination, long sourceOffset, long length)
    {
        if (length <= 0)
            return;

        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            var currentOffset = sourceOffset;
            var remaining = length;

            while (remaining > 0)
            {
                var chunk = (int)Math.Min(buffer.Length, remaining);
                var bytesRead = await RandomAccess.ReadAsync(_handle, buffer.AsMemory(0, chunk), currentOffset,
                    CancellationToken.None);
                if (bytesRead == 0)
                    break;

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), CancellationToken.None);
                currentOffset += bytesRead;
                remaining -= bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        _file.Dispose();
    }

    private void EnsureWritable()
    {
        if (!_canWrite)
            throw new InvalidOperationException("The file was opened read-only.");
    }
}
