using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinadecTools.Tools.FileRW;

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
internal partial class RgJsonContext : JsonSerializerContext;

// ── RipgrepRunner ─────────────────────────────────────────────────────────────

internal static class RipgrepRunner
{
    public const string RgPathEnvVar = "TINADEC_TOOLS_RG_PATH";

    public static string ResolveRgPath()
    {
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
        var searchPath = WorkspacePathResolver.ResolveDirectory(args.Path);
        var rgPath = ResolveRgPath();
        if (!File.Exists(rgPath))
            return Fail($"ripgrep not found at '{rgPath}'. Set {RgPathEnvVar} or place rg next to the executable.");

        var psi = BuildProcessStartInfo(rgPath, args, searchPath);
        using var process = new Process { StartInfo = psi };
        process.Start();

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
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
            CreateNoWindow         = true
        };

        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add("--no-follow");

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

        psi.ArgumentList.Add("--"); // 防止 pattern 被解析为 flag
        psi.ArgumentList.Add(args.Pattern);
        psi.ArgumentList.Add(searchPath);

        return psi;
    }

    private static FileSearchLine? ToSearchLine(RgData? data, bool isMatch)
    {
        if (data is null) return null;
        var filePath   = data.Path?.Text;
        var content    = data.Lines?.Text?.TrimEnd('\n', '\r');
        var lineNumber = data.LineNumber;
        if (filePath is null || content is null || lineNumber is null) return null;

        var result = new FileSearchLine
        {
            FilePath   = filePath,
            LineNumber = lineNumber.Value,
            Content    = content,
            IsMatch    = isMatch
        };

        if (isMatch && data.Submatches is { Count: > 0 })
        {
            result.Submatches = data.Submatches
                .Select(s => new MatchSpan
                {
                    Start = s.Start,
                    End   = s.End,
                    Text  = s.Match?.Text ?? string.Empty
                })
                .ToList();
        }

        return result;
    }

    private static FileSearchResponse Fail(string error) =>
        new() { Success = false, Error = error };

    private static void KillSafe(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }
}
