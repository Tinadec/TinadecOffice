using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinadecTools.Tools.FileRW;
using TinadecTools.Runtime;

namespace TinadecTools.Tools.Search;

// ── rg --json 内部模型（最小化，仅解析需要的字段）────────────────────────────

internal sealed class RgMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("data")] public RgData? Data { get; set; }
}

internal sealed class RgData
{
    [JsonPropertyName("path")]     public RgTextValue?     Path       { get; set; }
    [JsonPropertyName("lines")]    public RgTextValue?     Lines      { get; set; }
    [JsonPropertyName("line_number")] public int?          LineNumber { get; set; }
    [JsonPropertyName("submatches")] public List<RgSubmatch>? Submatches { get; set; }
    [JsonPropertyName("stats")]    public RgStats?         Stats      { get; set; }
}

internal sealed class RgTextValue
{
    [JsonPropertyName("text")] public string? Text { get; set; }
}

internal sealed class RgSubmatch
{
    [JsonPropertyName("match")] public RgTextValue? Match { get; set; }
    [JsonPropertyName("start")] public int Start { get; set; }
    [JsonPropertyName("end")]   public int End   { get; set; }
}

internal sealed class RgStats
{
    [JsonPropertyName("matched_lines")] public int MatchedLines { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(RgMessage))]
[JsonSerializable(typeof(RgData))]
[JsonSerializable(typeof(RgTextValue))]
[JsonSerializable(typeof(RgSubmatch))]
[JsonSerializable(typeof(RgStats))]
internal partial class RgJsonContext : JsonSerializerContext { }

// ── RipgrepRunner ─────────────────────────────────────────────────────────────

internal static class RipgrepRunner
{
    public const string RgPathEnvVar = "TINADEC_TOOLS_RG_PATH";

    public static string ResolveRgPath()
    {
        var configured = ToolExecutionContext.Current?.String("search", "rg_path");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var fromEnv = Environment.GetEnvironmentVariable(RgPathEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        var exe = OperatingSystem.IsWindows() ? "rg.exe" : "rg";
        var bundled = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(bundled)) return bundled;

        var pathEntries = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            ?? Array.Empty<string>();
        foreach (var entry in pathEntries)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var candidate = Path.Combine(entry.Trim(), exe);
            if (File.Exists(candidate)) return candidate;
        }

        return bundled;
    }

    public static async ValueTask<FileSearchResponse> RunAsync(
        FileSearchParams args,
        CancellationToken cancellationToken)
    {
        var callerCancellation = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (ToolExecutionContext.Current?.Setting("search", "timeout_ms") is { ValueKind: JsonValueKind.Number })
            deadline.CancelAfter(ToolExecutionContext.Current.Integer("search", "timeout_ms", 30_000, 1, 1_800_000));
        cancellationToken = deadline.Token;
        var searchPath = WorkspacePathResolver.ResolveDirectory(args.Path);
        var rgPath = ResolveRgPath();
        if (!File.Exists(rgPath))
            // Name every way out and say what is NOT affected: the failure used to read
            // like a broken tool, so a caller could not tell a missing optional
            // dependency from a defect.
            return Fail(
                $"file_search could not run: no ripgrep binary was found (looked for '{rgPath}'). "
                + $"Fix any ONE of: set {RgPathEnvVar} to an existing rg executable; place rg "
                + "(rg.exe on Windows) next to the TinadecTools executable; or put rg on PATH. "
                + "Binaries: https://github.com/BurntSushi/ripgrep/releases. "
                + "Every other tool works without ripgrep.");

        var psi = BuildProcessStartInfo(rgPath, args, searchPath);
        using var process = new Process { StartInfo = psi };
        process.Start();
        // rg reads files, never stdin, so the pipe is closed immediately: the child
        // must not keep a handle the host still uses for its own protocol.
        process.StandardInput.Close();

        // 并发读取 stderr，防止死锁
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var lines      = new List<FileSearchLine>();
        var matchFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchCount = 0;
        var truncated  = false;
        var totalCount = 0;

        try
        {
            string? rgLine;
            while ((rgLine = await process.StandardOutput.ReadLineAsync(cancellationToken)
                       .ConfigureAwait(false)) is not null)
            {
                var msg = JsonSerializer.Deserialize(rgLine, RgJsonContext.Default.RgMessage);
                if (msg is null) continue;

                switch (msg.Type)
                {
                    case "match":
                        var matchLine = ToSearchLine(msg.Data, isMatch: true);
                        if (matchLine is not null)
                        {
                            lines.Add(matchLine);
                            matchFiles.Add(matchLine.FilePath);
                            matchCount++;
                        }
                        if (matchCount >= args.MaxResults)
                        {
                            truncated = true;
                            KillSafe(process);
                            goto done;
                        }
                        break;

                    case "context":
                        var ctxLine = ToSearchLine(msg.Data, isMatch: false);
                        if (ctxLine is not null) lines.Add(ctxLine);
                        break;

                    case "summary":
                        totalCount = msg.Data?.Stats?.MatchedLines ?? 0;
                        break;
                }
            }
            done:;
        }
        catch (OperationCanceledException)
        {
            KillSafe(process);
            if (!callerCancellation.IsCancellationRequested)
                return Fail("file_search timed out under the frozen search.timeout_ms budget.");
            throw;
        }
        finally
        {
            KillSafe(process);
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        // exit code 1 = no matches (正常); 2 = error; killed 进程时也会产生非零退出码
        if (!truncated && process.ExitCode >= 2 && lines.Count == 0)
            return Fail(string.IsNullOrWhiteSpace(stderr)
                ? $"rg exited with code {process.ExitCode}."
                : stderr.Trim());

        // 计算命中文件的 file_hash（与 FileRW 工具算法一致）
        var fileHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fp in matchFiles)
        {
            try
            {
                var safePath = WorkspacePathResolver.ResolvePath(fp);
                var bytes = await File.ReadAllBytesAsync(safePath, cancellationToken).ConfigureAwait(false);
                fileHashes[fp] = FileHashing.ComputeFileHash(bytes);
            }
            catch
            {
                // best-effort，跳过不可读文件
            }
        }

        return new FileSearchResponse
        {
            Success          = true,
            Lines            = lines,
            FileHashes       = fileHashes,
            Truncated        = truncated,
            TotalMatchCount  = truncated ? Math.Max(totalCount, matchCount) : totalCount
        };
    }

    // ── 私有辅助 ──────────────────────────────────────────────────────────────

    private static ProcessStartInfo BuildProcessStartInfo(
        string rgPath, FileSearchParams args, string searchPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = rgPath,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            // Never let rg inherit the tool host's protocol stdin: a child holding
            // that live pipe handle can block its own exit and wedge the caller.
            RedirectStandardInput  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
            CreateNoWindow         = true
        };

        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add("--no-follow");
        if (args.IncludeHidden) psi.ArgumentList.Add("--hidden");
        if (!args.RespectIgnoreFiles) psi.ArgumentList.Add("--no-ignore");

        if (args.CaseSensitive) psi.ArgumentList.Add("--case-sensitive");
        else                    psi.ArgumentList.Add("--ignore-case");

        if (args.FixedStrings)
            psi.ArgumentList.Add("-F");

        if (args.ContextLines > 0)
        {
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(args.ContextLines.ToString());
        }

        if (!string.IsNullOrWhiteSpace(args.Glob))
        {
            psi.ArgumentList.Add("-g");
            psi.ArgumentList.Add(args.Glob);
        }

        if (!string.IsNullOrWhiteSpace(args.Type))
        {
            psi.ArgumentList.Add("--type");
            psi.ArgumentList.Add(args.Type);
        }

        var protectedPaths = ToolExecutionContext.Current?.ProjectStorageWrite == true
            ? WorkspaceStoragePolicy.ForbiddenRoots(WorkspacePathResolver.WorkspaceRoot)
            : WorkspaceStoragePolicy.ProtectedPaths(WorkspacePathResolver.WorkspaceRoot);
        {
            // Appended after user globs so --hidden/--no-ignore and a positive glob cannot
            // accidentally expose runtime databases, logs or retained package contents.
            foreach (var path in protectedPaths)
            {
                if (!WorkspaceRootSet.IsWithin(searchPath, path)) continue;
                var relative = Path.GetRelativePath(searchPath, path).Replace('\\', '/');
                psi.ArgumentList.Add("-g");
                psi.ArgumentList.Add("!" + relative);
                psi.ArgumentList.Add("-g");
                psi.ArgumentList.Add("!" + relative + "/**");
            }
        }

        psi.ArgumentList.Add("--"); // 防止 pattern 被解析为 flag
        psi.ArgumentList.Add(args.Pattern);
        psi.ArgumentList.Add(searchPath);

        return psi;
    }

    /// <summary>Longest line content returned; a longer line becomes a window around its first match.</summary>
    internal const int MaxLineChars = 400;

    /// <summary>A short pattern in a minified file can match thousands of times on one line.</summary>
    internal const int MaxSubmatchesPerLine = 16;

    private static FileSearchLine? ToSearchLine(RgData? data, bool isMatch)
    {
        if (data is null) return null;
        var filePath   = data.Path?.Text;
        var content    = data.Lines?.Text?.TrimEnd('\n', '\r');
        var lineNumber = data.LineNumber;
        if (filePath is null || content is null || lineNumber is null) return null;
        if (!WorkspacePathResolver.IsAllowed(filePath)) return null;

        var result = new FileSearchLine
        {
            FilePath   = filePath,
            LineNumber = lineNumber.Value,
            Content    = content,
            IsMatch    = isMatch
        };

        if (content.Length > MaxLineChars)
        {
            var anchor = isMatch && data.Submatches is { Count: > 0 } ? CharIndexOfByte(content, data.Submatches[0].Start) : 0;
            var (start, length) = ClipWindow(content, anchor, MaxLineChars);
            result.Content = content.Substring(start, length);
            result.ContentTruncated = true;
            result.ContentStart = start;
            result.LineLength = content.Length;
        }

        if (isMatch && data.Submatches is { Count: > 0 })
        {
            result.Submatches = data.Submatches
                .Take(MaxSubmatchesPerLine)
                .Select(s => new MatchSpan
                {
                    Start = s.Start,
                    End   = s.End,
                    Text  = Clip(s.Match?.Text ?? string.Empty)
                })
                .ToList();
        }

        return result;
    }

    /// <summary>
    /// Window of at most <paramref name="max"/> characters that keeps <paramref name="anchor"/> in
    /// view (a quarter of the window before it) and never splits a surrogate pair.
    /// </summary>
    internal static (int Start, int Length) ClipWindow(string line, int anchor, int max)
    {
        if (line.Length <= max) return (0, line.Length);
        var start = Math.Clamp(anchor - max / 4, 0, line.Length - max);
        if (char.IsLowSurrogate(line[start])) start++;
        var length = Math.Min(max, line.Length - start);
        if (start + length < line.Length && char.IsHighSurrogate(line[start + length - 1])) length--;
        return (start, length);
    }

    /// <summary>rg reports submatch offsets in bytes of the UTF-8 line.</summary>
    private static int CharIndexOfByte(string line, int byteOffset)
    {
        if (byteOffset <= 0) return 0;
        var bytes = Encoding.UTF8.GetBytes(line);
        return Encoding.UTF8.GetCharCount(bytes, 0, Math.Min(byteOffset, bytes.Length));
    }

    private static string Clip(string text)
    {
        if (text.Length <= MaxLineChars) return text;
        var (start, length) = ClipWindow(text, 0, MaxLineChars);
        return text.Substring(start, length);
    }

    private static FileSearchResponse Fail(string error) =>
        new() { Success = false, Error = error };

    private static void KillSafe(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }
}
