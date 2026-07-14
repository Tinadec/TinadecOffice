using System.Globalization;

namespace TinadecTools.Tools.Git;

// ── diff / name-status / numstat 解析 ─────────────────────────────────────────
// 输入来自 git diff-tree --name-status --numstat --patch -M 等。

internal static class DiffParser
{
    // ── name-status ────────────────────────────────────────────────────────────

    /// <summary>解析 `git diff-tree --name-status -M` 输出</summary>
    public static List<(string Status, int Score, string? OldPath, string NewPath)> ParseNameStatus(string output)
    {
        var result = new List<(string, int, string?, string)>();
        if (output.Contains('\0'))
        {
            var records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < records.Length;)
            {
                var statusField = records[index++];
                if (statusField.Length > 1 && statusField[0] is 'R' or 'C')
                {
                    var oldPath = index < records.Length ? records[index++] : string.Empty;
                    var newPath = index < records.Length ? records[index++] : oldPath;
                    result.Add((statusField[..1], int.Parse(statusField[1..], CultureInfo.InvariantCulture), oldPath, newPath));
                }
                else
                {
                    var newPath = index < records.Length ? records[index++] : string.Empty;
                    result.Add((statusField, 0, statusField == "A" ? null : newPath, newPath));
                }
            }
            return result;
        }

        foreach (var line in output.Split("\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrEmpty(line))
                continue;
            var parts = line.Split('\t');
            var statusField = parts[0];

            if (statusField.Length > 1 && (statusField[0] is 'R' or 'C'))
            {
                var status = statusField[0].ToString();
                var score = int.Parse(statusField[1..], CultureInfo.InvariantCulture);
                var oldPath = parts.Length > 1 ? parts[1] : null;
                var newPath = parts.Length > 2 ? parts[2] : parts[1];
                result.Add((status, score, oldPath, newPath));
            }
            else
            {
                var status = statusField;
                string? oldPath;
                var newPath = parts.Length > 1 ? parts[1] : string.Empty;
                oldPath = status switch
                {
                    "A" => null,
                    "D" => newPath,
                    _ => newPath, // M, T, U
                };
                result.Add((status, 0, oldPath, newPath));
            }
        }

        return result;
    }

    // ── numstat ────────────────────────────────────────────────────────────────

    /// <summary>解析 `git diff-tree --numstat -M` 输出，与 name-status 按行对齐</summary>
    public static List<(int Additions, int Deletions, bool IsBinary)> ParseNumstat(string output)
    {
        var result = new List<(int, int, bool)>();
        if (output.Contains('\0'))
        {
            var records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < records.Length; index++)
            {
                var parts = records[index].Split('\t');
                if (parts.Length < 2) continue;
                var isBinary = parts[0] == "-" && parts[1] == "-";
                var add = isBinary ? 0 : int.Parse(parts[0], CultureInfo.InvariantCulture);
                var del = isBinary ? 0 : int.Parse(parts[1], CultureInfo.InvariantCulture);
                if (parts.Length > 2 && string.IsNullOrEmpty(parts[2])) index += 2;
                result.Add((add, del, isBinary));
            }
            return result;
        }

        foreach (var line in output.Split("\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrEmpty(line))
            {
                result.Add((0, 0, false));
                continue;
            }
            var parts = line.Split('\t');
            if (parts.Length < 2)
            {
                result.Add((0, 0, false));
                continue;
            }

            var isBinary = parts[0] == "-" && parts[1] == "-";
            var add = isBinary ? 0 : int.Parse(parts[0], CultureInfo.InvariantCulture);
            var del = isBinary ? 0 : int.Parse(parts[1], CultureInfo.InvariantCulture);
            result.Add((add, del, isBinary));
        }

        return result;
    }

    /// <summary>合并 name-status + numstat → GitFileChange 列表</summary>
    public static List<GitFileChange> MergeFileChanges(
        List<(string Status, int Score, string? OldPath, string NewPath)> nameStatus,
        List<(int Additions, int Deletions, bool IsBinary)> numstat)
    {
        var files = new List<GitFileChange>();
        var count = Math.Max(nameStatus.Count, numstat.Count);
        for (var i = 0; i < count; i++)
        {
            var ns = i < nameStatus.Count ? nameStatus[i] : (Status: "M", Score: 0, OldPath: (string?)null, NewPath: string.Empty);
            var stat = i < numstat.Count ? numstat[i] : (Additions: 0, Deletions: 0, IsBinary: false);
            files.Add(new GitFileChange
            {
                Status = ns.Status,
                Score = ns.Score,
                OldPath = ns.OldPath,
                NewPath = ns.NewPath,
                Additions = stat.Additions,
                Deletions = stat.Deletions,
                IsBinary = stat.IsBinary
            });
        }

        return files;
    }

    // ── patch (unified diff) ───────────────────────────────────────────────────

    /// <summary>解析 unified diff 输出为 GitPatchFile 列表</summary>
    public static List<GitPatchFile> ParsePatch(string output)
    {
        var files = new List<GitPatchFile>();
        if (string.IsNullOrEmpty(output))
            return files;

        var lines = output.Split('\n');
        GitPatchFile? current = null;
        GitDiffHunk? hunk = null;
        var oldLine = 0;
        var newLine = 0;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (current is not null)
                    files.Add(current);
                current = new GitPatchFile();
                hunk = null;
                // paths from --- /+++ 更可靠，这里先占位
                var (op, np) = ParseDiffGitPaths(line);
                current.OldPath = op;
                current.NewPath = np;
                continue;
            }

            if (current is null)
                continue;

            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                current.OldPath = StripPathPrefix(line[4..]);
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                current.NewPath = StripPathPrefix(line[4..]);
                continue;
            }

            if (line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                line.StartsWith("Binary file ", StringComparison.Ordinal))
            {
                current.IsBinary = true;
                hunk = null;
                continue;
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                hunk = ParseHunkHeader(line);
                current.Hunks.Add(hunk);
                oldLine = hunk.OldStart;
                newLine = hunk.NewStart;
                continue;
            }

            if (hunk is null)
                continue;

            if (line.Length == 0)
            {
                // 空行视作 context
                hunk.Lines.Add(new GitDiffLine
                {
                    Type = "context",
                    OldLineNumber = oldLine > 0 ? oldLine : null,
                    NewLineNumber = newLine > 0 ? newLine : null,
                    Content = string.Empty
                });
                if (oldLine > 0) oldLine++;
                if (newLine > 0) newLine++;
                continue;
            }

            switch (line[0])
            {
                case '+':
                    hunk.Lines.Add(new GitDiffLine { Type = "add", NewLineNumber = newLine++, Content = line[1..] });
                    break;
                case '-':
                    hunk.Lines.Add(new GitDiffLine { Type = "delete", OldLineNumber = oldLine++, Content = line[1..] });
                    break;
                case '\\':
                    // "\ No newline at end of file" — 忽略
                    break;
                default:
                    hunk.Lines.Add(new GitDiffLine
                    {
                        Type = "context",
                        OldLineNumber = oldLine > 0 ? oldLine++ : null,
                        NewLineNumber = newLine > 0 ? newLine++ : null,
                        Content = line.Length > 0 ? line[1..] : string.Empty
                    });
                    break;
            }
        }

        if (current is not null)
            files.Add(current);

        // 修正：add/delete 文件 path
        foreach (var f in files)
        {
            if (string.IsNullOrEmpty(f.OldPath))
                f.OldPath = f.NewPath;
            if (string.IsNullOrEmpty(f.NewPath))
                f.NewPath = f.OldPath;
        }

        return files;
    }

    private static GitDiffHunk ParseHunkHeader(string line)
    {
        // @@ -old_start,old_count +new_start,new_count @@ ...
        var at = line.IndexOf("@@", 3, StringComparison.Ordinal);
        var body = at > 0 ? line[3..at].Trim() : line[3..].Trim();
        // body: "-a,b +c,d"
        var parts = body.Split(" ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var oldPart = parts.Length > 0 ? parts[0].TrimStart('-') : "1,1";
        var newPart = parts.Length > 1 ? parts[1].TrimStart('+') : "1,1";

        var (oldStart, oldCount) = ParseRange(oldPart);
        var (newStart, newCount) = ParseRange(newPart);

        return new GitDiffHunk
        {
            OldStart = oldStart,
            OldCount = oldCount,
            NewStart = newStart,
            NewCount = newCount
        };
    }

    private static (int start, int count) ParseRange(string s)
    {
        var comma = s.IndexOf(',');
        if (comma < 0)
            return (int.Parse(s, CultureInfo.InvariantCulture), 1);
        var start = int.Parse(s[..comma], CultureInfo.InvariantCulture);
        var count = int.Parse(s[(comma + 1)..], CultureInfo.InvariantCulture);
        return (start, count);
    }

    private static string StripPathPrefix(string path)
    {
        if (path == "/dev/null")
            return string.Empty;
        if (path.StartsWith("a/", StringComparison.Ordinal))
            return path[2..];
        if (path.StartsWith("b/", StringComparison.Ordinal))
            return path[2..];
        return path;
    }

    private static (string oldPath, string newPath) ParseDiffGitPaths(string line)
    {
        // diff --git a/old b/new
        var rest = line["diff --git ".Length..];
        var bIdx = rest.IndexOf(" b/", StringComparison.Ordinal);
        if (bIdx < 0)
            return (rest, rest);
        var oldRaw = rest[..bIdx];
        var newRaw = rest[(bIdx + 3)..];
        return (StripPathPrefix(oldRaw), StripPathPrefix(newRaw));
    }
}
