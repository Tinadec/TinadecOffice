using System.Text.Json.Serialization;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Git;

// ── git_log_detail ────────────────────────────────────────────────────────────
// Supports a single commit or a range (A..B).
// Default: commit metadata + file change list only.
// include_patch=true: also returns structured hunks.

public sealed class GitLogDetailArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }

    /// <summary>commit-ish or rev range, e.g. "HEAD", "abc123", "A..B"</summary>
    [JsonPropertyName("rev")] public string Rev { get; set; } = string.Empty;

    [JsonPropertyName("skip")] public int? Skip { get; set; }

    [JsonPropertyName("after_commit")] public string? AfterCommit { get; set; }

    [JsonPropertyName("limit")] public int? Limit { get; set; } = 100;

    [JsonPropertyName("include_patch")] public bool IncludePatch { get; set; } = false;

    [JsonPropertyName("max_files")] public int? MaxFiles { get; set; } = 200;

    [JsonPropertyName("max_patch_bytes")] public int? MaxPatchBytes { get; set; } = 262144;
}

public sealed class GitLogDetailResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }

    [JsonPropertyName("error")] public string? Error { get; set; }

    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }

    [JsonPropertyName("commits")] public List<GitCommitSummary> Commits { get; set; } = new();

    [JsonPropertyName("files")] public List<GitFileChange> Files { get; set; } = new();

    [JsonPropertyName("patches")] public List<GitPatchFile>? Patches { get; set; }

    [JsonPropertyName("truncated")] public bool Truncated { get; set; }

    [JsonPropertyName("truncation_reason")] public string? TruncationReason { get; set; }

    /// <summary>"single" | "first_parent" - only first-parent implemented; combined/per_parent reserved</summary>
    [JsonPropertyName("diff_mode")] public string DiffMode { get; set; } = "first_parent";

    [JsonPropertyName("next_cursor")] public string? NextCursor { get; set; }

    [JsonPropertyName("cache")] public object? Cache { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GitLogDetailArgs))]
[JsonSerializable(typeof(GitLogDetailResult))]
[JsonSerializable(typeof(GitCommitSummary))]
[JsonSerializable(typeof(GitGraphEdge))]
[JsonSerializable(typeof(GitRef))]
[JsonSerializable(typeof(GitFileChange))]
[JsonSerializable(typeof(GitPatchFile))]
[JsonSerializable(typeof(GitDiffHunk))]
[JsonSerializable(typeof(GitDiffLine))]
internal partial class GitLogDetailToolJsonContext : JsonSerializerContext;

internal static class GitLogDetailTool
{
    public const string TOOL_ID = "git_log_detail";

    private const string LogFormat = "%H%x1f%h%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cI%x1f%s%x1f%D";

    [ToolFunction(TOOL_ID, RequiresApproval = true)]
    public static async ValueTask<GitLogDetailResult> HandleAsync(
        GitLogDetailArgs args,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args.Rev))
            throw new InvalidOperationException("rev is required.");
        GitCli.ValidateRevision(args.Rev, "rev");
        if (!string.IsNullOrWhiteSpace(args.AfterCommit))
            GitCli.ValidateRevision(args.AfterCommit, "after_commit");

        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var repoError);
        if (repo is null)
            return new GitLogDetailResult { Success = false, Error = repoError, ErrorCode = GitCli.NotARepoCode };

        var maxFiles = args.MaxFiles is > 0 ? args.MaxFiles.Value : 200;
        var maxPatchBytes = args.MaxPatchBytes is > 0 ? args.MaxPatchBytes.Value : 262144;

        // single commit vs range
        var verify = await GitCli.RunAsync(
            repo,
            ["rev-parse", "--verify", $"{args.Rev}^{{commit}}"],
            stdin: null,
            cancellationToken,
            timeoutMs: 10_000).ConfigureAwait(false);

        if (verify.Ok)
        {
            return await SingleCommitAsync(repo, verify.Stdout.Trim(), args, maxFiles, maxPatchBytes, cancellationToken).ConfigureAwait(false);
        }

        return await RangeAsync(repo, args, maxFiles, maxPatchBytes, cancellationToken).ConfigureAwait(false);
    }

    // ── single commit ──────────────────────────────────────────────────────────

    private static async Task<GitLogDetailResult> SingleCommitAsync(
        string repo, string hash, GitLogDetailArgs args, int maxFiles, int maxPatchBytes, CancellationToken cancellationToken)
    {
        var meta = await GitCli.RunAsync(repo, ["log", "-1", $"--format={LogFormat}", hash], stdin: null, cancellationToken, timeoutMs: 10_000).ConfigureAwait(false);
        if (!meta.Ok)
            return Fail(meta);

        var commits = GitLogParser.ParseLog(meta.Stdout);
        LaneAssigner.Assign(commits);

        var ns = await GitCli.RunAsync(repo, ["diff-tree", "-r", "--root", "--name-status", "-M", hash], stdin: null, cancellationToken, timeoutMs: 15_000).ConfigureAwait(false);
        var stat = await GitCli.RunAsync(repo, ["diff-tree", "-r", "--root", "--numstat", "-M", hash], stdin: null, cancellationToken, timeoutMs: 15_000).ConfigureAwait(false);

        var nameStatus = DiffParser.ParseNameStatus(ns.Stdout);
        var numstat = DiffParser.ParseNumstat(stat.Stdout);
        var files = DiffParser.MergeFileChanges(nameStatus, numstat);

        var truncated = false;
        string? truncationReason = null;
        if (files.Count > maxFiles)
        {
            files = files.Take(maxFiles).ToList();
            truncated = true;
            truncationReason = "max_files";
        }

        // binary summaries
        await FillBinarySummariesAsync(repo, hash, files, cancellationToken).ConfigureAwait(false);

        List<GitPatchFile>? patches = null;
        if (args.IncludePatch && !truncated)
        {
            var patch = await GitPatchLoader.LoadAsync(repo, ["diff-tree", "-r", "--root", "--patch", "-M", hash], maxPatchBytes, cancellationToken).ConfigureAwait(false);
            if (patch.Patches is not null)
            {
                patches = patch.Patches;
                await FillPatchBinarySummariesAsync(repo, hash, patches, cancellationToken).ConfigureAwait(false);
            }
            else if (patch.Truncated)
            {
                truncated = true;
                truncationReason ??= patch.TruncationReason;
            }
        }

        return new GitLogDetailResult
        {
            Success = true,
            Commits = commits,
            Files = files,
            Patches = patches,
            Truncated = truncated,
            TruncationReason = truncationReason,
            DiffMode = "first_parent"
        };
    }

    // ── range ──────────────────────────────────────────────────────────────────

    private static async Task<GitLogDetailResult> RangeAsync(
        string repo, GitLogDetailArgs args, int maxFiles, int maxPatchBytes, CancellationToken cancellationToken)
    {
        var limit = args.Limit is > 0 ? args.Limit.Value : 100;
        var skip = args.Skip is > 0 ? args.Skip.Value : 0;

        List<string> revArgs;
        if (!string.IsNullOrWhiteSpace(args.AfterCommit))
            revArgs = [$"{args.AfterCommit}^@"];
        else
            revArgs = [args.Rev];

        var gitArgs = new List<string> { "log", "-z", $"--format={LogFormat}", $"--max-count={limit + 1}" };
        if (skip > 0)
            gitArgs.Add($"--skip={skip}");
        gitArgs.AddRange(revArgs);

        var meta = await GitCli.RunAsync(repo, gitArgs, stdin: null, cancellationToken, timeoutMs: 30_000).ConfigureAwait(false);
        if (!meta.Ok)
            return Fail(meta);

        var commits = GitLogParser.ParseLog(meta.Stdout);
        var truncated = commits.Count > limit;
        string? truncationReason = truncated ? "limit" : null;
        if (truncated)
            commits = commits.Take(limit).ToList();
        LaneAssigner.Assign(commits);

        // aggregated file changes: git diff <rev> --name-status / --numstat
        // ponytail: after_commit continue mode cannot express a diff range; files left empty then
        var headRev = ExtractRangeHead(args.Rev);
        var files = new List<GitFileChange>();
        List<GitPatchFile>? patches = null;

        if (string.IsNullOrWhiteSpace(args.AfterCommit) && !string.IsNullOrEmpty(headRev))
        {
            var baseRev = ExtractRangeBase(args.Rev);
            var diffRev = baseRev is not null ? $"{baseRev}..{headRev}" : headRev;
            var ns = await GitCli.RunAsync(repo, ["diff", "--name-status", "-M", diffRev], stdin: null, cancellationToken, timeoutMs: 15_000).ConfigureAwait(false);
            var stat = await GitCli.RunAsync(repo, ["diff", "--numstat", "-M", diffRev], stdin: null, cancellationToken, timeoutMs: 15_000).ConfigureAwait(false);
            var nameStatus = DiffParser.ParseNameStatus(ns.Stdout);
            var numstat = DiffParser.ParseNumstat(stat.Stdout);
            files = DiffParser.MergeFileChanges(nameStatus, numstat);

            if (files.Count > maxFiles)
            {
                files = files.Take(maxFiles).ToList();
                truncated = true;
                truncationReason ??= "max_files";
            }

            await FillBinarySummariesAsync(repo, headRev, files, cancellationToken).ConfigureAwait(false);

            if (args.IncludePatch && !truncated)
            {
                var patch = await GitPatchLoader.LoadAsync(repo, ["diff", "--patch", "-M", diffRev], maxPatchBytes, cancellationToken).ConfigureAwait(false);
                if (patch.Patches is not null)
                {
                    patches = patch.Patches;
                    await FillPatchBinarySummariesAsync(repo, headRev, patches, cancellationToken).ConfigureAwait(false);
                }
                else if (patch.Truncated)
                {
                    truncated = true;
                    truncationReason ??= patch.TruncationReason;
                }
            }
        }

        return new GitLogDetailResult
        {
            Success = true,
            Commits = commits,
            Files = files,
            Patches = patches,
            Truncated = truncated,
            TruncationReason = truncationReason,
            DiffMode = "first_parent",
            NextCursor = truncated && commits.Count > 0 ? commits[^1].Hash : null
        };
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static async Task FillBinarySummariesAsync(string repo, string rev, List<GitFileChange> files, CancellationToken cancellationToken)
    {
        foreach (var f in files)
        {
            if (!f.IsBinary)
                continue;
            var path = !string.IsNullOrEmpty(f.NewPath) ? f.NewPath : f.OldPath;
            if (string.IsNullOrEmpty(path))
                continue;
            var (blobHash, byteSize) = await GitCli.GetBlobSummaryAsync(repo, rev, path!, cancellationToken).ConfigureAwait(false);
            f.BlobHash = blobHash;
            f.ByteSize = byteSize;
        }
    }

    private static async Task FillPatchBinarySummariesAsync(string repo, string rev, List<GitPatchFile> patches, CancellationToken cancellationToken)
    {
        foreach (var p in patches)
        {
            if (!p.IsBinary)
                continue;
            var path = !string.IsNullOrEmpty(p.NewPath) ? p.NewPath : p.OldPath;
            if (string.IsNullOrEmpty(path))
                continue;
            var (blobHash, byteSize) = await GitCli.GetBlobSummaryAsync(repo, rev, path!, cancellationToken).ConfigureAwait(false);
            p.BlobHash = blobHash;
            p.ByteSize = byteSize;
        }
    }

    private static string? ExtractRangeHead(string rev)
    {
        var idx = rev.IndexOf("..", StringComparison.Ordinal);
        if (idx < 0)
            return rev;
        return rev[(idx + 2)..];
    }

    private static string? ExtractRangeBase(string rev)
    {
        var idx = rev.IndexOf("..", StringComparison.Ordinal);
        if (idx < 0)
            return null;
        return rev[..idx];
    }

    private static GitLogDetailResult Fail(GitExecResult exec) => new()
    {
        Success = false,
        Error = string.IsNullOrWhiteSpace(exec.Stderr) ? $"git exited {exec.ExitCode}" : exec.Stderr.Trim(),
        ErrorCode = exec.ExitCode < 0 ? GitCli.GitNotFoundCode : null
    };
}
