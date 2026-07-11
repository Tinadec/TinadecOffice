using System.Globalization;

namespace TinadecTools.Tools.Git;

// ── git log 输出解析 ──────────────────────────────────────────────────────────
// 格式: git log -z --format=%H%x1f%h%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cI%x1f%s%x1f%D <revs>
// -z: commit 之间用 NUL 分隔; 字段之间用 \x1f 分隔.

internal static class GitLogParser
{
    private const char FieldSep = '\x1f';

    /// <summary>解析 git log -z 输出为 GitCommitSummary 列表（不含 lanes；lanes 由 LaneAssigner 后填）</summary>
    public static List<GitCommitSummary> ParseLog(string output)
    {
        var commits = new List<GitCommitSummary>();
        if (string.IsNullOrEmpty(output))
            return commits;

        var records = output.Split("\0", StringSplitOptions.RemoveEmptyEntries);
        foreach (var rec in records)
        {
            var fields = rec.Split(FieldSep);
            if (fields.Length < 9)
                continue;

            var commit = new GitCommitSummary
            {
                Hash = fields[0],
                ShortHash = fields[1],
                Parents = fields[2].Split(" ", StringSplitOptions.RemoveEmptyEntries).ToList(),
                Author = fields[3],
                AuthorEmail = fields[4],
                AuthorDate = fields[5],
                CommitterDate = fields[6],
                Subject = fields[7],
                Refs = ParseDecorations(fields[8])
            };
            commits.Add(commit);
        }

        return commits;
    }

    /// <summary>解析 %D 装饰字符串为结构化 refs</summary>
    public static List<GitRef> ParseDecorations(string decorations)
    {
        var refs = new List<GitRef>();
        if (string.IsNullOrWhiteSpace(decorations))
            return refs;

        // ponytail: 含 "/" 的 ref 当作 remote；本地分支也可能含 "/"，准确性有上限，
        // 精确分类需要 git for-each-ref，当前足够结构正确。
        foreach (var raw in decorations.Split(",", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("tag: ", StringComparison.Ordinal))
            {
                refs.Add(new GitRef { Name = raw[5..], Type = "tag" });
            }
            else if (raw.Contains(" -> ", StringComparison.Ordinal))
            {
                var parts = raw.Split(" -> ", 2, StringSplitOptions.None);
                refs.Add(new GitRef { Name = parts[0], Type = "head", IsHead = true });
                refs.Add(new GitRef { Name = parts[1], Type = "branch" });
            }
            else if (raw == "HEAD")
            {
                refs.Add(new GitRef { Name = "HEAD", Type = "head", IsHead = true });
            }
            else if (raw.Contains('/'))
            {
                refs.Add(new GitRef { Name = raw, Type = "remote" });
            }
            else
            {
                refs.Add(new GitRef { Name = raw, Type = "branch" });
            }
        }

        return refs;
    }
}
