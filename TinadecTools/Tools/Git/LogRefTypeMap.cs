namespace TinadecTools.Tools.Git;

// ponytail: 用 git for-each-ref 一次性建 short 名 → 类型的只读字典，给 log parser 查表精确分类.
internal static class LogRefTypeMap
{
    public static async Task<IReadOnlyDictionary<string, string>> LoadAsync(string repo, CancellationToken ct)
    {
        var exec = await GitCli.RunAsync(
            repo,
            ["for-each-ref", "--format=%(refname)\t%(refname:short)"],
            stdin: null,
            ct,
            timeoutMs: 10_000).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!exec.Ok)
            return map;
        foreach (var line in exec.Stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            var refname = line[..tab];
            var shortName = line[(tab + 1)..];
            string type;
            if (refname.StartsWith("refs/heads/", StringComparison.Ordinal)) type = "branch";
            else if (refname.StartsWith("refs/remotes/", StringComparison.Ordinal)) type = "remote";
            else if (refname.StartsWith("refs/tags/", StringComparison.Ordinal)) type = "tag";
            else continue;
            // 多个 ref 可能 short 名重复（remote x、本地同名），优先取更"本地"的；分支/远程冲突时 branch 优先.
            if (!map.TryGetValue(shortName, out var existing) || (existing == "remote" && type == "branch"))
                map[shortName] = type;
        }
        return map;
    }
}