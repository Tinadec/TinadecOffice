using System.Text.Json.Serialization;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Git;

// ── git_log_list ──────────────────────────────────────────────────────────────

public sealed class GitLogListArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }

    /// <summary>rev / rev-range list, e.g. ["main", "A..B"]; defaults to HEAD</summary>
    [JsonPropertyName("revs")] public List<string>? Revs { get; set; }

    [JsonPropertyName("skip")] public int? Skip { get; set; }

    /// <summary>paging cursor: last commit hash from previous page; continues walking its parents</summary>
    [JsonPropertyName("after_commit")] public string? AfterCommit { get; set; }

    [JsonPropertyName("limit")] public int? Limit { get; set; } = 100;
}

public sealed class GitLogListResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }

    [JsonPropertyName("error")] public string? Error { get; set; }

    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }

    [JsonPropertyName("commits")] public List<GitCommitSummary> Commits { get; set; } = new();

    [JsonPropertyName("truncated")] public bool Truncated { get; set; }

    [JsonPropertyName("next_cursor")] public string? NextCursor { get; set; }

    /// <summary>reserved: future cache metadata, always null for now</summary>
    [JsonPropertyName("cache")] public object? Cache { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GitLogListArgs))]
[JsonSerializable(typeof(GitLogListResult))]
[JsonSerializable(typeof(GitCommitSummary))]
[JsonSerializable(typeof(GitGraphEdge))]
[JsonSerializable(typeof(GitRef))]
internal partial class GitLogListToolJsonContext : JsonSerializerContext { }

internal static class GitLogListTool
{
    public const string TOOL_ID = "git_log_list";

    private const string LogFormat = "%H%x1f%h%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cI%x1f%s%x1f%D";

    [ToolFunction(TOOL_ID, RequiresApproval = true)]
    public static async ValueTask<GitLogListResult> HandleAsync(
        GitLogListArgs args,
        CancellationToken cancellationToken)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var repoError);
        if (repo is null)
            return new GitLogListResult { Success = false, Error = repoError, ErrorCode = GitCli.NotARepoCode };

        var limit = args.Limit is > 0 ? args.Limit.Value : 100;
        var skip = args.Skip is > 0 ? args.Skip.Value : 0;

        // build revs
        List<string> revArgs;
        if (!string.IsNullOrWhiteSpace(args.AfterCommit))
        {
            GitCli.ValidateRevision(args.AfterCommit, "after_commit");
            // cursor continue: walk from all parents of after_commit
            revArgs = [$"{args.AfterCommit}^@"];
        }
        else if (args.Revs is { Count: > 0 } revs)
        {
            if (HasOptionInjection(revs))
                throw new InvalidOperationException("revs must not contain git options (tokens starting with '-').");
            revArgs = [.. revs];
        }
        else
        {
            revArgs = ["HEAD"];
        }

        var gitArgs = new List<string>
        {
            "log", "-z", $"--format={LogFormat}", $"--max-count={limit + 1}"
        };
        if (skip > 0)
            gitArgs.Add($"--skip={skip}");
        gitArgs.AddRange(revArgs);

        var exec = await GitCli.RunAsync(repo, gitArgs, stdin: null, cancellationToken, timeoutMs: 30_000).ConfigureAwait(false);
        if (!exec.Ok)
            return Fail(exec);

        var commits = GitLogParser.ParseLog(exec.Stdout);
        var truncated = commits.Count > limit;
        if (truncated)
            commits = commits.Take(limit).ToList();

        LaneAssigner.Assign(commits);

        return new GitLogListResult
        {
            Success = true,
            Commits = commits,
            Truncated = truncated,
            NextCursor = truncated && commits.Count > 0 ? commits[^1].Hash : null
        };
    }

    private static bool HasOptionInjection(List<string> revs) =>
        revs.Any(r => string.IsNullOrWhiteSpace(r) || r.StartsWith("-", StringComparison.Ordinal));

    private static GitLogListResult Fail(GitExecResult exec) => new()
    {
        Success = false,
        Error = string.IsNullOrWhiteSpace(exec.Stderr) ? $"git exited {exec.ExitCode}" : exec.Stderr.Trim(),
        ErrorCode = exec.ExitCode < 0 ? GitCli.GitNotFoundCode : null
    };
}
