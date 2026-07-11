using System.Text.Json.Serialization;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Git;

// ── git_file_history ──────────────────────────────────────────────────────────
// Single file history, --follow tracks renames by default.
// Uses `git log --follow -p` once to get commit metadata + the file's patch,
// deriving file change (status/binary/counts) from the patch.

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
internal partial class GitFileHistoryToolJsonContext : JsonSerializerContext;

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

        var limit = args.Limit is > 0 ? args.Limit.Value : 100;
        var skip = args.Skip is > 0 ? args.Skip.Value : 0;
        var follow = args.Follow ?? true;
        var maxPatchBytes = args.MaxPatchBytes is > 0 ? args.MaxPatchBytes.Value : 524288;

        var gitArgs = new List<string> { "log", "-p" };
        if (follow)
            gitArgs.Add("--follow");
        gitArgs.Add($"--format={LogFormat}");
        gitArgs.Add($"--max-count={limit + 1}");
        if (skip > 0)
            gitArgs.Add($"--skip={skip}");
        if (!string.IsNullOrWhiteSpace(args.AfterCommit))
            gitArgs.Add($"{args.AfterCommit}^@");
        gitArgs.Add("--");
        gitArgs.Add(args.Path);

        var exec = await GitCli.RunAsync(repo, gitArgs, stdin: null, cancellationToken, timeoutMs: 60_000).ConfigureAwait(false);
        if (!exec.Ok)
            return Fail(exec);

        // ponytail: coarse total-size cap; per-commit byte limits would need per-commit parsing
        var truncated = exec.Stdout.Length > maxPatchBytes * 4;
        var segments = exec.Stdout.Split(CommitMarker, StringSplitOptions.RemoveEmptyEntries);

        var entries = new List<GitFileHistoryEntry>();
        foreach (var seg in segments)
        {
            var nlIdx = seg.IndexOf('\n');
            var fieldsLine = nlIdx < 0 ? seg : seg[..nlIdx];
            var patchText = nlIdx < 0 ? string.Empty : seg[(nlIdx + 1)..];

            var fields = fieldsLine.Split('\x1f');
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

            var patchFiles = DiffParser.ParsePatch(patchText);
            var pf = patchFiles.Count > 0 ? patchFiles[0] : new GitPatchFile { NewPath = args.Path, OldPath = args.Path };
            var change = DeriveFileChange(pf);

            // binary summary
            if (change.IsBinary)
            {
                var bp = !string.IsNullOrEmpty(pf.NewPath) ? pf.NewPath : pf.OldPath;
                if (!string.IsNullOrEmpty(bp))
                {
                    var (blobHash, byteSize) = await GitCli.GetBlobSummaryAsync(repo, commit.Hash, bp, cancellationToken).ConfigureAwait(false);
                    change.BlobHash = blobHash;
                    change.ByteSize = byteSize;
                    pf.BlobHash = blobHash;
                    pf.ByteSize = byteSize;
                }
            }

            entries.Add(new GitFileHistoryEntry
            {
                Commit = commit,
                Change = change,
                Patch = args.IncludePatch ? pf : null
            });
        }

        LaneAssigner.Assign(entries.Select(e => e.Commit).ToList());

        var overflow = entries.Count > limit;
        if (overflow)
            entries = entries.Take(limit).ToList();

        return new GitFileHistoryResult
        {
            Success = true,
            Entries = entries,
            Truncated = truncated || overflow,
            NextCursor = (truncated || overflow) && entries.Count > 0 ? entries[^1].Commit.Hash : null
        };
    }

    private static GitFileChange DeriveFileChange(GitPatchFile pf)
    {
        var oldEmpty = string.IsNullOrEmpty(pf.OldPath);
        var newEmpty = string.IsNullOrEmpty(pf.NewPath);
        var renamed = !oldEmpty && !newEmpty && !string.Equals(pf.OldPath, pf.NewPath, StringComparison.Ordinal);

        var status = (oldEmpty, newEmpty, renamed) switch
        {
            (true, false, _) => "A",
            (false, true, _) => "D",
            (_, _, true) => "R",
            _ => "M"
        };

        var additions = pf.IsBinary ? 0 : pf.Hunks.Sum(h => h.Lines.Count(l => l.Type == "add"));
        var deletions = pf.IsBinary ? 0 : pf.Hunks.Sum(h => h.Lines.Count(l => l.Type == "delete"));

        return new GitFileChange
        {
            Status = status,
            Score = 0, // ponytail: -p has no similarity score; needs --name-status, skipped
            OldPath = oldEmpty ? null : pf.OldPath,
            NewPath = pf.NewPath,
            Additions = additions,
            Deletions = deletions,
            IsBinary = pf.IsBinary
        };
    }

    private static GitFileHistoryResult Fail(GitExecResult exec) => new()
    {
        Success = false,
        Error = string.IsNullOrWhiteSpace(exec.Stderr) ? $"git exited {exec.ExitCode}" : exec.Stderr.Trim(),
        ErrorCode = exec.ExitCode < 0 ? GitCli.GitNotFoundCode : null
    };
}
