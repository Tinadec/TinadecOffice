using System.Text.Json.Serialization;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Git;

// ── git_file_history ──────────────────────────────────────────────────────────
// Single file history, --follow tracks renames by default.
// Reads `git log --follow --name-status` for stable per-commit metadata, then
// requests individual patches only while the caller's output budget remains.

public sealed class GitFileHistoryEntry
{
    [JsonPropertyName("commit")] public GitCommitSummary Commit { get; set; } = new();

    [JsonPropertyName("change")] public GitFileChange Change { get; set; } = new();

    [JsonPropertyName("patch")] public GitPatchFile? Patch { get; set; }
}

public sealed class GitFileHistoryArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }

    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;

    [JsonPropertyName("follow")] public bool? Follow { get; set; } = true;

    [JsonPropertyName("include_patch")] public bool IncludePatch { get; set; } = false;

    [JsonPropertyName("skip")] public int? Skip { get; set; }

    [JsonPropertyName("after_commit")] public string? AfterCommit { get; set; }

    [JsonPropertyName("limit")] public int? Limit { get; set; } = 100;

    [JsonPropertyName("max_patch_bytes")] public int? MaxPatchBytes { get; set; } = 524288;
}

public sealed class GitFileHistoryResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }

    [JsonPropertyName("error")] public string? Error { get; set; }

    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }

    [JsonPropertyName("entries")] public List<GitFileHistoryEntry> Entries { get; set; } = new();

    [JsonPropertyName("truncated")] public bool Truncated { get; set; }

    [JsonPropertyName("truncation_reason")] public string? TruncationReason { get; set; }

    [JsonPropertyName("next_cursor")] public string? NextCursor { get; set; }

    [JsonPropertyName("cache")] public object? Cache { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GitFileHistoryArgs))]
[JsonSerializable(typeof(GitFileHistoryResult))]
[JsonSerializable(typeof(GitFileHistoryEntry))]
[JsonSerializable(typeof(GitCommitSummary))]
[JsonSerializable(typeof(GitGraphEdge))]
[JsonSerializable(typeof(GitRef))]
[JsonSerializable(typeof(GitFileChange))]
[JsonSerializable(typeof(GitPatchFile))]
[JsonSerializable(typeof(GitDiffHunk))]
[JsonSerializable(typeof(GitDiffLine))]
internal partial class GitFileHistoryToolJsonContext : JsonSerializerContext { }

internal static class GitFileHistoryTool
{
    public const string TOOL_ID = "git_file_history";

    private const string CommitMarker = "__TINADEC_COMMIT__\x1f";
    private const string LogFormat = CommitMarker + "%H%x1f%h%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cI%x1f%s%x1f%D";

    [ToolFunction(TOOL_ID, RequiresApproval = true)]
    public static async ValueTask<GitFileHistoryResult> HandleAsync(
        GitFileHistoryArgs args,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            throw new InvalidOperationException("path is required.");

        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var repoError);
        if (repo is null)
            return new GitFileHistoryResult { Success = false, Error = repoError, ErrorCode = GitCli.NotARepoCode };

        var repositoryPath = GitCli.ResolveRepositoryRelativePath(repo, args.Path);
        if (!string.IsNullOrWhiteSpace(args.AfterCommit))
            GitCli.ValidateRevision(args.AfterCommit, "after_commit");

        var limit = args.Limit is > 0 ? args.Limit.Value : 100;
        var skip = args.Skip is > 0 ? args.Skip.Value : 0;
        var follow = args.Follow ?? true;
        var maxPatchBytes = args.MaxPatchBytes is > 0 ? args.MaxPatchBytes.Value : 524288;

        var gitArgs = new List<string> { "log", "-z", "--name-status", "-M" };
        if (follow)
            gitArgs.Add("--follow");
        gitArgs.Add($"--format={LogFormat}");
        gitArgs.Add($"--max-count={limit + 1}");
        if (skip > 0)
            gitArgs.Add($"--skip={skip}");
        if (!string.IsNullOrWhiteSpace(args.AfterCommit))
            gitArgs.Add($"{args.AfterCommit}^@");
        gitArgs.Add("--");
        gitArgs.Add(repositoryPath);

        var exec = await GitCli.RunAsync(repo, gitArgs, stdin: null, cancellationToken, timeoutMs: 60_000).ConfigureAwait(false);
        if (!exec.Ok)
            return Fail(exec);

        var truncated = false;
        string? truncationReason = null;
        var segments = exec.Stdout.Split(CommitMarker, StringSplitOptions.RemoveEmptyEntries);

        var entries = new List<GitFileHistoryEntry>();
        foreach (var seg in segments)
        {
            var records = seg.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (records.Length == 0)
                continue;

            var fields = records[0].TrimStart('\r', '\n').Split('\x1f');
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
                Refs = GitLogParser.ParseDecorations(fields[8])
            };

            var change = ParseChange(records.Skip(1).ToArray(), repositoryPath);

            entries.Add(new GitFileHistoryEntry
            {
                Commit = commit,
                Change = change,
                Patch = null
            });
        }

        LaneAssigner.Assign(entries.Select(e => e.Commit).ToList());

        var overflow = entries.Count > limit;
        if (overflow)
        {
            entries = entries.Take(limit).ToList();
            truncated = true;
            truncationReason = "limit";
        }

        var usedPatchBytes = 0;
        foreach (var entry in entries)
        {
            var path = !string.IsNullOrEmpty(entry.Change.NewPath) ? entry.Change.NewPath : entry.Change.OldPath;
            if (string.IsNullOrEmpty(path))
                continue;

            var stat = await GitCli.RunAsync(
                repo,
                ["show", "--format=", "--numstat", "-M", entry.Commit.Hash, "--", path],
                stdin: null,
                cancellationToken,
                timeoutMs: 15_000).ConfigureAwait(false);
            if (stat.Ok)
            {
                var numstat = DiffParser.ParseNumstat(stat.Stdout).FirstOrDefault();
                entry.Change.Additions = numstat.Additions;
                entry.Change.Deletions = numstat.Deletions;
                entry.Change.IsBinary = numstat.IsBinary;
            }

            if (entry.Change.IsBinary)
            {
                var (blobHash, byteSize) = await GitCli.GetBlobSummaryAsync(repo, entry.Commit.Hash, path, cancellationToken).ConfigureAwait(false);
                entry.Change.BlobHash = blobHash;
                entry.Change.ByteSize = byteSize;
            }

            if (!args.IncludePatch || truncated)
                continue;

            var remaining = maxPatchBytes - usedPatchBytes;
            if (remaining <= 0)
            {
                truncated = true;
                truncationReason ??= "patch_output_limit";
                break;
            }

            var patch = await GitPatchLoader.LoadAsync(
                repo,
                ["show", "--format=", "--patch", "-M", entry.Commit.Hash, "--", path],
                remaining,
                cancellationToken).ConfigureAwait(false);
            if (patch.Truncated)
            {
                truncated = true;
                truncationReason ??= patch.TruncationReason;
                break;
            }

            if (patch.Patches is { Count: > 0 })
            {
                var patchFile = patch.Patches[0];
                if (entry.Change.IsBinary)
                {
                    patchFile.BlobHash = entry.Change.BlobHash;
                    patchFile.ByteSize = entry.Change.ByteSize;
                }
                entry.Patch = patchFile;
                usedPatchBytes += patch.CapturedBytes;
            }
        }

        return new GitFileHistoryResult
        {
            Success = true,
            Entries = entries,
            Truncated = truncated,
            TruncationReason = truncationReason,
            NextCursor = truncated && entries.Count > 0 ? entries[^1].Commit.Hash : null
        };
    }

    private static GitFileChange ParseChange(string[] records, string fallbackPath)
    {
        if (records.Length == 0)
            return new GitFileChange { Status = "M", OldPath = fallbackPath, NewPath = fallbackPath };

        var statusParts = records[0].Split('\t', 2);
        var statusField = statusParts[0];
        var status = statusField[..1];
        var score = status is "R" or "C" && int.TryParse(statusField[1..], out var parsedScore) ? parsedScore : 0;
        var firstPath = statusParts.Length > 1 ? statusParts[1] : records.Length > 1 ? records[1] : fallbackPath;
        var secondPath = status is "R" or "C" && statusParts.Length == 1 && records.Length > 2 ? records[2] : firstPath;

        var (oldPath, newPath) = status switch
        {
            "A" => ((string?)null, firstPath),
            "D" => (firstPath, string.Empty),
            "R" or "C" => (firstPath, secondPath),
            _ => (firstPath, firstPath)
        };

        return new GitFileChange
        {
            Status = status,
            Score = score,
            OldPath = oldPath,
            NewPath = newPath
        };
    }

    private static GitFileHistoryResult Fail(GitExecResult exec) => new()
    {
        Success = false,
        Error = string.IsNullOrWhiteSpace(exec.Stderr) ? $"git exited {exec.ExitCode}" : exec.Stderr.Trim(),
        ErrorCode = exec.ExitCode < 0 ? GitCli.GitNotFoundCode : null
    };
}
